using System.Diagnostics.Metrics;
using System.Text.Json.Serialization.Metadata;
using Bit.Telemetee.Telemetry.Batch;
using Bit.Telemetee.Telemetry.Pseudonymization;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.Sink;
using Bit.Telemetee.Telemetry.TelemetryData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Telemetry;

/// <summary>Registration entry point for the telemetry pipeline.</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the schema catalog (built from every <c>[Telemetry]</c>-marked POCO in this
    /// assembly), the pseudonym salt factory, the batch buffer, the Blob Storage sink, and the
    /// background flusher.
    ///
    /// Schemas are discovered and validated eagerly, inside this call, so a bad POCO (missing
    /// treatment, property-name snake_case collision, missing nested Sink, bad [Bucket] member,
    /// treatment/sink-type mismatch, duplicate [Telemetry] name) throws at service registration
    /// time rather than at the first request.
    /// </summary>
    public static IServiceCollection AddTelemetry(this IServiceCollection services, string collectorVersion)
    {
        var assembly = typeof(TelemetryServiceCollectionExtensions).Assembly;
        var polymorphismModifier = TelemetryRecordTypeResolver.ForAssembly(assembly);
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            var resolver = (options.SerializerOptions.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver());
            if (resolver is DefaultJsonTypeInfoResolver defaultResolver)
            {
                defaultResolver.Modifiers.Add(polymorphismModifier);
            }
            else
            {
                options.SerializerOptions.TypeInfoResolver =
                    resolver.WithAddedModifier(polymorphismModifier);
            }
        });
        return AddTelemetry(services, SchemaBuilder.FromAssembly(assembly), collectorVersion);
    }

    /// <summary>
    /// Overload for tests: register with a specific descriptor set so unit tests can exercise
    /// the eager validation guarantees without crafting a scanned-assembly fixture.
    /// </summary>
    internal static IServiceCollection AddTelemetry(this IServiceCollection services, IEnumerable<SchemaDescriptor> descriptors, string collectorVersion = "test")
    {
        var catalog = new SchemaCatalog(descriptors);

        services.TryAddSingleton<PseudonymSaltFactory>();
        services.TryAddSingleton(catalog);
        services.TryAddSingleton(sp => new SchemaApplier(
            sp.GetRequiredService<SchemaCatalog>(),
            sp.GetRequiredService<PseudonymSaltFactory>()));
        services.TryAddSingleton<BatchBuffer>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<TelemetryIngestionSettings>>().Value;
            return new BatchBuffer(settings.BufferCapacity);
        });
        services.TryAddSingleton(sp => new TelemetryMetrics(sp.GetRequiredService<IMeterFactory>(), collectorVersion));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<TelemetryHandler>();

        services.TryAddSingleton<ITelemetrySink, BlobStorageSink>();
        services.AddHostedService<BatchFlusher>();
        return services;
    }
}
