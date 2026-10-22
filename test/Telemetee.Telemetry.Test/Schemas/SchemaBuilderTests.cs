using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Schemas;

public class SchemaBuilderTests
{
    [Fact]
    public void FromType_UsesAttributeNameAsSchemaName()
    {
        var descriptor = SchemaBuilder.FromType(typeof(FillAssistRuleOutcome));
        Assert.Equal("fill_assist_rule_outcome", descriptor.Name);
    }

    [Fact]
    public void FromType_DerivesIsAuthedFromAnyAuthedField()
    {
        Assert.False(SchemaBuilder.FromType(typeof(FillAssistRuleOutcome)).IsAuthed);
        Assert.True(SchemaBuilder.FromType(typeof(AuthedGeneratedPassword)).IsAuthed);
    }

    [Fact]
    public void FromType_ReadsRetentionFromSinkIRetained()
    {
        var descriptor = SchemaBuilder.FromType(typeof(FillAssistRuleOutcome));
        Assert.Equal(FillAssistRuleOutcome.Sink.RetentionTime, descriptor.RetentionTime);
    }

    [Fact]
    public void FromType_RejectsPropertyMissingATreatment()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(MissingTreatment)));
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
    public void FromType_RejectsSchemaWithoutNestedSink()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(NoSink)));
        Assert.Contains("Sink", ex.Message);
    }

    [Fact]
    public void FromType_RejectsSinkThatDoesNotImplementIRetained()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(NoRetention)));
    }

    [Fact]
    public void FromType_RejectsBucketerMemberMissing()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(BadBucket)));
    }

    [Fact]
    public void FromType_RejectsKeepTypeMismatchBetweenInputAndSink()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(KeepTypeMismatch)));
        Assert.Contains("[Keep]", ex.Message);
    }

    [Fact]
    public void FromType_RejectsBucketWhoseInputTypeDoesNotMatchBucketer()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaBuilder.FromType(typeof(BucketTypeMismatch)));
    }

    [Fact]
    public void FromType_SchemaWithMultiplePseudonymizedFields_IsAccepted()
    {
        // Pseudonymized fields are not reserved for one "correlation key"; researchers slice on
        // whichever pseudonym makes sense at query time. The builder must not restrict this.
        var descriptor = SchemaBuilder.FromType(typeof(MultiPseudonymized));
        Assert.Equal("multi_pseudonymized", descriptor.Name);
    }

    [Telemetry("multi_pseudonymized")]
    private sealed class MultiPseudonymized : TelemetryRecord
    {
        [Pseudonymize]
        public string UserId { get; init; } = string.Empty;

        [Pseudonymize]
        public string DeviceId { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);

            public required string UserId { get; init; }
            public required string DeviceId { get; init; }
        }
    }

    [Telemetry("missing_treatment_fixture")]
    private sealed class MissingTreatment : TelemetryRecord
    {
        public string Field { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Field { get; init; }
        }
    }

    [Telemetry("collision_fixture")]
    private sealed class Collision : TelemetryRecord
    {
        [Keep]
        public string UserId { get; init; } = string.Empty;

        [Keep]
        public string userId { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string UserId { get; init; }
        }
    }

    [Telemetry("detached_fixture")]
    private sealed class DetachedPoco
    {
        [Keep]
        public string Field { get; init; } = string.Empty;
    }

    [Telemetry("no_sink_fixture")]
    private sealed class NoSink : TelemetryRecord
    {
        [Keep]
        public string Field { get; init; } = string.Empty;
    }

    [Telemetry("no_retention_fixture")]
    private sealed class NoRetention : TelemetryRecord
    {
        [Keep]
        public string Field { get; init; } = string.Empty;

        public sealed class Sink : TelemetrySinkRecord
        {
            public required string Field { get; init; }
        }
    }

    [Telemetry("bad_bucket_fixture")]
    private sealed class BadBucket : TelemetryRecord
    {
        [Bucket("DoesNotExist")]
        public int Value { get; init; }

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Value { get; init; }
        }
    }

    [Telemetry("keep_type_mismatch_fixture")]
    private sealed class KeepTypeMismatch : TelemetryRecord
    {
        [Keep]
        public int Count { get; init; }

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Count { get; init; }
        }
    }

    [Telemetry("bucket_type_mismatch_fixture")]
    private sealed class BucketTypeMismatch : TelemetryRecord
    {
        [Bucket(nameof(Buckets))]
        public string Name { get; init; } = string.Empty;

        public static readonly IBucketer<int> Buckets = new NumericRangeBucketer<int>(
            new NumericRange("x", null, null));

        public sealed class Sink : TelemetrySinkRecord, IRetained
        {
            public static TimeSpan RetentionTime => TimeSpan.FromDays(30);
            public required string Name { get; init; }
        }
    }
}
