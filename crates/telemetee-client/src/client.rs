use serde::Deserialize;
use url::Url;

use crate::{
    Error,
    attestation::{AttestationPolicy, Jwks, VerifiedAttestation, verify},
    noise::NkInitiator,
    page::{PAGE_LENGTH, PLAINTEXT_LENGTH, RESPONSE_LENGTH, Submission, pack_page},
};

const TARGET_VERSION_HEADER: &str = "Telemetee-Target-Version";

/// Summary of a single [`CollectorClient::send`] call: how many records the client packed onto
/// pages, how many pages those were, and the records the client refused to ship as oversize.
/// The caller receives the oversize records as values so the originating schema can be logged or
/// fixed, they cannot be shipped.
#[derive(Debug)]
pub struct SendOutcome {
    pub records_sent: usize,
    pub pages_sent: usize,
    pub oversize_dropped: Vec<Submission>,
}

/// HTTP client for one collector version.
pub struct CollectorClient {
    http: reqwest::Client,
    base: Url,
    version: String,
    policy: AttestationPolicy,
}

#[derive(Deserialize)]
struct AttestationResponse {
    token: String,
}

impl CollectorClient {
    /// Targets `base_url` directly (no version segment); the client sends `version` in the
    /// `Telemetee-Target-Version` header on every telemetry POST and verifies the collector's
    /// attestation against `policy`.
    pub fn new(base_url: Url, version: &str, policy: AttestationPolicy) -> Result<Self, Error> {
        let base = if base_url.path().ends_with('/') {
            base_url
        } else {
            base_url
                .join(&format!("{}/", base_url.path()))
                .map_err(|_| Error::Attestation("base URL cannot be joined".into()))?
        };
        Ok(Self {
            http: reqwest::Client::new(),
            base,
            version: version.to_string(),
            policy,
        })
    }

    /// Fetches and verifies the collector's attestation token.
    pub async fn attestation(&self) -> Result<VerifiedAttestation, Error> {
        let token = self
            .get_json::<AttestationResponse>(self.endpoint("attestation")?)
            .await?
            .token;
        let jwks = self.get_json::<Jwks>(self.policy.jwks_url()?).await?;
        verify(&token, &jwks, &self.policy)
    }

    /// Ships `queue` to the collector in submission order. The client packs records into one or
    /// more fixed-size pages, encrypts each under the collector's attested Noise static key, and
    /// POSTs them. Attestation runs once per call. An empty queue is a no-op.
    ///
    /// The collector rotates its Noise static key on a time cadence, so a page's attested key can
    /// become stale between attestation and POST. The handshake fails on the stale key; this
    /// method absorbs that by re-attesting and retrying any page that fails the handshake. Each
    /// page gets at most one retry; subsequent pages inherit the fresh attestation, so a slow
    /// upload that spans several rotations still completes.
    ///
    /// `queue` is treated as a work queue: on return it contains exactly the records that did
    /// **not** reach the wire, at the front, in their original order.
    ///
    /// - `Ok(outcome)` — every record either reached the wire ([`SendOutcome::records_sent`]) or
    ///   was removed as oversize ([`SendOutcome::oversize_dropped`]); `queue` is empty.
    /// - `Err(Error::PartialSend { .. })` — one or more pages reached the collector before a
    ///   later page failed. The records packed into pages that did not ship are back in `queue`
    ///   and are safe to retry on the next shipment without producing duplicates on the wire.
    /// - `Err(_)` otherwise — nothing reached the wire. `queue` holds every record it did on
    ///   entry.
    pub async fn send(&self, queue: &mut Vec<Submission>) -> Result<SendOutcome, Error> {
        if queue.is_empty() {
            return Ok(SendOutcome {
                records_sent: 0,
                pages_sent: 0,
                oversize_dropped: Vec::new(),
            });
        }

        let mut attestation = self.attestation().await?;

        let mut oversize_dropped: Vec<Submission> = Vec::new();
        let mut packed: Vec<([u8; PLAINTEXT_LENGTH], Vec<Submission>)> = Vec::new();
        loop {
            match pack_page(queue, &mut oversize_dropped) {
                Ok(Some(entry)) => packed.push(entry),
                Ok(None) => break,
                Err(e) => {
                    restore_to_front(
                        queue,
                        packed.into_iter().flat_map(|(_, subs)| subs).collect(),
                    );
                    return Err(e);
                }
            }
        }

        if packed.is_empty() {
            return Ok(SendOutcome {
                records_sent: 0,
                pages_sent: 0,
                oversize_dropped,
            });
        }

        let mut pages_sent = 0usize;
        let mut records_sent = 0usize;
        let mut iter = packed.into_iter();
        while let Some((bytes, subs)) = iter.next() {
            let count = subs.len();
            let mut result = self.send_one_page(&attestation, &bytes).await;
            if matches!(result, Err(Error::Noise(_))) {
                // The collector rotated its key between our attestation fetch and this POST.
                // Re-attest and retry this page; subsequent pages inherit the fresh attestation.
                attestation = self.attestation().await?;
                result = self.send_one_page(&attestation, &bytes).await;
            }
            if let Err(cause) = result {
                let mut leftover = subs;
                for (_, more) in iter {
                    leftover.extend(more);
                }
                restore_to_front(queue, leftover);
                return Err(Error::PartialSend {
                    pages_sent,
                    cause: Box::new(cause),
                });
            }
            pages_sent += 1;
            records_sent += count;
        }

        Ok(SendOutcome {
            records_sent,
            pages_sent,
            oversize_dropped,
        })
    }

    pub(crate) async fn send_one_page(
        &self,
        attestation: &VerifiedAttestation,
        page: &[u8; PLAINTEXT_LENGTH],
    ) -> Result<(), Error> {
        let mut initiator = NkInitiator::new(&attestation.noise_public_key)?;
        let message1 = initiator.write_message(page)?;
        debug_assert_eq!(message1.len(), PAGE_LENGTH);

        let response = self
            .http
            .post(self.endpoint("telemetry")?)
            .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
            .header(TARGET_VERSION_HEADER, &self.version)
            .body(message1)
            .send()
            .await?;
        if !response.status().is_success() {
            return Err(Error::Status(response.status()));
        }
        let message2 = response.bytes().await?;
        if message2.len() != RESPONSE_LENGTH {
            return Err(Error::Protocol(format!(
                "collector returned {} bytes, expected {RESPONSE_LENGTH}",
                message2.len()
            )));
        }
        let payload = initiator.read_message(&message2)?;
        if !payload.is_empty() {
            return Err(Error::Protocol(format!(
                "collector returned {} bytes of payload, expected 0",
                payload.len()
            )));
        }
        Ok(())
    }

    fn endpoint(&self, path: &str) -> Result<Url, Error> {
        self.base
            .join(path)
            .map_err(|_| Error::Attestation("endpoint URL cannot be joined".into()))
    }

    async fn get_json<T: serde::de::DeserializeOwned>(&self, url: Url) -> Result<T, Error> {
        let response = self.http.get(url).send().await?;
        if !response.status().is_success() {
            return Err(Error::Status(response.status()));
        }
        Ok(response.json().await?)
    }
}

fn restore_to_front(queue: &mut Vec<Submission>, prefix: Vec<Submission>) {
    let suffix = std::mem::take(queue);
    *queue = prefix;
    queue.extend(suffix);
}
