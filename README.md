# telemetee

Anonymizing telemetry collector for Bitwarden clients, hosted in a Confidential ACI container that
clients verify by attestation before sending anything. Design notes live in the research directory
(`claude-research/anon-telemetry`); this repository is the implementation.

Current state: milestone 1, the attested Noise channel. The collector serves its MAA token, binds its
Noise static key into that token, and completes `Noise_NK_P256_AESGCM_SHA256` handshakes. No
telemetry schemas, padding, batching, or sink yet.

## Layout

```
src/Telemetee.Collector/       ASP.NET Core host (Bitwarden.Server.Sdk, minimal APIs)
src/Telemetee.Attestation/     MAA token acquisition via the aci/skr sidecar, plus a dev issuer
src/Telemetee.NoiseChannel/    Noise NK responder over BCL crypto, static key, echo endpoint
test/                          xunit projects per library plus a WebApplicationFactory suite
crates/telemetee-client/       Rust client: token verification and NK initiator (snow)
crates/telemetee-cli/          Verifier CLI wrapping the client crate
deploy/                        ACI ARM template and CCE policy generation
```

The Rust crate links to `../sdk-internal` by path so it can move there unchanged. It uses the SDK's
`bitwarden-random` snow resolver, the same way `bitwarden-ipc` does.

## Endpoints

All under `/<version>/`, where `<version>` is the exact assembly version. Clients route on
it and pin a CCE policy hash per version.

| Method | Path                    | Purpose                                                  |
| ------ | ----------------------- | -------------------------------------------------------- |
| GET    | `attestation`           | `{ "token": "<MAA JWT>" }` with `x-ms-runtime.noise_pub` |
| POST   | `echo`                  | Noise NK message 1 in, message 2 out, payload echoed     |
| GET    | `dev-attestation/certs` | JWKS for the development issuer only                     |

Plus `/version`, `/healthz`, and `/openapi/v1.json`.

## Run locally

```sh
direnv allow
dotnet test telemetee.slnx
cargo test --workspace

ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Telemetee.Collector
```

Development mode swaps the sidecar for a self-signed issuer that mints MAA-shaped tokens. Verify it
with the CLI, pinning the dev issuer and the dev hostdata from `appsettings.Development.json`:

```sh
cargo run -p telemetee-cli -- \
  --base-url http://localhost:5000 --collector-version 0.1.0 \
  --issuer http://localhost:5000/0.1.0/dev-attestation \
  --hostdata deddeb25294cf93ccc3ce5ef00b2b07ec84d2a1e0fa356f35cbea30dbc1e4618 \
  echo "hello"
```

Against a real deployment, `--issuer` is the Microsoft shared attestation provider the group was
configured with and `--hostdata` is the value in the release artifact.

## Deploy

See [deploy/README.md](deploy/README.md) for the step-by-step experiment deployment, the rules for
secrets in the template, and how an outsider reproduces the hostdata from source. In short:
`deploy/build-image.sh` for a reproducible image, `deploy/generate-policy.sh <image@digest>` for
the policy and hostdata, `az deployment group create` with the generated template.

The value clients pin must come from the release workflow's artifact, not from a local run, but
anyone can reproduce it from the tagged source.
