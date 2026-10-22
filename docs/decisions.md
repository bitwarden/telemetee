# Design decisions

Rationale for choices that a reader might otherwise reach for the opposite of.

## Why version lives outside the API

Attestation pins a collector to one build. Each client holds a CCE policy hash
(`x-ms-sevsnpvm-hostdata`) specific to the build it shipped against. An older client that
reaches a newer collector cannot trust the newer attestation because it does not know the
newer measurement. Routing multiple versions inside one API would route a stale client to a
build it has no basis to trust.

An external transparency log that maps builds to measurements would let a client discover and
trust a newer measurement at request time. Multiple API versions could then live behind one
collector again. Until then, versioning is a deployment-layer concern. The in-protocol
`Telemetee-Target-Version` check exists only to catch misconfigured infrastructure.

See [architecture.md § Version header](architecture.md#version-header) for the header behavior.

## Why metrics, not logs

The purpose of the project is to anonymize data. Every templated log message has to be
scrutinized for security and privacy violations. Metrics can be reasoned about in aggregate
and reused many times.

Logs are fine on the sink side (`ITelemetrySink` post failures, flush exceptions) because the
content is already processed under the anonymization rules.

See [batching-and-sink.md § Metrics](batching-and-sink.md#metrics) for the metric set.
