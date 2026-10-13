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
}
