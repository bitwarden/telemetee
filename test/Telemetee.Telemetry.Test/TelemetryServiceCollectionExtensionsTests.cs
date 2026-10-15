using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test;

/// <summary>
/// The guarantees here are startup-time: a bad POCO must stop host boot, not break the first
/// request. Each test uses the internal <c>AddTelemetry(services, schemas)</c> overload so the
/// surface of "run the schema set through AddTelemetry" is exactly what the production parameterless
/// overload does.
/// </summary>
public class TelemetryServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTelemetry_ProductionAssembly_Succeeds()
    {
        new ServiceCollection().AddTelemetry("test");
    }

    [Fact]
    public void AddTelemetry_EnumeratesSchemasEagerly_SoBadPocoThrowsFromAddTelemetry()
    {
        // If AddTelemetry deferred the scan into a DI factory, enumerating this lazy sequence would
        // not happen inside the Assert.Throws block and the test would fail. The guarantee is that
        // the enumeration happens inside AddTelemetry.
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddTelemetry(LazyBadSchemas()));

        static IEnumerable<TelemetrySchema> LazyBadSchemas()
        {
            yield return SchemaBuilder.FromType(typeof(MissingTreatmentPoco));
        }
    }

    [Fact]
    public void AddTelemetry_DuplicateSchemaNames_ThrowsFromAddTelemetry()
    {
        var a = new TelemetrySchema("dup", new Dictionary<string, FieldTreatment>());
        var b = new TelemetrySchema("dup", new Dictionary<string, FieldTreatment>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddTelemetry([a, b]));
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void AddTelemetry_PropertyNameCollision_ThrowsFromAddTelemetry()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddTelemetry(LazyBuild()));

        static IEnumerable<TelemetrySchema> LazyBuild()
        {
            yield return SchemaBuilder.FromType(typeof(SnakeCaseCollisionPoco));
        }
    }

    [Fact]
    public void AddTelemetry_BucketerMemberMissing_ThrowsFromAddTelemetry()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddTelemetry(LazyBuild()));

        static IEnumerable<TelemetrySchema> LazyBuild()
        {
            yield return SchemaBuilder.FromType(typeof(BadBucketPoco));
        }
    }

    [Telemetry("missing_treatment_poco")]
    private sealed class MissingTreatmentPoco : TelemetryRecord
    {
        public string Field { get; init; } = string.Empty;
    }

    [Telemetry("snake_case_collision_poco")]
    private sealed class SnakeCaseCollisionPoco : TelemetryRecord
    {
        [Keep]
        public string UserId { get; init; } = string.Empty;

        [Keep]
        public string userId { get; init; } = string.Empty;
    }

    [Telemetry("bad_bucket_poco")]
    private sealed class BadBucketPoco : TelemetryRecord
    {
        [Bucket("DoesNotExist")]
        public int Value { get; init; }
    }
}
