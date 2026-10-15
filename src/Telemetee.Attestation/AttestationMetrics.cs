using System.Diagnostics.Metrics;

namespace Bit.Telemetee.Attestation;

/// <summary>Aggregate counters only; see the same note on the Noise channel's metrics.</summary>
internal sealed class AttestationMetrics
{
    public const string MeterName = "Bitwarden.Telemetee";

    private readonly Counter<long> _refreshes;
    private readonly KeyValuePair<string, object?> _versionTag;

    public AttestationMetrics(IMeterFactory meterFactory, string collectorVersion)
    {
        _versionTag = new KeyValuePair<string, object?>("collector_version", collectorVersion);
        var meter = meterFactory.Create(MeterName);
        _refreshes = meter.CreateCounter<long>(
            "telemetee.attestation.refreshes",
            unit: "{refresh}",
            description: "Attestation token requests to the sidecar, by outcome.");
    }

    public void Refreshed() => _refreshes.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "ok"));

    public void RefreshFailed() => _refreshes.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "error"));
}
