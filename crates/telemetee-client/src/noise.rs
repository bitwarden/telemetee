//! Initiator side of the collector's Noise channel.

use crate::Error;

/// The one suite the collector speaks. P-256 and AES-GCM keep the server inside the .NET BCL, and
/// the SDK's IPC layer already runs the same primitives.
pub const PROTOCOL: &str = "Noise_NK_P256_AESGCM_SHA256";

/// Maximum Noise message length per the specification.
const MAX_MESSAGE_LEN: usize = 65535;

/// One NK handshake. The request payload rides in message 1 and the reply in message 2, so a single
/// HTTP round trip completes both.
pub struct NkInitiator {
    state: snow::HandshakeState,
}

impl NkInitiator {
    /// Starts a handshake with a collector whose static public key is `remote_static`, taken from a
    /// verified attestation token.
    pub fn new(remote_static: &[u8]) -> Result<Self, Error> {
        let params = PROTOCOL.parse().map_err(|_| {
            snow::Error::Pattern(snow::error::PatternProblem::UnsupportedHandshakeType)
        })?;
        let state =
            snow::Builder::with_resolver(params, Box::new(bitwarden_random::SdkCryptoResolver))
                .remote_public_key(remote_static)?
                .build_initiator()?;
        Ok(Self { state })
    }

    /// Produces message 1 carrying `payload`, encrypted to the collector's static key.
    pub fn write_message(&mut self, payload: &[u8]) -> Result<Vec<u8>, Error> {
        let mut buffer = vec![0u8; MAX_MESSAGE_LEN];
        let len = self.state.write_message(payload, &mut buffer)?;
        buffer.truncate(len);
        Ok(buffer)
    }

    /// Consumes message 2 and returns the collector's payload.
    pub fn read_message(&mut self, message: &[u8]) -> Result<Vec<u8>, Error> {
        let mut buffer = vec![0u8; MAX_MESSAGE_LEN];
        let len = self.state.read_message(message, &mut buffer)?;
        buffer.truncate(len);
        Ok(buffer)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn new_responder() -> (snow::HandshakeState, Vec<u8>) {
        let builder = snow::Builder::new(PROTOCOL.parse().expect("valid params"));
        let keypair = builder.generate_keypair().expect("keypair");
        let state = snow::Builder::new(PROTOCOL.parse().expect("valid params"))
            .local_private_key(&keypair.private)
            .expect("private key")
            .build_responder()
            .expect("responder");
        (state, keypair.public)
    }

    #[test]
    fn round_trips_against_snow_responder() {
        let (mut responder, public) = new_responder();
        let mut initiator = NkInitiator::new(&public).expect("initiator");

        let message1 = initiator.write_message(b"ping").expect("message 1");
        let mut buffer = vec![0u8; MAX_MESSAGE_LEN];
        let len = responder
            .read_message(&message1, &mut buffer)
            .expect("reads");
        assert_eq!(&buffer[..len], b"ping");

        let len = responder
            .write_message(b"pong", &mut buffer)
            .expect("message 2");
        let reply = initiator.read_message(&buffer[..len]).expect("reads");
        assert_eq!(reply, b"pong");
    }

    #[test]
    fn wrong_static_key_is_rejected_by_responder() {
        let (mut responder, _) = new_responder();
        let (_, other_public) = new_responder();
        let mut initiator = NkInitiator::new(&other_public).expect("initiator");

        let message1 = initiator.write_message(b"ping").expect("message 1");
        let mut buffer = vec![0u8; MAX_MESSAGE_LEN];
        assert!(responder.read_message(&message1, &mut buffer).is_err());
    }

    #[test]
    fn malformed_static_key_cannot_complete_message_1() {
        // snow defers point validation to the DH, so the failure may surface at either step.
        let result = NkInitiator::new(&[0x02; 33])
            .and_then(|mut initiator| initiator.write_message(b"ping"));
        assert!(result.is_err());
    }
}
