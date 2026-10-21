using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.Sink;

/// <summary>Where a flushed batch goes after it leaves the collector.</summary>
public interface ITelemetrySink
{
    /// <summary>
    /// Called by the flusher on every tick. <paramref name="records"/> may be empty.
    /// Returns the per-call accounting of how many records landed and how many were lost.
    /// A sink that carries records across flushes (for example a tail cache) may process
    /// records from prior flushes on this call, so <c>Written + Lost</c> can exceed
    /// <paramref name="records"/>.Count. No retry.
    /// </summary>
    Task<SinkOutcome> SendAsync(
        IReadOnlyList<AnonymizedRecord> records,
        DateTimeOffset batchTime,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-call accounting from <see cref="ITelemetrySink.SendAsync"/>: records that reached the
/// sink's destination and records the sink attempted but could not deliver.
/// </summary>
public readonly record struct SinkOutcome(int Written, int Lost);
