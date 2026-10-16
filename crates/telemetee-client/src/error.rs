use thiserror::Error;

/// Errors from verifying a collector or talking to it.
#[derive(Debug, Error)]
pub enum Error {
    /// HTTP transport failure.
    #[error("http: {0}")]
    Http(#[from] reqwest::Error),

    /// The collector answered with an unexpected status.
    #[error("collector returned {0}")]
    Status(reqwest::StatusCode),

    /// The token's signature or registered claims did not validate.
    #[error("token: {0}")]
    Token(#[from] jsonwebtoken::errors::Error),

    /// The token validated but its claims do not match the pinned policy.
    #[error("attestation rejected: {0}")]
    Attestation(String),

    /// The Noise handshake failed.
    #[error("noise: {0}")]
    Noise(#[from] snow::Error),

    /// JSON encoding or decoding failed. Only hit when preparing records on the client side.
    #[error("json: {0}")]
    Json(#[from] serde_json::Error),

    /// The collector's response was not the expected length.
    #[error("protocol: {0}")]
    Protocol(String),

    /// A send flushed some pages onto the wire before a transport-level failure stopped it. The
    /// records that did not reach the collector have been restored to the front of the input
    /// queue in their original order and are safe to retry on the next shipment without producing
    /// duplicates on the wire.
    #[error("partial send after {pages_sent} page(s): {cause}")]
    PartialSend {
        /// Pages that reached the collector before the failure.
        pages_sent: usize,
        /// The underlying transport error that stopped the loop.
        #[source]
        cause: Box<Error>,
    },
}
