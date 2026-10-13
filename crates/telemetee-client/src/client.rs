use serde::Deserialize;
use url::Url;

use crate::{
    Error,
    attestation::{AttestationPolicy, Jwks, VerifiedAttestation, verify},
    noise::NkInitiator,
};

/// HTTP client for one collector version.
pub struct CollectorClient {
    http: reqwest::Client,
    base: Url,
    policy: AttestationPolicy,
}

#[derive(Deserialize)]
struct AttestationResponse {
    token: String,
}

impl CollectorClient {
    /// Targets `<base_url>/<version>/` and verifies it against `policy`.
    pub fn new(base_url: Url, version: &str, policy: AttestationPolicy) -> Result<Self, Error> {
        let base = base_url
            .join(&format!("{version}/"))
            .map_err(|_| Error::Attestation("base URL cannot be joined".into()))?;
        Ok(Self {
            http: reqwest::Client::new(),
            base,
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

    /// Sends `payload` through a fresh NK handshake to the echo endpoint and returns what came back.
    pub async fn echo(&self, payload: &[u8]) -> Result<Vec<u8>, Error> {
        let attestation = self.attestation().await?;
        let mut initiator = NkInitiator::new(&attestation.noise_public_key)?;
        let message1 = initiator.write_message(payload)?;

        let response = self
            .http
            .post(self.endpoint("echo")?)
            .header(reqwest::header::CONTENT_TYPE, "application/octet-stream")
            .body(message1)
            .send()
            .await?;
        if !response.status().is_success() {
            return Err(Error::Status(response.status()));
        }
        let message2 = response.bytes().await?;
        initiator.read_message(&message2)
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
