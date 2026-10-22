using System.Text;
using System.Text.Json;
using Bit.Telemetee.Telemetry.Pseudonymization;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Schemas;

public class SchemaApplierTests
{
    private readonly PseudonymSaltFactory _saltFactory = new();
    private readonly SchemaApplier _applier;

    public SchemaApplierTests()
    {
        _applier = new SchemaApplier(new SchemaCatalog(ProductionDescriptors), _saltFactory);
    }

    [Fact]
    public void KeptFields_PassThroughVerbatim()
    {
        var result = _applier.ApplyUnauthed(Encode("fill_assist_rule_outcome", new
        {
            rule_id = "r1",
            success = true,
            domain = "a.example",
            client_version_major = 2025,
        }));

        var sink = Assert.IsType<FillAssistRuleOutcome.Sink>(result.Record);
        Assert.Equal("r1", sink.RuleId);
        Assert.True(sink.Success);
        Assert.Equal("a.example", sink.Domain);
    }

    [Fact]
    public void PseudonymizedField_IsStableWithinProcess_AndDifferentAcrossSaltFactories()
    {
        var descriptor = SchemaBuilder.FromType(typeof(PseudoFixture));
        var applier = new SchemaApplier(new SchemaCatalog([descriptor]), _saltFactory);

        var first = applier.ApplyUnauthed(Encode("pseudo_fixture", new { id = "same" })).Record!;
        var second = applier.ApplyUnauthed(Encode("pseudo_fixture", new { id = "same" })).Record!;
        Assert.Equal(((PseudoFixture.Sink)first).Id, ((PseudoFixture.Sink)second).Id);

        using var otherFactory = new PseudonymSaltFactory();
        var otherApplier = new SchemaApplier(new SchemaCatalog([descriptor]), otherFactory);
        var afterRestart = otherApplier.ApplyUnauthed(Encode("pseudo_fixture", new { id = "same" })).Record!;
        Assert.NotEqual(((PseudoFixture.Sink)first).Id, ((PseudoFixture.Sink)afterRestart).Id);
    }

    [Fact]
    public void BucketedField_IsReplacedByItsLabel()
    {
        var result = _applier.ApplyUnauthed(SampleFillAssistRecord(clientVersionMajor: 2023));
        var sink = Assert.IsType<FillAssistRuleOutcome.Sink>(result.Record);
        Assert.Equal("<2024", sink.ClientVersionMajor);
    }

    [Fact]
    public void UnknownSchema_IsDropped()
    {
        var result = _applier.ApplyUnauthed(Encode("ghost_schema", new { x = 1 }));
        Assert.False(result.IsOk);
        Assert.Equal(DropReason.UnknownSchema, result.Reason);
    }

    [Fact]
    public void UndeclaredField_IsSilentlyDropped_RecordStillKept()
    {
        var result = _applier.ApplyUnauthed(Encode("fill_assist_rule_outcome", new
        {
            rule_id = "r",
            success = true,
            domain = "x",
            client_version_major = 2025,
            future_field = "sneaky",
        }));
        Assert.True(result.IsOk);
        var sink = Assert.IsType<FillAssistRuleOutcome.Sink>(result.Record);
        Assert.Equal("r", sink.RuleId);
    }

    [Fact]
    public void MissingDeclaredField_IsMalformedRecord()
    {
        // The compiled process requires every declared input field to be present. A record that
        // omits one is malformed — the old behavior (quietly accept partial records) is gone, since
        // the typed sink has required properties that must all be set.
        var result = _applier.ApplyUnauthed(Encode("fill_assist_rule_outcome", new
        {
            rule_id = "r",
            // success, domain, client_version_major missing
        }));
        Assert.False(result.IsOk);
        Assert.Equal(DropReason.MalformedRecord, result.Reason);
    }

    [Fact]
    public void Catalog_RejectsSchemasWithDuplicateNames()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SchemaCatalog([
                SchemaBuilder.FromType(typeof(DupA)),
                SchemaBuilder.FromType(typeof(DupB)),
            ]));
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public void MalformedJson_IsDropped()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\": \"fill_assist_rule_outcome\",");
        var result = _applier.ApplyUnauthed(bytes);
        Assert.False(result.IsOk);
        Assert.Equal(DropReason.MalformedJson, result.Reason);
    }

    [Fact]
    public void MissingSchemaProperty_IsMalformed()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"rule_id\": \"r\"}");
        var result = _applier.ApplyUnauthed(bytes);
        Assert.False(result.IsOk);
        Assert.Equal(DropReason.MalformedRecord, result.Reason);
    }

    private static IEnumerable<SchemaDescriptor> ProductionDescriptors =>
        SchemaBuilder.FromAssembly(typeof(TelemetryServiceCollectionExtensions).Assembly);

    private static byte[] SampleFillAssistRecord(int clientVersionMajor = 2025) =>
        Encode("fill_assist_rule_outcome", new
        {
            rule_id = "r",
            success = true,
            domain = "a.example",
            client_version_major = clientVersionMajor,
        });

    private static byte[] Encode(string schema, object fields)
    {
        var merged = new Dictionary<string, object?> { ["schema"] = schema };
        foreach (var prop in fields.GetType().GetProperties())
        {
            merged[prop.Name] = prop.GetValue(fields);
        }
        return JsonSerializer.SerializeToUtf8Bytes(merged);
    }

    [Telemetry("pseudo_fixture")]
    private sealed class PseudoFixture : TelemetryRecord
    {
        [Pseudonymize]
        public string Id { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Id { get; init; }
        }
    }

    [Telemetry("dup")]
    private sealed class DupA : TelemetryRecord
    {
        [Keep]
        public string Field { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Field { get; init; }
        }
    }

    [Telemetry("dup")]
    private sealed class DupB : TelemetryRecord
    {
        [Keep]
        public string Other { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Other { get; init; }
        }
    }
}
