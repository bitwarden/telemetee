using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Bit.Telemetee.Collector;

/// <summary>
/// The only OpenTelemetry pipeline in this service. It exports the libraries' own aggregate meter and
/// nothing else: no request instrumentation, no traces, no logs.
/// </summary>
internal static class CollectorMetricsExtensions
{
    private const string MeterName = "Bitwarden.Telemetee";

    /// <summary>
    /// Floor for the export interval. It must never be shorter than the batching window, or exported
    /// counts would reveal arrival timing more precisely than the sink hop does.
    /// </summary>
    private static readonly TimeSpan ExportInterval = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddCollectorMetrics(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(environment.ApplicationName))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(MeterName);
                if (!string.IsNullOrEmpty(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                {
                    metrics.AddOtlpExporter((_, reader) =>
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            (int)ExportInterval.TotalMilliseconds);
                }
            });
        return services;
    }
}
