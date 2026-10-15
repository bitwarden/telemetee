using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Schemas;

public class SchemaBuilderTests
{
    [Fact]
    public void FromType_UsesAttributeNameAsSchemaName()
    {
        var schema = SchemaBuilder.FromType(typeof(FillAssistRuleOutcome));
        Assert.Equal("fill_assist_rule_outcome", schema.Name);
    }

    [Fact]
    public void FromType_MapsPascalCaseToSnakeCase()
    {
        var schema = SchemaBuilder.FromType(typeof(FillAssistRuleOutcome));
        Assert.Contains("rule_id", schema.Fields.Keys);
        Assert.Contains("client_version_major", schema.Fields.Keys);
    }

    [Fact]
    public void FromType_ReadsBucketerFromDeclaredMember()
    {
        var schema = SchemaBuilder.FromType(typeof(FillAssistRuleOutcome));
        var bucket = Assert.IsType<FieldTreatment.Bucket>(schema.Fields["client_version_major"]);
        Assert.Same(FillAssistRuleOutcome.ClientVersionMajorBuckets, bucket.Bucketer);
    }

    [Fact]
    public void FromType_RejectsPropertyMissingATreatment()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(Missing)));
    }

    [Fact]
    public void FromType_RejectsPropertiesCollidingOnSnakeCaseName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(Collision)));
        Assert.Contains("user_id", ex.Message);
    }

    [Fact]
    public void FromType_RejectsPocoThatDoesNotInheritTelemetryRecord()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(DetachedPoco)));
        Assert.Contains(nameof(TelemetryRecord), ex.Message);
    }

    [Fact]
    public void FromType_SchemaWithMultiplePseudonymizedFields_IsAccepted()
    {
        // Pseudonymized fields are not reserved for one "correlation key"; researchers slice on
        // whichever pseudonym makes sense at query time. The builder must not restrict this.
        var schema = SchemaBuilder.FromType(typeof(MultiPseudonymized));
        Assert.IsType<FieldTreatment.Pseudonymize>(schema.Fields["user_id"]);
        Assert.IsType<FieldTreatment.Pseudonymize>(schema.Fields["device_id"]);
    }

    [Telemetry("multi_pseudonymized")]
    private sealed class MultiPseudonymized : TelemetryRecord
    {
        [Pseudonymize]
        public string UserId { get; init; } = string.Empty;

        [Pseudonymize]
        public string DeviceId { get; init; } = string.Empty;
    }

    [Telemetry("missing_treatment")]
    private sealed class Missing : TelemetryRecord
    {
        public string Field { get; init; } = string.Empty;
    }

    [Telemetry("collision")]
    private sealed class Collision : TelemetryRecord
    {
        [Keep]
        public string UserId { get; init; } = string.Empty;

        [Keep]
        public string userId { get; init; } = string.Empty;
    }

    [Telemetry("detached")]
    private sealed class DetachedPoco
    {
        [Keep]
        public string Field { get; init; } = string.Empty;
    }
}
