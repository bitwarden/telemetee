using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.Sink;

/// <summary>Where a flushed batch goes after it leaves the collector.</summary>
public interface ITelemetrySink
{
    /// <summary>Returns true if the batch was accepted, false on any failure. No retry.</summary>
    Task<bool> SendAsync(
        IReadOnlyList<AnonymizedRecord> records,
        DateTimeOffset batchTime,
        CancellationToken cancellationToken);
}
