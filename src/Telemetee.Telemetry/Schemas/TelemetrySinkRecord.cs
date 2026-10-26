namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Base for every sink-side record. Each concrete subtype is paired with one input
/// <see cref="TelemetryData.TelemetryRecord"/> and declares the exact shape that reaches storage
/// after field treatments run. The subtype also implements <see cref="IRetained"/> to commit a
/// retention time.
///
/// <see cref="BatchTime"/> and <see cref="Seq"/> are assigned by the batcher at flush, not by the
/// compiled process.
/// </summary>
public abstract class TelemetrySinkRecord
{
    private DateTime? _batchTime;

    /// <summary>
    /// Flush instant the record belongs to, in UTC. Stored as <see cref="DateTime"/> rather than
    /// <see cref="DateTimeOffset"/> because Parquet.Net's reflection serializer expands
    /// <see cref="DateTimeOffset"/> into every public sub-property (DateTime, UtcDateTime, Ticks,
    /// Year, …) and we want exactly one column. The setter normalizes <see cref="DateTimeKind"/>
    /// to <see cref="DateTimeKind.Utc"/> so Parquet writes <c>isAdjustedToUtc=true</c>; passing a
    /// <see cref="DateTimeKind.Local"/> value is rejected so a stray <c>DateTime.Now</c> cannot
    /// silently poison the Parquet metadata.
    /// </summary>
    public DateTime? BatchTime
    {
        get => _batchTime;
        set => _batchTime = value switch
        {
            null => null,
            { Kind: DateTimeKind.Local } => throw new ArgumentException(
                "BatchTime must be UTC; got a Local DateTime. Convert with .ToUniversalTime() at the call site.",
                nameof(value)),
            { Kind: DateTimeKind.Utc } v => v,
            var v => DateTime.SpecifyKind(v.Value, DateTimeKind.Utc),
        };
    }

    /// <summary>
    /// per-flush monotonic ordinal that preserves arrival order across chunks (chunking, aged-tail
    /// sweeps, or future retries can split a single flush across several blobs, so a timestamp alone
    /// cannot order rows within a flush).
    /// </summary>
    public long? Seq { get; set; }
}
