# Client bindings

The Rust side consists of three crates under `crates/`:

- `telemetee-api/` — generated types for every `[Telemetry]` POCO. Regenerate from the
  collector's OpenAPI document.
- `telemetee-client/` — token verification, NK initiator, page builder. Re-exports
  `telemetee-api` models.
- `telemetee-cli/` — verifier and sender CLI wrapping the client crate.

## Regenerating `telemetee-api`

`crates/telemetee-api/` is generated from the collector's OpenAPI document
(`openapitools.json` pins generator 7.15.0). Regenerate after you add or change a
`[Telemetry]` POCO. Requires `java` and `npx` on `PATH`.

```sh
./support/generate-client-bindings.sh
```

The script boots the collector in Development on port 5050. `/openapi/v1.json` is only
served in Development, and the dev-only `/dev/telemetry` endpoint is what anchors every
`TelemetryRecord` subtype in the schema. The script fetches `/openapi/v1.json` into
`artifacts/` and runs `openapi-generator-cli` with the Rust generator over
`crates/telemetee-api/`. Generated code is committed; `artifacts/` is not.

## Using `telemetee-client`

`telemetee-client` re-exports the generated models as `telemetee_client::models`. The sole
public sender, [`CollectorClient::send`], is bound to the generated `TelemetryRecord` enum.
The compiler rejects any other type, so there is no path to put an unknown schema or raw bytes
on the wire from the public API. The raw page/byte path is crate-internal.

```rust
use telemetee_client::{CollectorClient, attestation::AttestationPolicy};
use telemetee_client::models::{AuthedGeneratedPassword, GeneratedPassword, TelemetryRecord};
use telemetee_client::page::Submission;

let client = CollectorClient::new(base, "0.1.0", policy)?;
let mut queue = vec![
    Submission::Unauthed(TelemetryRecord::GeneratedPassword(Box::new(GeneratedPassword {
        schema: None,
        kind: "password".into(),
        length: 14,
        word_count: 0,
        has_lowercase: true,
        has_uppercase: true,
        has_digits: true,
        has_special: false,
        accepted: true,
    }))),
    Submission::Authed {
        token: jwt_for_logged_in_user.clone(),
        event: TelemetryRecord::AuthedGeneratedPassword(Box::new(AuthedGeneratedPassword {
            schema: None,
            user_id: user_id.clone(),         // collector rejects if this != the JWT's sub claim
            kind: "passphrase".into(),
            length: 24,
            word_count: 4,
            has_lowercase: true,
            has_uppercase: false,
            has_digits: false,
            has_special: false,
            accepted: true,
        })),
    },
];
let outcome = client.send(&mut queue).await?;
// `queue` is empty on success; `outcome.oversize_dropped` carries any records the client
// refused to ship. On an `Err(Error::PartialSend { .. })` the records that did not reach the
// wire are back at the front of `queue`, safe to retry on the next shipment.
```

## CLI

The CLI accepts flat JSON on the command line. `serde_json::from_str::<TelemetryRecord>`
fails early on an unknown `schema` discriminator or a missing required field, so bad input
never reaches the wire. Pass `--auth <jwt>` to submit records as an authed group.
