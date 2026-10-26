using System.Diagnostics.Metrics;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>Aggregate counters only; see the same note on the attestation metrics.</summary>
internal sealed class NoiseChannelMetrics
{
    public const string MeterName = "Bitwarden.Telemetee";

    private readonly Counter<long> _rotations;
    private readonly KeyValuePair<string, object?> _versionTag;

    public NoiseChannelMetrics(IMeterFactory meterFactory, string collectorVersion)
    {
        _versionTag = new KeyValuePair<string, object?>("collector_version", collectorVersion);
        var meter = meterFactory.Create(MeterName);
        _rotations = meter.CreateCounter<long>(
            "telemetee.noise.key_rotations",
            unit: "{rotation}",
            description: "Noise static key rotations, by outcome.");
    }

    public void Rotated() => _rotations.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "ok"));

    public void RotationFailed() => _rotations.Add(1, _versionTag, new KeyValuePair<string, object?>("outcome", "error"));
}
