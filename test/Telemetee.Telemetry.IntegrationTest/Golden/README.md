# Parquet goldens — Data Services contract

Each `*.schema.txt` and `*.rows.txt` file in this directory documents the exact Parquet layout
`BlobStorageSink` emits for one telemetry schema. Bitwarden's Data Services team reads these
layouts directly in their ingestion jobs.

**A diff on any file in this directory is a contract change.** Before updating:

1. Confirm the change is intentional.
2. Notify Data Services — downstream ingestion jobs read this exact layout.
3. Regenerate the goldens: `TELEMETEE_UPDATE_GOLDENS=1 dotnet test test/Telemetee.Telemetry.IntegrationTest`
4. Review the diff on each affected file as part of code review.

The goldens are text projections rather than raw Parquet bytes because raw bytes are not
deterministic (writer version string and footer UUIDs vary across runs). The schema and data
projections are what Data Services actually sees after their reader opens the file.

See [docs/batching-and-sink.md § Parquet contract](../../../docs/batching-and-sink.md) for the
contract definition and the test infrastructure that enforces it.
