using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.TelemetryData;

/// <summary>
/// A client's fill-assist rule either worked or failed on a page. The real event product wants
/// today: aggregate rule outcomes so broken rules surface. The matched domain is kept because the
/// question "which site is this rule broken on" cannot be answered otherwise, and the sink is
/// outside the privacy trust boundary.
/// </summary>
[Telemetry("fill_assist_rule_outcome")]
public sealed class FillAssistRuleOutcome : TelemetryRecord
{
    /// <summary>Identifier of the fill rule attempted. Kept so broken rules can be identified.</summary>
    [Keep]
    public string RuleId { get; init; } = string.Empty;

    /// <summary>Whether the rule produced a successful fill.</summary>
    [Keep]
    public bool Success { get; init; }

    /// <summary>eTLD+1 of the page. Kept, since triage needs the site.</summary>
    [Keep]
    public string Domain { get; init; } = string.Empty;

    /// <summary>Client major version. Bucketed by release year.</summary>
    [Bucket(nameof(ClientVersionMajorBuckets))]
    public int ClientVersionMajor { get; init; }

    public static readonly IBucketer<int> ClientVersionMajorBuckets = new NumericRangeBucketer<int>(
        new NumericRange("<2024", null, 2024),
        new NumericRange("2024", 2024, 2025),
        new NumericRange("2025", 2025, 2026),
        new NumericRange("2026+", 2026, null));

    /// <summary>Sink shape for <see cref="FillAssistRuleOutcome"/>.</summary>
    public sealed class Sink : TelemetrySinkRecord, IRetained
    {
        public static TimeSpan RetentionTime => TimeSpan.FromYears(3);

        public required string RuleId { get; init; }
        public required bool Success { get; init; }
        public required string Domain { get; init; }
        public required string ClientVersionMajor { get; init; }
    }
}
