using System.Diagnostics.Metrics;

namespace Bit.Telemetee.Telemetry;

/// <summary>
/// Aggregate counters only. Nothing here may carry a value supplied by a client or identify a single
/// request, record, or page. Every metric must be derivable from what an observer already sees at the
/// edge (request counts, batch cadence) and must never be joinable to a single telemetry record.
/// </summary>
internal sealed class TelemetryMetrics
{
    public const string MeterName = "Bitwarden.Telemetee";

    private readonly Counter<long> _pages;
    private readonly Counter<long> _recordsAccepted;
    private readonly Counter<long> _recordsDropped;
    private readonly Counter<long> _flushes;
    private readonly Histogram<long> _recordsPerFlush;
    private readonly Counter<long> _sinkFailures;
    private readonly Counter<long> _authGroups;
    private readonly KeyValuePair<string, object?> _versionTag;

    public TelemetryMetrics(IMeterFactory meterFactory, string collectorVersion)
    {
        _versionTag = new KeyValuePair<string, object?>("collector_version", collectorVersion);
        var meter = meterFactory.Create(MeterName);
        _pages = meter.CreateCounter<long>(
            "telemetee.pages",
            unit: "{page}",
            description: "Noise pages received, by outcome.");
        _recordsAccepted = meter.CreateCounter<long>(
            "telemetee.batch.records_accepted",
            unit: "{record}",
            description: "Records that passed schema application and entered the batch buffer.");
        _recordsDropped = meter.CreateCounter<long>(
            "telemetee.batch.records_dropped",
            unit: "{record}",
            description: "Records that did not reach the batch buffer, by reason.");
        _flushes = meter.CreateCounter<long>(
            "telemetee.batch.flushes",
            unit: "{flush}",
            description: "Batch flushes attempted, by sink outcome.");
        _recordsPerFlush = meter.CreateHistogram<long>(
            "telemetee.batch.records_per_flush",
            unit: "{record}",
            description: "Record count observed on each flush, including empty flushes.");
        _sinkFailures = meter.CreateCounter<long>(
            "telemetee.batch.sink_failures",
            unit: "{failure}",
            description: "Flush posts the sink did not accept.");
        _authGroups = meter.CreateCounter<long>(
            "telemetee.auth.groups",
            unit: "{group}",
            description: "Authed groups processed, by outcome.");
    }

    /// <summary>
    /// Called when an authed group's token was parsed but not signature-verified. Phase B replaces
    /// this with concrete verify/reject outcomes.
    /// </summary>
    public void AuthGroupUnverified() =>
        _authGroups.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "unverified"));

    public void PageAccepted() => _pages.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "accepted"));
    public void PageRejectedSize() => _pages.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "rejected_size"));
    public void PageRejectedVersion() => _pages.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "rejected_version"));
    public void PageRejectedNoise() => _pages.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "rejected_noise"));
    public void PageMalformed() => _pages.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "malformed"));

    public void RecordAccepted() => _recordsAccepted.Add(1, _versionTag);

    public void RecordDropped(string reason) =>
        _recordsDropped.Add(1, _versionTag, new KeyValuePair<string, object?>("reason", reason));

    public void FlushSucceeded(int count)
    {
        _flushes.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "sent"));
        _recordsPerFlush.Record(count, _versionTag);
    }

    public void FlushFailed(int count)
    {
        _flushes.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "failed"));
        _recordsPerFlush.Record(count, _versionTag);
        _sinkFailures.Add(1, _versionTag);
    }

    public void FlushEmpty()
    {
        _flushes.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "empty"));
        _recordsPerFlush.Record(0, _versionTag);
    }
}
