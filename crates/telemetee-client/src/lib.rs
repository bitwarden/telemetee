//! Client for the Bitwarden anonymous telemetry collector.
//!
//! A client trusts a collector only after verifying its Microsoft Azure Attestation token against a
//! pinned shared attestation provider and a pinned CCE policy hash, then takes the collector's Noise
//! static key from that token and speaks `Noise_NK_P256_AESGCM_SHA256` to it over HTTP.

pub mod attestation;
mod client;
mod error;
pub mod noise;

pub use client::CollectorClient;
pub use error::Error;
