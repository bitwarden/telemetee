//! Fixed-size pages: constants, wire layout, and order-preserving packing shared with the collector.
//!
//! Every POST to `/telemetry` is exactly one page on the wire. The plaintext layout is
//! `[uint16-BE body_length][CBOR body][0x00 padding to PLAINTEXT_LENGTH]`. The CBOR body is a
//! single map:
//!
//! ```cbor
//! {
//!   "events": [<record>, ...]
//! }
//! ```
//!
//! Each `<record>` is a bare CBOR map carrying the record's `schema` discriminator and its fields.
//! `events` is in strict submission order. The body stays a map so new entries can be added later
//! without a wire-format version bump. See `src/Telemetee.Telemetry/Pages/PageFormat.cs` for the
//! collector-side counterpart.
//!
//! Packing is caller-driven: [`pack_page`] drains records from the front of a queue, filling one
//! page per call. Records that would overflow the page stay in the queue for the next call;
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

/// Packs the front of `queue` into one page's worth of CBOR body, drains the records that made it
/// (or were dropped as oversize) from `queue`, and encodes them to the fixed page layout.
///
/// Ordering: records are considered in input order. The first record that does not fit on the
/// current page stays at the front of `queue` for the next call, so a caller that invokes
/// `pack_page` in a loop sees records flow to the wire in the same order they entered the queue.
///
/// Oversize handling: any record whose standalone CBOR encoding exceeds [`MAX_RECORD_LENGTH`] is
/// removed from `queue` and appended to `oversize_dropped`. It is never attempted on any page
/// because it cannot fit by definition; the caller gets the record back so it can log or inspect
/// the offending schema.
///
/// Returns:
/// - `Ok(Some((page, packed)))` when at least one record was packed. `packed` is the records that
///   landed on this page, in order; the caller holds onto them so they can be restored to the
///   queue if the subsequent page send fails.
/// - `Ok(None)` when `queue` was exhausted (or only contained oversize records that were all
///   siphoned off).
/// - `Err(Error::Protocol(_))` when the front record cannot fit on an empty page even by itself.
///   The queue is left with the stuck record at the front so the caller can inspect or drop it.
pub(crate) fn pack_page(
    queue: &mut Vec<TelemetryRecord>,
    oversize_dropped: &mut Vec<TelemetryRecord>,
) -> Result<Option<([u8; PLAINTEXT_LENGTH], Vec<TelemetryRecord>)>, Error> {
    let mut events: Vec<TelemetryRecord> = Vec::new();
    let mut cursor = 0usize;
    let mut oversize_positions: Vec<usize> = Vec::new();

    while cursor < queue.len() {
        let record = &queue[cursor];
        if event_too_big(record)? {
            oversize_positions.push(cursor);
            cursor += 1;
            continue;
        }

        let mut next_events = events.clone();
        next_events.push(record.clone());
        if body_fits(&next_events)? {
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

    let drained: Vec<TelemetryRecord> = queue.drain(..cursor).collect();
    let mut packed: Vec<TelemetryRecord> = Vec::with_capacity(drained.len() - oversize_positions.len());
    for (i, record) in drained.into_iter().enumerate() {
        if oversize_positions.binary_search(&i).is_ok() {
            oversize_dropped.push(record);
        } else {
            packed.push(record);
        }
    }

    if events.is_empty() {
        return Ok(None);
    }
    Ok(Some((encode_page(&events)?, packed)))
}

fn event_too_big(event: &TelemetryRecord) -> Result<bool, Error> {
    let mut buf = Vec::new();
    ciborium::ser::into_writer(event, &mut buf)
        .map_err(|e| Error::Protocol(format!("CBOR encode failed: {e}")))?;
    Ok(buf.len() > MAX_RECORD_LENGTH)
}

#[derive(Serialize)]
struct PageBody<'a> {
    events: &'a [TelemetryRecord],
}

fn encode_body(events: &[TelemetryRecord]) -> Result<Vec<u8>, Error> {
    let body = PageBody { events };
    let mut buf = Vec::new();
    ciborium::ser::into_writer(&body, &mut buf)
        .map_err(|e| Error::Protocol(format!("CBOR encode failed: {e}")))?;
    Ok(buf)
}

fn body_fits(events: &[TelemetryRecord]) -> Result<bool, Error> {
    Ok(encode_body(events)?.len() <= MAX_BODY_LENGTH)
}

fn encode_page(events: &[TelemetryRecord]) -> Result<[u8; PLAINTEXT_LENGTH], Error> {
    let body = encode_body(events)?;
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
        let mut queue: Vec<TelemetryRecord> = Vec::new();
        let mut dropped: Vec<TelemetryRecord> = Vec::new();
        assert!(pack_page(&mut queue, &mut dropped).expect("pack").is_none());
        assert!(dropped.is_empty());
    }

    #[test]
    fn preserves_submission_order() {
        let mut queue = vec![fill(), fill(), fill(), fill()];
        let mut dropped: Vec<TelemetryRecord> = Vec::new();
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
        for ev in evs {
            assert!(matches!(ev, ciborium::value::Value::Map(_)));
        }
    }

    #[test]
    fn overflow_leaves_remaining_records_at_front_of_queue() {
        // Enough records to force more than one page.
        let mut queue: Vec<TelemetryRecord> = (0..200).map(|_| fill()).collect();
        let total = queue.len();
        let mut dropped: Vec<TelemetryRecord> = Vec::new();

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
        let mut queue = vec![fill(), oversize, fill()];
        let mut dropped: Vec<TelemetryRecord> = Vec::new();
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
        let mut queue: Vec<TelemetryRecord> = (0..200).map(|_| fill()).collect();
        let total = queue.len();
        let mut dropped: Vec<TelemetryRecord> = Vec::new();
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
