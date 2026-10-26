# telemetee

Anonymizing telemetry collector for Bitwarden clients. The collector runs in a Confidential ACI
container. Each client verifies the container by attestation before it sends anything.

Current state is milestone 2: the anonymizing pipeline running on milestone 1's attested Noise
channel.

## Documentation

- [docs/architecture.md](docs/architecture.md) — the request path: attestation, Noise
  handshake, CBOR page layout, endpoints, version header.
- [docs/schemas.md](docs/schemas.md) — the schema model, how to add a telemetry type, and the
  review discipline every new schema has to pass.
- [docs/batching-and-sink.md](docs/batching-and-sink.md) — the in-memory buffer, flush
  behavior, Blob Storage layout, and metrics.
- [docs/client-bindings.md](docs/client-bindings.md) — the generated Rust API crate, the Rust
  client, and the CLI.
- [docs/decisions.md](docs/decisions.md) — architectural decisions and rationale.
- [deploy/README.md](deploy/README.md) — how to deploy a reproducible build to Confidential
  ACI.

## Repository layout

```
src/Telemetee.Collector/       ASP.NET Core host (Bitwarden.Server.Sdk, minimal APIs)
src/Telemetee.Attestation/     MAA token acquisition via the aci/skr sidecar, plus a dev issuer
src/Telemetee.NoiseChannel/    Noise NK responder over BCL crypto, static key
src/Telemetee.Telemetry/       Page/record format, schema machinery, batch buffer, Blob Storage sink
  TelemetryData/               The data: one attributed POCO per telemetry type
test/                          xunit projects per library plus a WebApplicationFactory suite
crates/telemetee-api/          Generated Rust types for every [Telemetry] POCO
crates/telemetee-client/       Rust client: token verification, NK initiator, page builder
crates/telemetee-cli/          Verifier and sender CLI wrapping the client crate
deploy/                        ACI ARM template and CCE policy generation
```

## Build and test

Required tools:

- .NET SDK `10.0.100` (pinned in [global.json](global.json)).
- Rust `1.97.1`, edition 2024 (pinned in [Cargo.toml](Cargo.toml)).
- `sdk-internal` checked out as a sibling of this repo (`../sdk-internal`). The Rust
  workspace path-depends on `bitwarden-random` from it.
- Docker, for the `Telemetee.Telemetry.IntegrationTest` suite. That project spins up an
  Azurite container through Testcontainers to verify the Parquet contract with Data
  Services. Without Docker, `dotnet test telemetee.slnx` fails in that project; run
  `dotnet test` on the other test projects individually to skip it.

Build and test:

```sh
dotnet test telemetee.slnx
cargo test --workspace
```

## Run locally

```sh
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

Against a real deployment, `--issuer` is the Microsoft shared attestation provider the group
was configured with, and `--hostdata` is the value in the release artifact.

## Contributing

Bitwarden-wide contribution standards live at
<https://contributing.bitwarden.com/>. Repo-specific conventions:

- Telemetry data POCOs live under `src/Telemetee.Telemetry/TelemetryData/`. Machinery lives
  under `src/Telemetee.Telemetry/Schemas/`. Routine schema additions never touch `Schemas/`.
- Every new telemetry type passes the review discipline in
  [docs/schemas.md § Reviewing a new schema](docs/schemas.md#reviewing-a-new-schema) before
  merge.
- After adding or changing a `[Telemetry]` POCO, regenerate the Rust bindings; see
  [docs/client-bindings.md](docs/client-bindings.md).
