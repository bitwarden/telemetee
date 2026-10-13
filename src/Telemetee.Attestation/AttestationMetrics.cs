using System.Diagnostics.Metrics;

namespace Bit.Telemetee.Attestation;

/// <summary>Aggregate counters only; see the same note on the Noise channel's metrics.</summary>
internal sealed class AttestationMetrics
{
    public const string MeterName = "Bitwarden.Telemetee";

    private readonly Counter<long> _refreshes;

    public AttestationMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _refreshes = meter.CreateCounter<long>(
            "telemetee.attestation.refreshes",
            unit: "{refresh}",
            description: "Attestation token requests to the sidecar, by outcome.");
    }

    public void Refreshed() => _refreshes.Add(1, new KeyValuePair<string, object?>("outcome", "ok"));

    public void RefreshFailed() => _refreshes.Add(1, new KeyValuePair<string, object?>("outcome", "error"));
}
