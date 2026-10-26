//! Verifies that the client re-attests and retries once per `send()` call when the collector's
//! Noise static key rotates between attestation and POST.

use std::sync::{Arc, Mutex};

use base64::{Engine, engine::general_purpose::STANDARD};
use jsonwebtoken::{Algorithm, EncodingKey, Header, encode};
use serde_json::{Value, json};
use telemetee_api::models::{FillAssistRuleOutcome, TelemetryRecord};
use telemetee_client::{
    CollectorClient, Error,
    attestation::AttestationPolicy,
    page::{RESPONSE_LENGTH, Submission},
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

const RSA_PRIVATE_PEM: &str = include_str!("data/issuer_key.pem");
const RSA_N: &str = include_str!("data/issuer_n.txt");
const HOSTDATA: &str = "670fff86714a650a49b58fadc1e90fedae0eb32dd51e34931c1e7a1839c08f6f";
const PROTOCOL: &str = "Noise_NK_P256_AESGCM_SHA256";
const COLLECTOR_VERSION: &str = "0.1.0";

#[tokio::test]
async fn send_retries_once_when_key_rotates_between_attestation_and_post() {
    let (client, state) = start_client_against_mock().await;
    state.lock().rotate_after_next_attestation = true;

    let mut queue = vec![Submission::Unauthed(fill_record())];
    let outcome = client
        .send(&mut queue)
        .await
        .expect("send succeeds via retry");

    assert_eq!(outcome.records_sent, 1);
    assert_eq!(outcome.pages_sent, 1);
    assert_eq!(state.lock().attestation_calls, 2);
    assert_eq!(state.lock().telemetry_calls, 2);
    assert_eq!(state.lock().accepted_pages, 1);
}

#[tokio::test]
async fn send_does_not_re_attest_when_first_post_succeeds() {
    let (client, state) = start_client_against_mock().await;

    let mut queue = vec![Submission::Unauthed(fill_record())];
    let outcome = client.send(&mut queue).await.expect("send succeeds");

    assert_eq!(outcome.records_sent, 1);
    assert_eq!(state.lock().attestation_calls, 1);
    assert_eq!(state.lock().telemetry_calls, 1);
}

#[tokio::test]
async fn send_retries_each_page_independently_across_multiple_rotations() {
    // A slow or large upload can span several rotation ticks. Each page must retry independently;
    // consuming the retry on page 1 must not leave page 2 stuck when its own rotation lands.
    // Simulate it by rotating before every first-attempt POST (odd-indexed): each page's first
    // attempt fails and its retry (next POST, no rotation) lands on the post-rotation key.
    let (client, state) = start_client_against_mock().await;
    state.lock().rotate_before_odd_telemetry_calls = true;

    let mut queue: Vec<Submission> =
        (0..200).map(|_| Submission::Unauthed(fill_record())).collect();
    let outcome = client
        .send(&mut queue)
        .await
        .expect("every page's retry absorbs its rotation");

    assert!(outcome.pages_sent >= 2, "payload spans multiple pages");
    assert!(queue.is_empty(), "queue drained after retries");
    assert_eq!(state.lock().accepted_pages, outcome.pages_sent);
    assert_eq!(
        state.lock().telemetry_calls,
        outcome.pages_sent * 2,
        "each page = one failing first attempt + one succeeding retry",
    );
}

#[tokio::test]
async fn send_fails_when_retry_also_fails() {
    let (client, state) = start_client_against_mock().await;
    // Rotating on every attestation means the key the client just received is already stale by
    // the time its POST reaches the server.
    state.lock().rotate_on_every_attestation = true;

    let mut queue = vec![Submission::Unauthed(fill_record())];
    let error = client
        .send(&mut queue)
        .await
        .expect_err("retry also fails");

    assert!(
        matches!(&error, Error::PartialSend { pages_sent, .. } if *pages_sent == 0),
        "expected PartialSend with pages_sent=0, got {error:?}"
    );
    assert!(
        state.lock().attestation_calls >= 2,
        "re-attested at least once"
    );
    assert_eq!(state.lock().accepted_pages, 0);
}

fn fill_record() -> TelemetryRecord {
    TelemetryRecord::FillAssistRuleOutcome(Box::new(FillAssistRuleOutcome {
        schema: None,
        rule_id: "r1".into(),
        success: true,
        domain: "a.example".into(),
        client_version_major: 2025,
    }))
}

struct State {
    private_key: Vec<u8>,
    public_key: Vec<u8>,
    issuer: String,
    attestation_calls: usize,
    telemetry_calls: usize,
    accepted_pages: usize,
    rotate_after_next_attestation: bool,
    rotate_on_every_attestation: bool,
    /// If set, every odd-numbered `/telemetry` POST (1st, 3rd, 5th, ...) rotates the key before
    /// the responder attempts to decrypt. With the client's per-page retry, that means every page's
    /// first attempt fails on the post-rotation key and its retry (next POST) lands cleanly.
    rotate_before_odd_telemetry_calls: bool,
}

impl State {
    fn new(issuer: String) -> Self {
        let keypair = fresh_keypair();
        Self {
            private_key: keypair.private,
            public_key: keypair.public,
            issuer,
            attestation_calls: 0,
            telemetry_calls: 0,
            accepted_pages: 0,
            rotate_after_next_attestation: false,
            rotate_on_every_attestation: false,
            rotate_before_odd_telemetry_calls: false,
        }
    }

    fn rotate(&mut self) {
        let keypair = fresh_keypair();
        self.private_key = keypair.private;
        self.public_key = keypair.public;
    }
}

fn fresh_keypair() -> snow::Keypair {
    snow::Builder::new(PROTOCOL.parse().expect("protocol parses"))
        .generate_keypair()
        .expect("keypair")
}

#[derive(Clone)]
struct SharedState(Arc<Mutex<State>>);

impl SharedState {
    fn lock(&self) -> std::sync::MutexGuard<'_, State> {
        self.0.lock().expect("mutex not poisoned")
    }
}

async fn start_client_against_mock() -> (CollectorClient, SharedState) {
    let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
    let addr = listener.local_addr().expect("local addr");
    let issuer = format!("http://{addr}");
    let state = SharedState(Arc::new(Mutex::new(State::new(issuer.clone()))));
    spawn_server(listener, state.clone());

    let policy = AttestationPolicy {
        issuer: issuer.parse().expect("valid url"),
        hostdata: HOSTDATA.into(),
    };
    let client = CollectorClient::new(
        format!("http://{addr}/").parse().expect("valid url"),
        COLLECTOR_VERSION,
        policy,
    )
    .expect("client");
    (client, state)
}

fn spawn_server(listener: TcpListener, state: SharedState) {
    tokio::spawn(async move {
        loop {
            let (socket, _) = match listener.accept().await {
                Ok(pair) => pair,
                Err(_) => break,
            };
            let state = state.clone();
            tokio::spawn(async move {
                let _ = handle_connection(socket, state).await;
            });
        }
    });
}

async fn handle_connection(
    mut socket: tokio::net::TcpStream,
    state: SharedState,
) -> std::io::Result<()> {
    let mut buf = Vec::with_capacity(8192);
    let header_end = loop {
        let mut chunk = [0u8; 1024];
        let n = socket.read(&mut chunk).await?;
        if n == 0 {
            return Ok(());
        }
        buf.extend_from_slice(&chunk[..n]);
        if let Some(idx) = find_subslice(&buf, b"\r\n\r\n") {
            break idx + 4;
        }
    };
    let header_str = std::str::from_utf8(&buf[..header_end]).unwrap_or("").to_string();
    let request_line = header_str.lines().next().unwrap_or("").to_string();
    let mut parts = request_line.split_whitespace();
    let method = parts.next().unwrap_or("").to_string();
    let path = parts.next().unwrap_or("").to_string();
    let content_length = header_str
        .lines()
        .find_map(|l| {
            let (name, value) = l.split_once(':')?;
            if name.eq_ignore_ascii_case("content-length") {
                Some(value)
            } else {
                None
            }
        })
        .and_then(|v| v.trim().parse::<usize>().ok())
        .unwrap_or(0);

    while buf.len() < header_end + content_length {
        let mut chunk = [0u8; 4096];
        let n = socket.read(&mut chunk).await?;
        if n == 0 {
            break;
        }
        buf.extend_from_slice(&chunk[..n]);
    }
    let body = buf[header_end..header_end + content_length].to_vec();

    let (status, content_type, body_out) = route(&method, &path, &body, &state);
    let response = format!(
        "HTTP/1.1 {status}\r\nContent-Type: {content_type}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        body_out.len()
    );
    socket.write_all(response.as_bytes()).await?;
    socket.write_all(&body_out).await?;
    let _ = socket.shutdown().await;
    Ok(())
}

fn route(
    method: &str,
    path: &str,
    body: &[u8],
    state: &SharedState,
) -> (&'static str, &'static str, Vec<u8>) {
    if method == "GET" && path == "/attestation" {
        let (public_key, issuer, rotate_now) = {
            let mut st = state.lock();
            st.attestation_calls += 1;
            (
                st.public_key.clone(),
                st.issuer.clone(),
                st.rotate_on_every_attestation,
            )
        };
        let token = sign_claims(&public_key, &issuer);
        if rotate_now {
            state.lock().rotate();
        } else {
            let mut st = state.lock();
            if st.rotate_after_next_attestation {
                st.rotate();
                st.rotate_after_next_attestation = false;
            }
        }
        let json = serde_json::to_vec(&json!({ "token": token })).expect("json");
        return ("200 OK", "application/json", json);
    }
    if method == "GET" && path == "/certs" {
        let jwks = json!({
            "keys": [{
                "kid": "test",
                "kty": "RSA",
                "n": RSA_N.trim(),
                "e": "AQAB",
            }]
        });
        return (
            "200 OK",
            "application/json",
            serde_json::to_vec(&jwks).expect("json"),
        );
    }
    if method == "POST" && path == "/telemetry" {
        let private_key = {
            let mut st = state.lock();
            st.telemetry_calls += 1;
            if st.rotate_before_odd_telemetry_calls && st.telemetry_calls % 2 == 1 {
                st.rotate();
            }
            st.private_key.clone()
        };
        let mut responder = snow::Builder::new(PROTOCOL.parse().expect("protocol parses"))
            .local_private_key(&private_key)
            .expect("private key")
            .build_responder()
            .expect("responder");

        let mut plaintext = vec![0u8; 65535];
        return match responder.read_message(body, &mut plaintext) {
            Ok(_) => {
                let mut out = vec![0u8; 65535];
                let n = responder
                    .write_message(&[], &mut out)
                    .expect("writes message 2");
                state.lock().accepted_pages += 1;
                ("200 OK", "application/octet-stream", out[..n].to_vec())
            }
            Err(_) => {
                // Constant-size garbage keeps the response shape uniform with the real collector.
                let mut garbage = vec![0u8; RESPONSE_LENGTH];
                for byte in garbage.iter_mut() {
                    *byte = rand_byte();
                }
                ("200 OK", "application/octet-stream", garbage)
            }
        };
    }
    ("404 Not Found", "text/plain", b"not found".to_vec())
}

fn sign_claims(public_key: &[u8], issuer: &str) -> String {
    let mut header = Header::new(Algorithm::RS256);
    header.kid = Some("test".into());
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("after epoch")
        .as_secs();
    let claims: Value = json!({
        "iss": issuer,
        "iat": now,
        "nbf": now,
        "exp": now + 3600,
        "x-ms-attestation-type": "sevsnpvm",
        "x-ms-compliance-status": "azure-compliant-uvm",
        "x-ms-sevsnpvm-is-debuggable": false,
        "x-ms-sevsnpvm-hostdata": HOSTDATA,
        "x-ms-runtime": { "noise_pub": STANDARD.encode(public_key) },
    });
    let key = EncodingKey::from_rsa_pem(RSA_PRIVATE_PEM.as_bytes()).expect("valid key");
    encode(&header, &claims, &key).expect("signs")
}

fn find_subslice(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    haystack.windows(needle.len()).position(|w| w == needle)
}

fn rand_byte() -> u8 {
    use std::time::{SystemTime, UNIX_EPOCH};
    (SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("after epoch")
        .subsec_nanos()
        & 0xFF) as u8
}
