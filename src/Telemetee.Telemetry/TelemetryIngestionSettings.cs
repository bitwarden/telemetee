using System.ComponentModel.DataAnnotations;

namespace Bit.Telemetee.Telemetry;

/// <summary>
/// Operator-tunable knobs for the ingestion half of the pipeline: the in-memory buffer that holds
/// accepted records between flushes and the cadence the flusher drains it on. These values live
/// inside the privacy trust boundary — changing them can affect what the collector guarantees
/// about anonymization — so each knob is reviewed on that basis.
/// </summary>
public sealed class TelemetryIngestionSettings
{
    /// <summary>
    /// How often the collector drains the buffer and posts to the sink. Also the privacy coarsening
    /// floor: every emitted row carries only the flush time as its timestamp, so an observer who
    /// groups by a pseudonymized field can order an entity's events within one flush window and no
    /// tighter. Pinned as a literal in the CCE policy for that reason.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "01:00:00")]
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum records held between flushes; excess records are dropped and counted. Operator-tunable
    /// because changing it only affects drop rate under load, not what the collector does with any
    /// retained record.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int BufferCapacity { get; set; } = 1024;
}
