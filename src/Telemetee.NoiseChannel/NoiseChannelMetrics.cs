using System.Diagnostics.Metrics;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>
/// Aggregate counters only. Nothing here may carry a value supplied by a client or identify a single
/// request; the collector exists to strip that information.
/// </summary>
internal sealed class NoiseChannelMetrics
{
    public const string MeterName = "Bitwarden.Telemetee";

    private readonly Counter<long> _pages;

    public NoiseChannelMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _pages = meter.CreateCounter<long>(
            "telemetee.pages",
            unit: "{page}",
            description: "Noise messages received, by outcome.");
    }

    public void Accepted() => _pages.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));

    public void RejectedNoise() => _pages.Add(1, new KeyValuePair<string, object?>("outcome", "rejected_noise"));

    public void RejectedSize() => _pages.Add(1, new KeyValuePair<string, object?>("outcome", "rejected_size"));
}
