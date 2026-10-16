//! Fixed-size pages: constants, wire layout, and order-preserving packing shared with the collector.
//!
//! Every POST to `/telemetry` is exactly one page on the wire. The plaintext layout is
//! `[uint16-BE body_length][CBOR body][0x00 padding to PLAINTEXT_LENGTH]`. The CBOR body is a
//! single map:
//!
//! ```cbor
//! {
//!   "auths":  [<jwt>, ...],         // deduped, insertion-ordered token table
//!   "events": [
//!     [<auth_idx>, <record>],       // authed: 2-element array, index into "auths"
//!     [<record>],                   // unauthed: 1-element array
//!     ...
//!   ]
//! }
//! ```
//!
//! `events` is written in strict submission order. Same-token authed records share one entry in
//! `auths` so the ~700-byte JWT is written once per user per page. See
//! `src/Telemetee.Telemetry/Pages/PageFormat.cs` for the collector-side counterpart.
//!
//! Packing is caller-driven: [`pack_page`] drains submissions from the front of a queue, filling
//! one page per call. Records that would overflow the page stay in the queue for the next call;
//! records whose standalone encoding exceeds [`MAX_RECORD_LENGTH`] are siphoned into a dropped
//! sink.

use serde::Serialize;
use telemetee_api::models::TelemetryRecord;

use crate::Error;

/// Length of one NK message 1 on the wire, including the ephemeral public key and the AEAD tag.
pub const PAGE_LENGTH: usize = 4096;

/// Length of one NK message 2 on the wire. The collector always sends an empty payload.
pub const RESPONSE_LENGTH: usize = 65 + 16;

/// Plaintext bytes inside one page: `PAGE_LENGTH` minus the 65-byte ephemeral public key and the
/// 16-byte AEAD tag.
pub const PLAINTEXT_LENGTH: usize = PAGE_LENGTH - 65 - 16;

/// Bytes used by the uint16-BE body-length prefix at the start of the plaintext.
pub const BODY_LENGTH_PREFIX_BYTES: usize = 2;

/// Maximum length of the CBOR body in one plaintext page.
pub const MAX_BODY_LENGTH: usize = PLAINTEXT_LENGTH - BODY_LENGTH_PREFIX_BYTES;

/// Advisory per-event cap used by the packer. The collector does not enforce a per-event limit,
/// only `MAX_BODY_LENGTH` across the whole page; this keeps a single pathological event from
/// filling an entire page by itself.
pub const MAX_RECORD_LENGTH: usize = 1024;

/// One record ready to submit: either authenticated under a JWT the collector will verify, or
/// anonymous. The collector requires that the record's schema and its (un)authed placement agree:
/// a schema declaring any `[Authed]` field must arrive authed, and vice versa.
#[derive(Debug, Clone)]
pub enum Submission {
    /// Authenticated record. `token` is the JWT the collector will parse (and, from Phase B,
    /// verify) to populate every `[Authed]` field on the event's schema.
    Authed {
        /// Opaque JWT string. The client never inspects it; the collector does.
        token: String,
        /// The event payload itself.
        event: TelemetryRecord,
    },
    /// Anonymous record. The event's schema must declare no `[Authed]` fields.
    Unauthed(TelemetryRecord),
}

/// Packs the front of `queue` into one page's worth of CBOR body, drains the records that made it
/// (or were dropped as oversize) from `queue`, and encodes them to the fixed page layout.
///
/// Ordering: submissions are considered in input order. The first record that does not fit on the
/// current page stays at the front of `queue` for the next call, so a caller that invokes
/// `pack_page` in a loop sees records flow to the wire in the same order they entered the queue.
///
/// Oversize handling: any submission whose standalone CBOR encoding exceeds [`MAX_RECORD_LENGTH`]
/// is removed from `queue` and appended to `oversize_dropped`. It is never attempted on any page
/// because it cannot fit by definition; the caller gets the record back so it can log or inspect
/// the offending schema.
///
/// Returns:
/// - `Ok(Some((page, packed)))` when at least one record was packed. `packed` is the Submissions
///   that landed on this page, in order; the caller holds onto them so the records can be
///   restored to the queue if the subsequent page send fails.
/// - `Ok(None)` when `queue` was exhausted (or only contained oversize records that were all
///   siphoned off).
/// - `Err(Error::Protocol(_))` when the front record cannot fit on an empty page even by itself
///   (for example, an authed submission whose JWT is pathologically large). The queue is left
///   with the stuck record at the front so the caller can inspect or drop it.
pub(crate) fn pack_page(
    queue: &mut Vec<Submission>,
    oversize_dropped: &mut Vec<Submission>,
) -> Result<Option<([u8; PLAINTEXT_LENGTH], Vec<Submission>)>, Error> {
    let mut auths: Vec<String> = Vec::new();
    let mut events: Vec<PackedEvent> = Vec::new();
    let mut cursor = 0usize;
    let mut oversize_positions: Vec<usize> = Vec::new();

    while cursor < queue.len() {
        let sub = &queue[cursor];
        if event_too_big(sub.record())? {
            oversize_positions.push(cursor);
            cursor += 1;
            continue;
        }

        let (next_auths, next_events) = speculatively_append(&auths, &events, sub);
        if body_fits(&next_auths, &next_events)? {
            auths = next_auths;
            events = next_events;
            cursor += 1;
        } else {
            break;
        }
    }

    // Progress guarantee: if the queue is non-empty but no record (fit or oversize) was consumed,
    // the front record is stuck. Return an error so the caller can inspect or discard it rather
    // than looping forever.
    if cursor == 0 && !queue.is_empty() {
        return Err(Error::Protocol(
            "front record is too large to fit on an empty page".into(),
        ));
    }

    let drained: Vec<Submission> = queue.drain(..cursor).collect();
    let mut packed: Vec<Submission> = Vec::with_capacity(drained.len() - oversize_positions.len());
    for (i, sub) in drained.into_iter().enumerate() {
        if oversize_positions.binary_search(&i).is_ok() {
            oversize_dropped.push(sub);
        } else {
            packed.push(sub);
        }
    }

    if events.is_empty() {
        return Ok(None);
    }
    Ok(Some((encode_page(&auths, &events)?, packed)))
}

impl Submission {
    fn record(&self) -> &TelemetryRecord {
        match self {
            Submission::Authed { event, .. } => event,
            Submission::Unauthed(event) => event,
        }
    }
}

#[derive(Clone)]
enum PackedEvent {
    Authed { auth_idx: usize, record: TelemetryRecord },
    Unauthed(TelemetryRecord),
}

fn speculatively_append(
    auths: &[String],
    events: &[PackedEvent],
    sub: &Submission,
) -> (Vec<String>, Vec<PackedEvent>) {
    let mut next_auths = auths.to_vec();
    let mut next_events = events.to_vec();
    match sub {
        Submission::Authed { token, event } => {
            let idx = next_auths
                .iter()
                .position(|t| t == token)
                .unwrap_or_else(|| {
                    next_auths.push(token.clone());
                    next_auths.len() - 1
                });
            next_events.push(PackedEvent::Authed {
                auth_idx: idx,
                record: event.clone(),
            });
        }
        Submission::Unauthed(event) => {
            next_events.push(PackedEvent::Unauthed(event.clone()));
        }
    }
    (next_auths, next_events)
}

fn event_too_big(event: &TelemetryRecord) -> Result<bool, Error> {
    let mut buf = Vec::new();
    ciborium::ser::into_writer(event, &mut buf)
        .map_err(|e| Error::Protocol(format!("CBOR encode failed: {e}")))?;
    Ok(buf.len() > MAX_RECORD_LENGTH)
}

#[derive(Serialize)]
struct PageBody<'a> {
    auths: &'a [String],
    events: Vec<EventSer<'a>>,
}

enum EventSer<'a> {
    Authed { auth_idx: usize, record: &'a TelemetryRecord },
    Unauthed { record: &'a TelemetryRecord },
}

impl serde::Serialize for EventSer<'_> {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        use serde::ser::SerializeSeq;
        match self {
            EventSer::Authed { auth_idx, record } => {
                let mut seq = serializer.serialize_seq(Some(2))?;
                seq.serialize_element(auth_idx)?;
                seq.serialize_element(record)?;
                seq.end()
            }
            EventSer::Unauthed { record } => {
                let mut seq = serializer.serialize_seq(Some(1))?;
                seq.serialize_element(record)?;
                seq.end()
            }
        }
    }
}

fn encode_body(auths: &[String], events: &[PackedEvent]) -> Result<Vec<u8>, Error> {
    let events_ser: Vec<EventSer<'_>> = events
        .iter()
        .map(|e| match e {
            PackedEvent::Authed { auth_idx, record } => EventSer::Authed {
                auth_idx: *auth_idx,
                record,
            },
            PackedEvent::Unauthed(record) => EventSer::Unauthed { record },
        })
        .collect();
    let body = PageBody {
        auths,
        events: events_ser,
    };
    let mut buf = Vec::new();
    ciborium::ser::into_writer(&body, &mut buf)
        .map_err(|e| Error::Protocol(format!("CBOR encode failed: {e}")))?;
    Ok(buf)
}

fn body_fits(auths: &[String], events: &[PackedEvent]) -> Result<bool, Error> {
    Ok(encode_body(auths, events)?.len() <= MAX_BODY_LENGTH)
}

fn encode_page(
    auths: &[String],
    events: &[PackedEvent],
) -> Result<[u8; PLAINTEXT_LENGTH], Error> {
    let body = encode_body(auths, events)?;
    debug_assert!(body.len() <= MAX_BODY_LENGTH);
    let mut page = [0u8; PLAINTEXT_LENGTH];
    page[..BODY_LENGTH_PREFIX_BYTES].copy_from_slice(&(body.len() as u16).to_be_bytes());
    page[BODY_LENGTH_PREFIX_BYTES..BODY_LENGTH_PREFIX_BYTES + body.len()].copy_from_slice(&body);
    Ok(page)
}

#[cfg(test)]
mod tests {
    use super::*;
    use telemetee_api::models::{FillAssistRuleOutcome, TelemetryRecord};

    fn fill() -> TelemetryRecord {
        TelemetryRecord::FillAssistRuleOutcome(Box::new(FillAssistRuleOutcome {
            schema: None,
            rule_id: "r1".into(),
            success: true,
            domain: "a.example".into(),
            client_version_major: 2025,
        }))
    }

    fn decode_body(page: &[u8; PLAINTEXT_LENGTH]) -> ciborium::value::Value {
        let len = u16::from_be_bytes([page[0], page[1]]) as usize;
        let body = &page[BODY_LENGTH_PREFIX_BYTES..BODY_LENGTH_PREFIX_BYTES + len];
        ciborium::de::from_reader(body).expect("decode")
    }

    fn get_field<'a>(map: &'a ciborium::value::Value, key: &str) -> &'a ciborium::value::Value {
        if let ciborium::value::Value::Map(entries) = map {
            for (k, v) in entries {
                if matches!(k, ciborium::value::Value::Text(s) if s == key) {
                    return v;
                }
            }
        }
        panic!("missing key {key}");
    }

    #[test]
    fn empty_queue_produces_no_page() {
        let mut queue: Vec<Submission> = Vec::new();
        let mut dropped: Vec<Submission> = Vec::new();
        assert!(pack_page(&mut queue, &mut dropped).expect("pack").is_none());
        assert!(dropped.is_empty());
    }

    #[test]
    fn preserves_submission_order_across_auth_boundaries() {
        let token = "a.b.c".to_string();
        let mut queue = vec![
            Submission::Unauthed(fill()),
            Submission::Authed { token: token.clone(), event: fill() },
            Submission::Unauthed(fill()),
            Submission::Authed { token: token.clone(), event: fill() },
        ];
        let mut dropped: Vec<Submission> = Vec::new();
        let (page, packed) = pack_page(&mut queue, &mut dropped).expect("pack").expect("page");
        assert!(queue.is_empty());
        assert!(dropped.is_empty());
        assert_eq!(packed.len(), 4);

        let body = decode_body(&page);
        let events = get_field(&body, "events");
        let ciborium::value::Value::Array(evs) = events else {
            panic!("events not array");
        };
        assert_eq!(evs.len(), 4);
        // 1-element = unauthed, 2-element = authed. Check pattern U, A, U, A.
        let lengths: Vec<usize> = evs
            .iter()
            .map(|e| {
                if let ciborium::value::Value::Array(a) = e { a.len() } else { panic!("not array") }
            })
            .collect();
        assert_eq!(lengths, vec![1, 2, 1, 2]);
    }

    #[test]
    fn same_token_collapses_to_one_auths_entry() {
        let token = "a.b.c".to_string();
        let mut queue: Vec<Submission> = (0..4)
            .map(|_| Submission::Authed { token: token.clone(), event: fill() })
            .collect();
        let mut dropped: Vec<Submission> = Vec::new();
        let (page, packed) = pack_page(&mut queue, &mut dropped).expect("pack").expect("page");
        assert!(queue.is_empty());
        assert_eq!(packed.len(), 4);

        let body = decode_body(&page);
        let auths = get_field(&body, "auths");
        let ciborium::value::Value::Array(a) = auths else { panic!("auths not array") };
        assert_eq!(a.len(), 1);

        let events = get_field(&body, "events");
        let ciborium::value::Value::Array(evs) = events else { panic!("events not array") };
        assert_eq!(evs.len(), 4);
        for ev in evs {
            if let ciborium::value::Value::Array(a) = ev {
                assert_eq!(a.len(), 2);
                assert!(matches!(a[0], ciborium::value::Value::Integer(_)));
            }
        }
    }

    #[test]
    fn overflow_leaves_remaining_records_at_front_of_queue() {
        // Enough records to force more than one page.
        let mut queue: Vec<Submission> = (0..200).map(|_| Submission::Unauthed(fill())).collect();
        let total = queue.len();
        let mut dropped: Vec<Submission> = Vec::new();

        let (page, packed) = pack_page(&mut queue, &mut dropped).expect("pack").expect("page");
        assert!(dropped.is_empty());
        // Some records went on the page, some remain in the queue.
        assert!(!queue.is_empty());
        assert!(queue.len() < total);
        assert_eq!(packed.len() + queue.len(), total);

        let body = decode_body(&page);
        let events = get_field(&body, "events");
        let ciborium::value::Value::Array(evs) = events else { panic!() };
        assert_eq!(evs.len(), packed.len());
    }

    #[test]
    fn oversize_record_is_siphoned_and_order_preserved() {
        let huge = "x".repeat(MAX_RECORD_LENGTH + 1);
        let oversize = TelemetryRecord::FillAssistRuleOutcome(Box::new(FillAssistRuleOutcome {
            schema: None,
            rule_id: huge,
            success: true,
            domain: "a.example".into(),
            client_version_major: 2025,
        }));
        let mut queue = vec![
            Submission::Unauthed(fill()),
            Submission::Unauthed(oversize),
            Submission::Unauthed(fill()),
        ];
        let mut dropped: Vec<Submission> = Vec::new();
        let (page, packed) = pack_page(&mut queue, &mut dropped).expect("pack").expect("page");
        assert!(queue.is_empty());
        assert_eq!(dropped.len(), 1);
        assert_eq!(packed.len(), 2);

        let body = decode_body(&page);
        let events = get_field(&body, "events");
        let ciborium::value::Value::Array(evs) = events else { panic!() };
        assert_eq!(evs.len(), 2);
    }

    #[test]
    fn loop_over_pack_page_drains_queue_in_order() {
        let mut queue: Vec<Submission> = (0..200).map(|_| Submission::Unauthed(fill())).collect();
        let total = queue.len();
        let mut dropped: Vec<Submission> = Vec::new();
        let mut pages = Vec::new();
        while let Some((page, _)) = pack_page(&mut queue, &mut dropped).expect("pack") {
            pages.push(page);
        }
        assert!(queue.is_empty());
        assert!(dropped.is_empty());
        assert!(pages.len() >= 2);
        let packed_total: usize = pages
            .iter()
            .map(|p| {
                let body = decode_body(p);
                let ciborium::value::Value::Array(a) = get_field(&body, "events") else { panic!() };
                a.len()
            })
            .sum();
        assert_eq!(packed_total, total);
    }
}
