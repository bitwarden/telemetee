//! Client for the Bitwarden anonymous telemetry collector.
//!
//! A client trusts a collector only after verifying its Microsoft Azure Attestation token against a
//! pinned shared attestation provider and a pinned CCE policy hash, then takes the collector's Noise
//! static key from that token and speaks `Noise_NK_P256_AESGCM_SHA256` to it over HTTP.

pub mod attestation;
mod client;
mod error;
pub mod noise;
pub mod page;

pub use client::CollectorClient;
pub use error::Error;

/// Strongly-typed record variants, generated from the collector's OpenAPI document by
/// `support/generate-client-bindings.sh`. One struct per `[Telemetry]`-marked POCO in
/// `src/Telemetee.Telemetry/TelemetryData/`; wrap an instance in a [`page::Submission`] and pass
/// it to [`CollectorClient::send`].
pub use telemetee_api::models;
