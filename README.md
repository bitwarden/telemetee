# telemetee

Anonymizing telemetry collector for Bitwarden clients. The collector runs in a Confidential ACI
container. Each client verifies the container by attestation before it sends anything. Design notes
live in the research directory (`claude-research/anon-telemetry`); this repository is the
implementation.

Current state: milestone 2, the anonymized telemetry path. Milestone 1 added the attested Noise
channel; milestone 2 adds the pipeline on top. The collector accepts fixed-size padded pages. It
applies a per-field schema (`keep`, `bucket`, `pseudonymize`, `authed`) and pseudonymizes with
HMAC-SHA256 under a boot-time salt. It batches accepted records in memory, stamps each batch with
the flush time, and posts to Datadog in arrival order. Arrival order lets a downstream query group
on any pseudonymized field and sequence a user's events.

## Layout

```
src/Telemetee.Collector/       ASP.NET Core host (Bitwarden.Server.Sdk, minimal APIs)
src/Telemetee.Attestation/     MAA token acquisition via the aci/skr sidecar, plus a dev issuer
src/Telemetee.NoiseChannel/    Noise NK responder over BCL crypto, static key
src/Telemetee.Telemetry/       Page/record format, schema machinery, batch buffer, Datadog sink
  TelemetryData/               The data: one attributed POCO per telemetry type
test/                          xunit projects per library plus a WebApplicationFactory suite
crates/telemetee-api/          Generated Rust types for every [Telemetry] POCO (see "Client bindings")
crates/telemetee-client/       Rust client: token verification, NK initiator, page builder
crates/telemetee-cli/          Verifier and sender CLI wrapping the client crate
deploy/                        ACI ARM template and CCE policy generation
```

The Rust crate links to `../sdk-internal` by path so it can move there unchanged. It uses the SDK's
`bitwarden-random` snow resolver, the same way `bitwarden-ipc` does.

## Endpoints

Endpoints live at the root. The deployment maps a client to a specific collector version through
the reverse proxy, DNS, or a separate deployment per build. The collector itself hosts one version
at a time. See [Why version lives outside the API](#why-version-lives-outside-the-api).

| Method | Path                     | Purpose                                                                                                                       |
| ------ | ------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| GET    | `/attestation`           | `{ "token": "<MAA JWT>" }` with `x-ms-runtime.noise_pub`                                                                      |
| POST   | `/telemetry`             | Exactly one 4096-byte NK message 1 page in, exactly 81 bytes of empty NK message 2 out                                        |
| GET    | `/dev/attestation/certs` | JWKS for the development issuer only (not registered in Production)                                                           |
| POST   | `/dev/telemetry`         | Plaintext unauthed `TelemetryRecord` in, anonymized output back (not registered in Production)                                |
| POST   | `/dev/telemetry/authed`  | Same, plus `Authorization: Bearer <jwt>`; claims are parsed and `[Authed]` fields are verified (not registered in Production) |

Plus `/version`, `/healthz`, and `/openapi/v1.json`.

Every `POST /telemetry` must carry `Telemetee-Target-Version` equal to the collector's assembly
version. The collector rejects a mismatched or missing header with 400 before any crypto work. It
counts the rejection on `telemetee.pages` under outcome `rejected_version`. The handler never sees
a page from a client that disagrees about the version, so field semantics cannot drift silently if
a schema changes.

### Why version lives outside the API

Attestation pins a collector to one build. Each client holds a CCE policy hash
(`x-ms-sevsnpvm-hostdata`) specific to the build it shipped against, and an older client that
reaches a newer collector cannot trust the newer attestation because it does not know the newer
measurement. Routing multiple versions inside one API would route a stale client to a build it has
no basis to trust.

An external transparency log for build-to-measurement mappings would let a client discover and
trust a newer measurement at request time, at which point multiple API versions could live behind
one collector again. Until then, versioning is a deployment-layer concern, and the in-protocol
version check exists only to catch misconfigured infrastructure.

## Page and record format

- One POST = one page = one NK handshake. The request body is exactly 4096 bytes. The collector
  rejects any other length before it runs the handshake, so the edge sees a constant request size.
- The response is exactly 81 bytes (`PageFormat.ResponseLength`) in every case: handshake success
  or failure, every record accepted or dropped, or plaintext that was all padding. The response
  size reveals nothing.
- Plaintext layout: `uint16-BE body_length || CBOR body || 0x00 padding`. The CBOR body is a
  single map:
  ```cbor
  {
    "auths":  [<jwt>, ...],                        // deduped, insertion-ordered table
    "events": [
      [<auth_idx>, <event>],                       // authed: 2-element array; index into "auths"
      [<event>],                                   // unauthed: 1-element array
      ...
    ]
  }
  ```
  Each event is a CBOR map with a `schema` discriminator and the schema's fields. The `events`
  array is in strict submission order. If two authed records use the same token, they share a
  single entry in `auths`, so the ~700-byte JWT is written once per user per page, not once per
  record.
- The maximum record length is 1024 bytes. The collector drops a record over the cap and counts
  it; the walk continues.
- The client also drops an oversized record before any crypto work and returns it to the caller
  in `SendOutcome.oversize_dropped`.

## Schemas

Schemas are attributed POCOs under `src/Telemetee.Telemetry/TelemetryData/`. One POCO per schema.
The type inherits from `TelemetryRecord`. The `[Telemetry("name")]` attribute names it on the
wire. Every property carries one of `[Keep]`, `[Pseudonymize]`, `[Authed("<jwt-claim>")]`, or
`[Bucket(nameof(...))]`. A schema is authed if it declares any `[Authed]` field; its records are
only accepted inside an authed group on the page. For each `[Authed]` field, the client sends the
plaintext value it asserts. The collector rejects the record if that value does not equal the
named JWT claim, and otherwise HMAC-pseudonymizes it under the per-boot salt before anything
reaches the sink. Properties map PascalCase → snake_case on the wire.

The schema set and the System.Text.Json polymorphism derived-type set are both discovered at
startup. The collector scans the assembly for `[Telemetry]`-marked subtypes of `TelemetryRecord`.
To add a telemetry type, drop a POCO into `TelemetryData/`. There is no index to update, and the
type immediately appears in the OpenAPI document and the generated client bindings. The code under
`Schemas/` is machinery that interprets what is declared under `TelemetryData/`; it should not
change for routine schema work.

The collector ships three schemas:

- `TelemetryData/FillAssistRuleOutcome.cs` — `rule_id` keep, `success` keep, `domain` keep,
  `client_version_major` bucket by release year.
- `TelemetryData/GeneratedPassword.cs` — `kind`, the character-class flags, and `accepted` keep.
  `length` and `word_count` bucket so an exact length never fingerprints the record.
- `TelemetryData/AuthedGeneratedPassword.cs` — same shape as `GeneratedPassword` plus a `user_id`
  field marked `[Authed("sub")]`. Reported only from authenticated sessions. The client sends
  `user_id` as the plaintext id it claims this record belongs to. The collector rejects the record
  if that value does not equal the JWT's `sub` claim, and otherwise HMAC-pseudonymizes it. The
  wire output has the pseudonym, which gives research a per-user dimension without the client
  picking the identity unilaterally.

If a record carries a field the schema does not declare, the collector drops that field silently
and keeps the rest of the record, so older collectors stay forward-compatible with newer clients.

`[Pseudonymize]` is defined as a type-system option but is expected to be rare. The salt is
per-process and per-instance, so a pseudonym enables only within-boot correlation; for most
schemas, omitting the field from the POCO is strictly better. In this schema specifically,
pseudonymizing an installation id would let the sink correlate fill outcomes to a stable per-boot
identifier, which would leak the shape of one user's vault across their events.

Pseudonymization is HMAC-SHA256 under a 32-byte salt drawn once at boot. The salt lives in memory
only and is lost on restart. The collector drops and counts records with an unknown schema, an
undeclared field, malformed JSON, or over the record cap. The reason tag appears on
`telemetee.batch.records_dropped`.

## Batching and sink

Accepted records go into a fixed-capacity in-memory buffer (default 1024). A background flusher
drains the buffer every 30 seconds in arrival order, stamps each record with the flush time, and
posts the batch to Datadog's logs intake at `https://http-intake.logs.<site>/api/v2/logs`. A
failed post drops the batch; there is no retry.

The collector preserves arrival order so a downstream query that groups by any pseudonymized field
(user id, device id, whatever the researcher cares about at query time) sees that key's events in
the order they happened. Batch-time coarsening is the only timing granularity that leaves the
collector; the record carries no per-event timestamp, so an observer can order events within a
30-second window for a given pseudonym, not within a tighter interval.

Metrics on the `Bitwarden.Telemetee` meter. Every measurement carries a `collector_version` tag
equal to the collector's assembly version.

- `telemetee.pages` by outcome (`accepted`, `rejected_size`, `rejected_version`, `rejected_noise`,
  `malformed`).
- `telemetee.batch.records_accepted` and `telemetee.batch.records_dropped` by reason
  (`wrong_auth_scope`, `missing_auth_claim`, `claim_mismatch`, `auth_malformed`, plus the
  pre-existing reasons).
- `telemetee.batch.flushes` by outcome, `telemetee.batch.records_per_flush` histogram, and
  `telemetee.batch.sink_failures`.
- `telemetee.auth.groups` by outcome. The current implementation only emits `unverified`: tokens
  are parsed to extract claims but are not signature-verified. Phase B replaces this with concrete
  verify/reject outcomes once the JWKS fetch and `sig`/`exp`/`iss`/`aud` checks land.
- `telemetee.attestation.refreshes` by outcome.

The OTel export floor stays at 60 s so the metric cadence remains above the batch flush cadence.

### Why metrics, not logs

The purpose of the project is to anonymize data. Every templated log message has to be scrutinized
for security and privacy violations, while metrics can be reasoned about in aggregate and reused
many times.

Logs are fine on the sink side (ITelemetrySink post failures, flush exceptions) because the
content is already processed under the anonymization rules.

## Client bindings

`crates/telemetee-api/` is generated from the collector's OpenAPI document (`openapitools.json`
pins generator 7.15.0). Regenerate after you add or change a `[Telemetry]` POCO:

```sh
./support/generate-client-bindings.sh
```

The script boots the collector in Development on port 5050. The dev-only `/dev/telemetry`
endpoint is what anchors every `TelemetryRecord` subtype in the OpenAPI schema, so a Production
collector would emit a document that misses them. The script fetches `/openapi/v1.json` into
`artifacts/` and runs `openapi-generator-cli` with the Rust generator over
`crates/telemetee-api/`. It requires `java` and `npx` (both in the dev flake). Generated code is
committed; `artifacts/` is not.

`telemetee-client` re-exports the generated models as `telemetee_client::models`. The sole public
sender, [`CollectorClient::send`], is bound to the generated `TelemetryRecord` enum. The compiler
rejects any other type, so there is no path to put an unknown schema or raw bytes on the wire
from the public API. The raw page/byte path is crate-internal.

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

If two authed records use the same token, they share a single `auths` entry on the wire, so the
JWT is not repeated per event. Mismatched placement (an authed schema in an unauthed submission
or the reverse) is rejected server-side as `wrong_auth_scope`.

The CLI accepts flat JSON on the command line. `serde_json::from_str::<TelemetryRecord>` fails
early on an unknown `schema` discriminator or a missing required field, so bad input never
reaches the wire. Pass `--auth <jwt>` to submit the records as an authed group.

## Run locally

```sh
direnv allow
dotnet test telemetee.slnx
cargo test --workspace

ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Telemetee.Collector
```

Development mode replaces the sidecar with a self-signed issuer that mints MAA-shaped tokens.
Verify a token and send a page with the CLI. Pin the dev issuer and the dev hostdata from
`appsettings.Development.json`:

```sh
cargo run -p telemetee-cli -- \
  --base-url http://localhost:5000 --collector-version 0.1.0 \
  --issuer http://localhost:5000/dev/attestation \
  --hostdata deddeb25294cf93ccc3ce5ef00b2b07ec84d2a1e0fa356f35cbea30dbc1e4618 \
  send --record '{"schema":"fill_assist_rule_outcome","rule_id":"r1","success":true,"domain":"a.example","client_version_major":2025}'
```

Against a real deployment, `--issuer` is the Microsoft shared attestation provider the group was
configured with, and `--hostdata` is the value in the release artifact.

## Deploy

See [deploy/README.md](deploy/README.md) for the deployment steps, the rules for secrets in the
template, and how an outsider reproduces the hostdata from source. In short: run
`deploy/build-image.sh` for a reproducible image, run `deploy/generate-policy.sh <image@digest>`
for the policy and hostdata, and run `az deployment group create` with the generated template.
Pass the Datadog API key as a `datadogApiKey` parameter.

The value clients pin must come from the release workflow's artifact, not from a local run, but
anyone can reproduce it from the tagged source.
