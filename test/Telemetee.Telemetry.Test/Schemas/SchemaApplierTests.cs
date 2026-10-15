using System.Text;
using System.Text.Json;
using Bit.Telemetee.Telemetry.Pseudonymization;
using Bit.Telemetee.Telemetry.Schemas;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Schemas;

public class SchemaApplierTests
{
    private readonly PseudonymSalt _salt = new();
    private readonly SchemaApplier _applier;

    public SchemaApplierTests()
    {
        _applier = new SchemaApplier(ProductionSchemas, _salt);
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

        Assert.Equal("r1", result.Record!.Fields["rule_id"]);
        Assert.Equal(true, result.Record.Fields["success"]);
        Assert.Equal("a.example", result.Record.Fields["domain"]);
    }

    [Fact]
    public void PseudonymizedField_IsStableWithinProcess_AndDifferentAcrossSalts()
    {
        var pseudoSchema = new TelemetrySchema("pseudo", new Dictionary<string, FieldTreatment>
        {
            ["id"] = new FieldTreatment.Pseudonymize(),
        });
        var applier = new SchemaApplier([pseudoSchema], _salt);

        var first = applier.ApplyUnauthed(Encode("pseudo", new { id = "same" })).Record!;
        var second = applier.ApplyUnauthed(Encode("pseudo", new { id = "same" })).Record!;
        Assert.Equal(first.Fields["id"], second.Fields["id"]);

        using var otherSalt = new PseudonymSalt();
        var otherApplier = new SchemaApplier([pseudoSchema], otherSalt);
        var afterRestart = otherApplier.ApplyUnauthed(Encode("pseudo", new { id = "same" })).Record!;
        Assert.NotEqual(first.Fields["id"], afterRestart.Fields["id"]);
    }


    private static IEnumerable<TelemetrySchema> ProductionSchemas =>
        SchemaBuilder.FromAssembly(typeof(TelemetryServiceCollectionExtensions).Assembly);

    [Fact]
    public void BucketedField_IsReplacedByItsLabel()
    {
        var result = _applier.ApplyUnauthed(SampleRecord(clientVersionMajor: 2023));
        Assert.Equal("<2024", result.Record!.Fields["client_version_major"]);
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
        Assert.DoesNotContain("future_field", result.Record!.Fields.Keys);
        Assert.Equal("r", result.Record.Fields["rule_id"]);
    }

    [Fact]
    public void Constructor_RejectsSchemasWithDuplicateNames()
    {
        var a = new TelemetrySchema("same", new Dictionary<string, FieldTreatment>());
        var b = new TelemetrySchema("same", new Dictionary<string, FieldTreatment>());

        var ex = Assert.Throws<InvalidOperationException>(() => new SchemaApplier([a, b], _salt));
        Assert.Contains("same", ex.Message);
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

    [Fact]
    public void RecordWithOnlyDiscriminator_IsEmptyButOk()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"schema\": \"fill_assist_rule_outcome\"}");
        var result = _applier.ApplyUnauthed(bytes);
        Assert.True(result.IsOk);
        Assert.Empty(result.Record!.Fields);
    }

    private static byte[] SampleRecord(int clientVersionMajor = 2025) =>
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
}
