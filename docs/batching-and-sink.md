# Batching and sink

Records that pass schema application go into an in-memory buffer. A background flusher drains
the buffer on `FlushInterval` and writes Parquet to Azure Blob Storage.

## Buffer and flusher

By the time records reach the batch buffer, they are already typed `TelemetrySinkRecord`
instances. A fixed-capacity in-memory buffer holds them. A background flusher drains the
buffer on `FlushInterval` in arrival order. It stamps each record with `BatchTime` (the flush
instant) and `Seq` (a per-flush monotonic ordinal starting at 0), then hands the batch to the
Blob Storage sink.

`FlushInterval` is a privacy coarsening the collector guarantees, not downstream convenience.
Every row carries its flush's `BatchTime` as a Parquet column, and the record carries no
per-event timestamp. An observer can pin time down within one flush window and no tighter.
Within a single flush, `Seq` is the stable tie-breaker. It recovers arrival order if chunking
or the aged-tail sweep splits the flush across several blobs.

See [schemas.md § Does batch_time anonymize this schema?](schemas.md#does-batch_time-anonymize-this-schema)
for how reviewers should check that `FlushInterval` is actually doing anonymizing work for a
given schema.

## Sink

The sink groups by sink type and writes `TargetChunkRecords`-sized Parquet chunks as blobs at
`{container}/{schema}/{guid}.parquet`. It caches the trailing partial per sink type so the
next flush can roll it into a full chunk. A tail that never fills emits on its own once it
ages past `TailMaxDelay`. The Parquet schema per blob is derived from the sink type's
declared properties, so every blob of a given schema has the same columns in the same order
with no cross-batch inference.

## Settings

The pipeline splits settings along the privacy boundary.

- `TelemetryIngestionSettings` (section `TelemetryIngestion`) carries ingestion-side values
  that may affect anonymization.
- `BlobStorageSinkSettings` (section `BlobStorageSink`) carries sink-side values that only
  touch already-anonymized output.

## Metrics

Metrics are on the `Bitwarden.Telemetee` meter. Every measurement carries a
`collector_version` tag equal to the collector's assembly version.

- `telemetee.pages` by outcome: `accepted`, `rejected_size`, `rejected_version`,
  `rejected_noise`, `malformed`.
- `telemetee.batch.records_accepted` and `telemetee.batch.records_dropped` by reason:
  `wrong_auth_scope`, `missing_auth_claim`, `claim_mismatch`, `auth_malformed`, plus the
  pre-existing reasons.
- `telemetee.batch.flushes` by outcome: `sent`, `partial`, `failed`, `empty`.
  `telemetee.batch.records_per_flush` is a histogram. `telemetee.batch.sink_failures` counts
  one unit per lost record, not per failed flush.
- `telemetee.auth.groups` by outcome. The current implementation only emits `unverified`:
  tokens are parsed to extract claims but are not signature-verified. Phase B replaces this
  with concrete verify/reject outcomes once the JWKS fetch and `sig`/`exp`/`iss`/`aud`
  checks land.
- `telemetee.attestation.refreshes` by outcome.

See [decisions.md](decisions.md#why-metrics-not-logs) for why observability goes through
metrics rather than log messages.
