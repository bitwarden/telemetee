#!/usr/bin/env bash
# Regenerates crates/telemetee-api/ from the collector's OpenAPI document.
#
# 1. Boots the collector in Development so the diagnostic /dev/telemetry endpoint anchors every
#    TelemetryRecord subtype in the OpenAPI schema (production deployments do not mount it, so a
#    production collector's /openapi/v1.json would be missing the telemetry schemas).
# 2. Fetches /openapi/v1.json into artifacts/openapi.json.
# 3. Runs openapi-generator-cli over it to produce crates/telemetee-api/.
#
# Requires `java` and `npx` on PATH (both provided by the project's dev flake).

set -euo pipefail

cd "$(dirname "$0")/.."

ARTIFACTS=./artifacts
OUT=./crates/telemetee-api
PORT=5050

mkdir -p "$ARTIFACTS"

echo "==> Booting collector on port $PORT"
ASPNETCORE_URLS="http://127.0.0.1:$PORT" \
    ASPNETCORE_ENVIRONMENT=Development \
    dotnet run --project src/Telemetee.Collector --no-launch-profile >"$ARTIFACTS/collector.log" 2>&1 &
PID=$!
trap 'kill "$PID" 2>/dev/null; wait "$PID" 2>/dev/null || true' EXIT

echo "==> Waiting for /openapi/v1.json"
for _ in $(seq 1 40); do
    if curl -sf "http://127.0.0.1:$PORT/openapi/v1.json" -o "$ARTIFACTS/openapi.json"; then
        break
    fi
    sleep 0.5
done
test -s "$ARTIFACTS/openapi.json" || { echo "collector did not serve openapi"; tail -40 "$ARTIFACTS/collector.log"; exit 1; }
echo "    got $(wc -c <"$ARTIFACTS/openapi.json") bytes"

echo "==> Running openapi-generator-cli"
rm -rf "$OUT/src"
npx @openapitools/openapi-generator-cli generate \
    -i "$ARTIFACTS/openapi.json" \
    -g rust \
    -o "$OUT" \
    --package-name telemetee-api \
    --additional-properties="library=reqwest,packageVersion=0.1.0,packageDescription=\"Generated client types for the Bitwarden telemetee collector.\""

echo "==> cargo fmt"
cargo fmt --manifest-path "$OUT/Cargo.toml"

echo "==> Done. Review changes with: git diff $OUT"
