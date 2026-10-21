using System.ComponentModel.DataAnnotations;

namespace Bit.Telemetee.Telemetry.Sink;

/// <summary>
/// Operator-tunable knobs for the Blob Storage sink. Anonymization runs upstream before anything
/// reaches the sink so these values are purely operational and freely tunable by the operator.
/// </summary>
public sealed class BlobStorageSinkSettings
{
    /// <summary>
    /// Sink-side cap on records per emitted Parquet blob. The sink groups a flushed batch by
    /// schema, prepends any cached tail from previous flushes, and splits the combined records
    /// into chunks of this size. Full chunks write immediately; the final partial per schema goes
    /// back into the per-schema tail cache for the next flush to absorb. Each row carries its
    /// original flush's <c>batch_time</c>, so chunking does not loosen the privacy coarsening.
    /// Exists to keep blob sizes uniform for downstream data lake tooling that pays per-file
    /// overhead regardless of size.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int TargetChunkRecords { get; set; } = 512;

    /// <summary>
    /// Upper bound on how long a cached tail can sit before the sweep emits it as a partial blob.
    /// Protects quiet schemas from starving when traffic never fills a full chunk. The sink is
    /// swept on every flusher tick, so the actual delay is at most this plus one tick.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.0010000", "01:00:00")]
    public TimeSpan TailMaxDelay { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Azure Storage connection string for the destination account. An empty value drops every flushed
    /// batch and logs a warning (useful for local dev where no account is configured); format is
    /// validated by the Azure SDK on first use rather than at startup, so a malformed value surfaces
    /// as a sink exception. The sink uses the Blob SDK, which addresses both plain Azure Blob Storage
    /// and ADLS Gen2 accounts.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Container inside the storage account that receives flushed batches. Each schema becomes a
    /// virtual directory beneath it: <c>{container}/{schema}/{filename}</c>. The length and
    /// pattern bounds mirror Azure's container-name rules so a bad name fails host startup
    /// instead of first blob write; the regex does not reject consecutive hyphens, which Azure
    /// also disallows but which is cheap for the service to reject on container create.
    /// </summary>
    [MinLength(3)]
    [MaxLength(63)]
    [RegularExpression("^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
    public string Container { get; set; } = "telemetry";
}
