# telemetee

Anonymizing telemetry collector for Bitwarden clients, hosted in a Confidential ACI container that
clients verify by attestation before sending anything. Design notes live in the research directory
(`claude-research/anon-telemetry`); this repository is the implementation.

Current state: milestone 2, the anonymized telemetry path. On top of the attested Noise channel from
milestone 1, the collector now accepts fixed-size padded pages, applies a per-field schema
(`keep`, `bucket`, `pseudonymize`, `authed`) with HMAC-SHA256 pseudonymization under a boot-time
salt, batches accepted records in memory, stamps batch time, and posts to Datadog in arrival order
so downstream research queries can sequence a user's events by grouping on any pseudonymized field.

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

Endpoints live at the root. The deployment (reverse proxy, DNS, or separate deployments per build)
is what maps a client to a specific collector version; the collector itself hosts one version at a
time. See [Why version lives outside the API](#why-version-lives-outside-the-api).

| Method | Path                     | Purpose                                                                                                                       |
| ------ | ------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| GET    | `/attestation`           | `{ "token": "<MAA JWT>" }` with `x-ms-runtime.noise_pub`                                                                      |
| POST   | `/telemetry`             | Exactly one 4096-byte NK message 1 page in, exactly 81 bytes of empty NK message 2 out                                        |
| GET    | `/dev/attestation/certs` | JWKS for the development issuer only (not registered in Production)                                                           |
| POST   | `/dev/telemetry`         | Plaintext unauthed `TelemetryRecord` in, anonymized output back (not registered in Production)                                |
| POST   | `/dev/telemetry/authed`  | Same, plus `Authorization: Bearer <jwt>`; claims are parsed and `[Authed]` fields are verified (not registered in Production) |

Plus `/version`, `/healthz`, and `/openapi/v1.json`.

Every `POST /telemetry` must carry `Telemetee-Target-Version` equal to the collector's assembly
version. The collector rejects a mismatched or missing header with 400 before any crypto work and
counts it on `telemetee.pages` under outcome `rejected_version`. The handler never sees a page from
a client that disagrees about the version, so field semantics cannot drift silently if a schema
changes.

### Why version lives outside the API

A collector pinned by attestation is pinned to one build. Each client holds a CCE policy hash
(`x-ms-sevsnpvm-hostdata`) specific to the build it was shipped against; an older client reaching a
newer collector cannot trust the newer attestation because it does not know the newer measurement.
Routing multiple versions inside one API would route a stale client to a build it has no basis to
trust.

An external transparency log for build-to-measurement mappings would let a client discover and
trust a newer measurement at request time, at which point multiple API versions could live behind
one collector again. Until then, versioning is a deployment-layer concern and the in-protocol
version check exists only to catch misconfigured infrastructure.

## Page and record format

- One POST = one page = one NK handshake. Request body is exactly 4096 bytes; the collector rejects
  any other length before running the handshake, so the edge sees a constant request size.
- Response is exactly 81 bytes (`PageFormat.ResponseLength`) whether the handshake succeeded, every
  record was accepted, every record was dropped, or the plaintext was all padding, so the response
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
  Each event is a CBOR map with a `schema` discriminator and the schema's fields. `events` is in
  strict submission order. Same-token authed records share a single entry in `auths` so the
  ~700-byte JWT is written once per user per page, not once per record.
- Max record length is 1024 bytes. A record over the cap is dropped and counted; the walk continues.
- Oversized records are also dropped client-side before any crypto work and returned to the
  caller in `SendOutcome.oversize_dropped`.

## Schemas

Schemas are declared as attributed POCOs under `src/Telemetee.Telemetry/TelemetryData/`. One POCO
per schema; the type inherits from `TelemetryRecord`, the `[Telemetry("name")]` attribute names it
on the wire, and every property carries one of `[Keep]`, `[Pseudonymize]`,
`[Authed("<jwt-claim>")]`, or `[Bucket(nameof(...))]`. A schema is "authed" iff it declares any
`[Authed]` field; its records are only accepted inside an authed group on the page. For each
`[Authed]` field, the client sends the plaintext value it asserts; the collector rejects the
record if that value does not equal the named JWT claim, and otherwise HMAC-pseudonymizes it
under the per-boot salt before anything reaches the sink. Properties map PascalCase → snake_case on the wire. The schema set and the
System.Text.Json polymorphism derived-type set are both discovered at startup by scanning the
assembly for `[Telemetry]`-marked subtypes of `TelemetryRecord`, so adding a telemetry type is just
dropping a POCO into `TelemetryData/` — no index to update, and the type immediately shows up in the
OpenAPI document and generated client bindings. The code under `Schemas/` is machinery that
interprets what is declared under `TelemetryData/` and should not change for routine schema work.
The collector ships three schemas:

- `TelemetryData/FillAssistRuleOutcome.cs` — `rule_id` keep, `success` keep, `domain` keep,
  `client_version_major` bucket by release year.
- `TelemetryData/GeneratedPassword.cs` — `kind` / character-class flags / `accepted` keep,
  `length` and `word_count` bucket so an exact length never fingerprints the record.
- `TelemetryData/AuthedGeneratedPassword.cs` — same shape as `GeneratedPassword` plus a `user_id`
  field marked `[Authed("sub")]`. Reported only from authenticated sessions. The client sends
  `user_id` as the plaintext id it claims this record belongs to; the collector rejects the record
  if that value does not equal the JWT's `sub` claim and otherwise HMAC-pseudonymizes it. The
  wire output has the pseudonym, giving research a per-user dimension without the client getting
  to pick the identity unilaterally.

A record that carries a field the schema does not declare has that field silently dropped and the
rest of the record kept, so older collectors stay forward-compatible with newer clients.

`[Pseudonymize]` is defined as a type-system option but is expected to be rare. The salt is
per-process and per-instance, so a pseudonym only enables within-boot correlation; in most schemas
omitting the field from the POCO entirely is strictly better. In this schema specifically,
pseudonymizing an installation id would let the sink correlate fill outcomes to a stable per-boot
identifier, which would leak the shape of one user's vault across their events.

Pseudonymization is HMAC-SHA256 under a 32-byte salt drawn once at boot. The salt lives in memory
only and is lost on restart. Records with an unknown schema, an undeclared field, malformed JSON, or
over the record cap are dropped and counted on `telemetee.batch.records_dropped` with a reason tag.

## Batching and sink

Accepted records go into a fixed-capacity in-memory buffer (default 1024). A background flusher
drains the buffer every 30 seconds in arrival order, stamps each record with the flush time, and
posts the batch to Datadog's logs intake at `https://http-intake.logs.<site>/api/v2/logs`. A failed
post drops the batch; there is no retry.

Arrival order is preserved so a downstream query that groups by any pseudonymized field (user id,
device id, whatever the researcher cares about at query time) sees that key's events in the order
they happened. Batch-time coarsening is the only timing granularity that leaves the collector — the
record carries no per-event timestamp — so an observer can order events within a 30-second window
for a given pseudonym, not within a tighter interval.

Metrics on the `Bitwarden.Telemetee` meter. Every measurement carries a `collector_version` tag
equal to the collector's assembly version.

- `telemetee.pages` by outcome (`accepted`, `rejected_size`, `rejected_version`, `rejected_noise`,
  `malformed`).
- `telemetee.batch.records_accepted` and `telemetee.batch.records_dropped` by reason
  (`wrong_auth_scope`, `missing_auth_claim`, `claim_mismatch`, `auth_malformed`, plus the
  pre-existing reasons).
- `telemetee.batch.flushes` by outcome, `telemetee.batch.records_per_flush` histogram, and
  `telemetee.batch.sink_failures`.
- `telemetee.auth.groups` by outcome. Current implementation only emits `unverified` — tokens are
  parsed to extract claims but not signature-verified. Phase B replaces this with concrete
  verify/reject outcomes once the JWKS fetch and `sig`/`exp`/`iss`/`aud` checks land.
- `telemetee.attestation.refreshes` by outcome.

The OTel export floor stays at 60 s so metric cadence remains above the batch flush cadence.

### Why metrics, not logs

The purpose of the project is to anonymize data. Templated log messages all have to be scrutinized for
security and privacy violations, while metrics can be reasoned about in aggregate and used many times.

Logs are fine on the sink side (ITelemetrySink post failures, flush exceptions) where the content already
processed according to anonymization rules.

## Client bindings

`crates/telemetee-api/` is generated from the collector's OpenAPI document (`openapitools.json`
pins generator 7.15.0). Regenerate after adding or changing a `[Telemetry]` POCO:

```sh
./support/generate-client-bindings.sh
```

The script boots the collector in Development on port 5050 — the dev-only `/dev/telemetry`
endpoint is what anchors every `TelemetryRecord` subtype in the OpenAPI schema, so a Production
collector would emit a document missing them — fetches `/openapi/v1.json` into `artifacts/`, and
runs `openapi-generator-cli` with the Rust generator over `crates/telemetee-api/`. Requires
`java` and `npx` (both in the dev flake). Generated code is committed; `artifacts/` is not.

`telemetee-client` re-exports the generated models as `telemetee_client::models`. The sole public
sender, [`CollectorClient::send`], is bound to the generated `TelemetryRecord` enum — the compiler
rejects any other type, so there is no path to put an unknown schema or raw bytes on the wire from
the public API. The raw page/byte path is crate-internal.

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

Same-token authed records share a single `auths` entry on the wire so the JWT isn't repeated per
event. Mismatched placement (an authed schema in an unauthed submission or vice versa) is
rejected server-side as `wrong_auth_scope`.

The CLI accepts flat JSON on the command line and `serde_json::from_str::<TelemetryRecord>` fails
early on an unknown `schema` discriminator or missing required fields, so bad input never reaches
the wire. Pass `--auth <jwt>` to submit the records as an authed group.

## Run locally

```sh
direnv allow
dotnet test telemetee.slnx
cargo test --workspace

ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Telemetee.Collector
```

Development mode swaps the sidecar for a self-signed issuer that mints MAA-shaped tokens. Verify a
token and send a page with the CLI, pinning the dev issuer and the dev hostdata from
`appsettings.Development.json`:

```sh
cargo run -p telemetee-cli -- \
  --base-url http://localhost:5000 --collector-version 0.1.0 \
  --issuer http://localhost:5000/dev/attestation \
  --hostdata deddeb25294cf93ccc3ce5ef00b2b07ec84d2a1e0fa356f35cbea30dbc1e4618 \
  send --record '{"schema":"fill_assist_rule_outcome","rule_id":"r1","success":true,"domain":"a.example","client_version_major":2025}'
```

Against a real deployment, `--issuer` is the Microsoft shared attestation provider the group was
configured with and `--hostdata` is the value in the release artifact.

## Deploy

See [deploy/README.md](deploy/README.md) for the deployment steps, the rules for secrets in the
template, and how an outsider reproduces the hostdata from source. In short: `deploy/build-image.sh`
for a reproducible image, `deploy/generate-policy.sh <image@digest>` for the policy and hostdata,
`az deployment group create` with the generated template, passing the Datadog API key as a
`datadogApiKey` parameter.

The value clients pin must come from the release workflow's artifact, not from a local run, but
anyone can reproduce it from the tagged source.
