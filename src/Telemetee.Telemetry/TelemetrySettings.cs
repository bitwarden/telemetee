namespace Bit.Telemetee.Telemetry;

/// <summary>Operator-tunable knobs for the batching and sink pipeline.</summary>
public sealed class TelemetrySettings
{
    /// <summary>How often the collector drains the buffer and posts to the sink.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum records held between flushes; excess records are dropped and counted.</summary>
    public int BufferCapacity { get; set; } = 1024;

    /// <summary>Datadog logs intake site, e.g. <c>datadoghq.com</c> or <c>datadoghq.eu</c>.</summary>
    public string DatadogSite { get; set; } = "datadoghq.com";

    /// <summary>Datadog API key. Comes from an ARM securestring parameter; see deploy/README.md.</summary>
    public string? DatadogApiKey { get; set; }

    /// <summary>Datadog <c>ddsource</c> tag applied to every record.</summary>
    public string DatadogSource { get; set; } = "telemetee";

    /// <summary>Datadog <c>service</c> tag applied to every record.</summary>
    public string DatadogService { get; set; } = "telemetee-collector";
}
