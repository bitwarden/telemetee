//! Verification of the collector's MAA token against a pinned policy.

use base64::{Engine, engine::general_purpose::STANDARD};
use jsonwebtoken::{Algorithm, DecodingKey, Validation, decode, decode_header};
use serde::Deserialize;
use url::Url;
use x509_parser::prelude::{FromDer, X509Certificate};

use crate::Error;

/// Length of an uncompressed SEC1 P-256 point.
const NOISE_PUBLIC_KEY_LENGTH: usize = 65;

/// What a client pins about the collector it is willing to talk to.
#[derive(Debug, Clone)]
pub struct AttestationPolicy {
    /// A Microsoft shared attestation provider, for example
    /// `https://sharedeus2.eus2.attest.azure.net`. Must equal the token's `iss` exactly.
    pub issuer: Url,
    /// SHA-256 of the collector's CCE policy, hex. Must equal `x-ms-sevsnpvm-hostdata`.
    pub hostdata: String,
}

impl AttestationPolicy {
    /// `iss` as it appears in tokens: the pinned URL without a trailing slash.
    pub fn issuer_string(&self) -> String {
        self.issuer.as_str().trim_end_matches('/').to_string()
    }

    /// Where the issuer publishes its signing certificates.
    pub fn jwks_url(&self) -> Result<Url, Error> {
        format!("{}/certs", self.issuer_string())
            .parse()
            .map_err(|_| Error::Attestation("issuer URL is not a valid base".into()))
    }
}

/// The outcome of a successful verification.
#[derive(Debug, Clone)]
pub struct VerifiedAttestation {
    /// The collector's Noise static public key, taken from `x-ms-runtime.noise_pub`.
    pub noise_public_key: Vec<u8>,
    /// Unix time after which the token must not be used.
    pub expires_at: u64,
    /// All claims, for display and diagnostics.
    pub claims: serde_json::Value,
}

/// A JWKS as MAA publishes it: entries carry `kid` and `x5c`, and may omit `n`/`e`.
#[derive(Debug, Clone, Deserialize)]
pub struct Jwks {
    /// The signing keys.
    pub keys: Vec<Jwk>,
}

/// One JWKS entry.
#[derive(Debug, Clone, Deserialize)]
pub struct Jwk {
    /// Key identifier matched against the token header.
    pub kid: Option<String>,
    /// Key type; only `RSA` is accepted.
    pub kty: String,
    /// RSA modulus, base64url.
    pub n: Option<String>,
    /// RSA exponent, base64url.
    pub e: Option<String>,
    /// Certificate chain, base64 DER, leaf first.
    pub x5c: Option<Vec<String>>,
}

#[derive(Deserialize)]
struct Claims {
    exp: u64,
    #[serde(rename = "x-ms-attestation-type")]
    attestation_type: String,
    #[serde(rename = "x-ms-compliance-status")]
    compliance_status: String,
    #[serde(rename = "x-ms-sevsnpvm-is-debuggable")]
    is_debuggable: bool,
    #[serde(rename = "x-ms-sevsnpvm-hostdata")]
    hostdata: String,
    #[serde(rename = "x-ms-runtime")]
    runtime: RuntimeClaims,
}

#[derive(Deserialize)]
struct RuntimeClaims {
    noise_pub: String,
}

/// Verifies `token` against `policy` using the issuer's published keys.
///
/// Checks, in order: RS256 signature by a key in `jwks` matching the header `kid`; `iss`, `exp`, and
/// `nbf`; then the SEV-SNP claims that make the token mean "this exact collector code, not in debug
/// mode, on an Azure-compliant UVM"; then that the runtime data carries a well-formed Noise key.
pub fn verify(
    token: &str,
    jwks: &Jwks,
    policy: &AttestationPolicy,
) -> Result<VerifiedAttestation, Error> {
    let header = decode_header(token)?;
    if header.alg != Algorithm::RS256 {
        return Err(Error::Attestation(format!(
            "unexpected alg {:?}",
            header.alg
        )));
    }
    let kid = header
        .kid
        .ok_or_else(|| Error::Attestation("token header has no kid".into()))?;
    let jwk = jwks
        .keys
        .iter()
        .find(|key| key.kid.as_deref() == Some(kid.as_str()))
        .ok_or_else(|| Error::Attestation(format!("no published key with kid {kid}")))?;
    let decoding_key = decoding_key(jwk)?;

    let mut validation = Validation::new(Algorithm::RS256);
    validation.set_issuer(&[policy.issuer_string()]);
    validation.set_required_spec_claims(&["exp", "iss"]);
    validation.validate_nbf = true;
    validation.validate_aud = false;

    let data = decode::<serde_json::Value>(token, &decoding_key, &validation)?;
    let claims: Claims = serde_json::from_value(data.claims.clone())
        .map_err(|error| Error::Attestation(format!("missing or malformed claim: {error}")))?;

    expect(
        "x-ms-attestation-type",
        &claims.attestation_type,
        "sevsnpvm",
    )?;
    expect(
        "x-ms-compliance-status",
        &claims.compliance_status,
        "azure-compliant-uvm",
    )?;
    if claims.is_debuggable {
        return Err(Error::Attestation(
            "x-ms-sevsnpvm-is-debuggable is true".into(),
        ));
    }
    if !claims.hostdata.eq_ignore_ascii_case(&policy.hostdata) {
        return Err(Error::Attestation(format!(
            "hostdata {} is not the pinned {}",
            claims.hostdata, policy.hostdata
        )));
    }

    let noise_public_key = STANDARD
        .decode(&claims.runtime.noise_pub)
        .map_err(|_| Error::Attestation("noise_pub is not base64".into()))?;
    if noise_public_key.len() != NOISE_PUBLIC_KEY_LENGTH || noise_public_key[0] != 0x04 {
        return Err(Error::Attestation(
            "noise_pub is not an uncompressed P-256 point".into(),
        ));
    }

    Ok(VerifiedAttestation {
        noise_public_key,
        expires_at: claims.exp,
        claims: data.claims,
    })
}

fn expect(name: &str, actual: &str, wanted: &str) -> Result<(), Error> {
    if actual == wanted {
        Ok(())
    } else {
        Err(Error::Attestation(format!(
            "{name} is {actual}, expected {wanted}"
        )))
    }
}

fn decoding_key(jwk: &Jwk) -> Result<DecodingKey, Error> {
    if jwk.kty != "RSA" {
        return Err(Error::Attestation(format!("unsupported kty {}", jwk.kty)));
    }
    if let (Some(n), Some(e)) = (&jwk.n, &jwk.e) {
        return Ok(DecodingKey::from_rsa_components(n, e)?);
    }
    let leaf = jwk
        .x5c
        .as_ref()
        .and_then(|chain| chain.first())
        .ok_or_else(|| Error::Attestation("jwk has neither n/e nor x5c".into()))?;
    let der = STANDARD
        .decode(leaf)
        .map_err(|_| Error::Attestation("x5c is not base64".into()))?;
    let (_, certificate) = X509Certificate::from_der(&der)
        .map_err(|error| Error::Attestation(format!("x5c is not a certificate: {error}")))?;
    // For RSA the BIT STRING inside SubjectPublicKeyInfo is a PKCS#1 RSAPublicKey.
    Ok(DecodingKey::from_rsa_der(
        &certificate.public_key().subject_public_key.data,
    ))
}

#[cfg(test)]
mod tests {
    use jsonwebtoken::{EncodingKey, Header, encode};
    use serde_json::json;

    use super::*;

    const RSA_PRIVATE_PEM: &str = include_str!("../tests/data/issuer_key.pem");
    const RSA_N: &str = include_str!("../tests/data/issuer_n.txt");
    const HOSTDATA: &str = "670fff86714a650a49b58fadc1e90fedae0eb32dd51e34931c1e7a1839c08f6f";

    fn policy() -> AttestationPolicy {
        AttestationPolicy {
            issuer: "https://sharedeus2.eus2.attest.azure.net"
                .parse()
                .expect("valid url"),
            hostdata: HOSTDATA.into(),
        }
    }

    fn jwks() -> Jwks {
        Jwks {
            keys: vec![Jwk {
                kid: Some("test".into()),
                kty: "RSA".into(),
                n: Some(RSA_N.trim().into()),
                e: Some("AQAB".into()),
                x5c: None,
            }],
        }
    }

    fn noise_pub() -> String {
        let mut key = vec![0x04];
        key.extend_from_slice(&[0x11; 64]);
        STANDARD.encode(key)
    }

    fn claims() -> serde_json::Value {
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("after epoch")
            .as_secs();
        json!({
            "iss": "https://sharedeus2.eus2.attest.azure.net",
            "iat": now,
            "nbf": now,
            "exp": now + 3600,
            "x-ms-attestation-type": "sevsnpvm",
            "x-ms-compliance-status": "azure-compliant-uvm",
            "x-ms-sevsnpvm-is-debuggable": false,
            "x-ms-sevsnpvm-hostdata": HOSTDATA,
            "x-ms-runtime": { "noise_pub": noise_pub() },
        })
    }

    fn sign(claims: &serde_json::Value) -> String {
        let mut header = Header::new(Algorithm::RS256);
        header.kid = Some("test".into());
        let key = EncodingKey::from_rsa_pem(RSA_PRIVATE_PEM.as_bytes()).expect("valid key");
        encode(&header, claims, &key).expect("signs")
    }

    #[test]
    fn accepts_matching_token() {
        let verified = verify(&sign(&claims()), &jwks(), &policy()).expect("verifies");
        assert_eq!(verified.noise_public_key.len(), 65);
        assert_eq!(verified.noise_public_key[0], 0x04);
    }

    #[test]
    fn rejects_wrong_issuer() {
        let mut claims = claims();
        claims["iss"] = json!("https://evil.example");
        assert!(matches!(
            verify(&sign(&claims), &jwks(), &policy()),
            Err(Error::Token(_))
        ));
    }

    #[test]
    fn rejects_wrong_hostdata() {
        let mut claims = claims();
        claims["x-ms-sevsnpvm-hostdata"] = json!("00".repeat(32));
        assert!(matches!(
            verify(&sign(&claims), &jwks(), &policy()),
            Err(Error::Attestation(_))
        ));
    }

    #[test]
    fn rejects_debuggable() {
        let mut claims = claims();
        claims["x-ms-sevsnpvm-is-debuggable"] = json!(true);
        assert!(matches!(
            verify(&sign(&claims), &jwks(), &policy()),
            Err(Error::Attestation(_))
        ));
    }

    #[test]
    fn rejects_cvm_compliance_status() {
        let mut claims = claims();
        claims["x-ms-compliance-status"] = json!("azure-compliant-cvm");
        assert!(matches!(
            verify(&sign(&claims), &jwks(), &policy()),
            Err(Error::Attestation(_))
        ));
    }

    #[test]
    fn rejects_expired() {
        let mut claims = claims();
        claims["exp"] = json!(1_000_000);
        assert!(matches!(
            verify(&sign(&claims), &jwks(), &policy()),
            Err(Error::Token(_))
        ));
    }

    #[test]
    fn rejects_unknown_kid() {
        let mut jwks = jwks();
        jwks.keys[0].kid = Some("other".into());
        assert!(matches!(
            verify(&sign(&claims()), &jwks, &policy()),
            Err(Error::Attestation(_))
        ));
    }

    #[test]
    fn rejects_malformed_noise_key() {
        let mut claims = claims();
        claims["x-ms-runtime"]["noise_pub"] = json!(STANDARD.encode([0x02; 33]));
        assert!(matches!(
            verify(&sign(&claims), &jwks(), &policy()),
            Err(Error::Attestation(_))
        ));
    }

    #[test]
    fn rejects_signature_by_other_key() {
        let mut jwks = jwks();
        // Flip a byte of the modulus so the published key no longer matches the signer.
        let mut n = jwks.keys[0].n.clone().expect("n");
        n.replace_range(10..11, if &n[10..11] == "A" { "B" } else { "A" });
        jwks.keys[0].n = Some(n);
        assert!(matches!(
            verify(&sign(&claims()), &jwks, &policy()),
            Err(Error::Token(_))
        ));
    }
}
