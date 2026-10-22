namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Base for every sink-side record. Each concrete subtype is paired with one input
/// <see cref="TelemetryData.TelemetryRecord"/> and declares the exact shape that reaches storage
/// after field treatments run. The subtype also implements <see cref="IRetained"/> to commit a
/// retention time.
///
/// <see cref="BatchTime"/> and <see cref="Seq"/> are assigned by the batcher at flush, not by the
/// compiled process.
public abstract class TelemetrySinkRecord
{
    /// <summary>
    /// flush instant the record belongs to.
    /// </summary>
    public DateTimeOffset? BatchTime { get; set; }
    /// <summary>
    /// per-flush monotonic ordinal that preserves arrival order across chunks (chunking, aged-tail
    /// sweeps, or future retries can split a single flush across several blobs, so a timestamp alone
    /// cannot order rows within a flush).
    /// </summary>
    public long? Seq { get; set; }
}
