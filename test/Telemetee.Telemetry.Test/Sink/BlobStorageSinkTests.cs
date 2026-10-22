using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.Sink;
using Bit.Telemetee.Telemetry.TelemetryData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Sink;

public class BlobStorageSinkTests
{
    private static readonly DateTimeOffset FlushA = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FlushB = FlushA.AddSeconds(30);
    private static readonly DateTimeOffset FlushC = FlushA.AddSeconds(60);

    [Fact]
    public void PrepareChunks_WithNoCachedTail_EmitsFullChunks_AndStashesPartial()
    {
        var sink = Sink();
        var records = Many<FillAssistRuleOutcome.Sink>(7, FlushA);

        var chunks = sink.PrepareChunks(records, targetChunkRecords: 3);

        Assert.Collection(
            chunks,
            c => AssertChunk(c, typeof(FillAssistRuleOutcome.Sink), expectedCount: 3, expectedSeqs: [0, 1, 2]),
            c => AssertChunk(c, typeof(FillAssistRuleOutcome.Sink), expectedCount: 3, expectedSeqs: [3, 4, 5]));

        // The one-record partial was retained; the next PrepareChunks will prepend it.
        var next = sink.PrepareChunks(
            Many<FillAssistRuleOutcome.Sink>(2, FlushB, seqStart: 0),
            targetChunkRecords: 3);
        AssertChunk(Assert.Single(next), typeof(FillAssistRuleOutcome.Sink), expectedCount: 3, expectedSeqs: [6, 0, 1]);
    }

    [Fact]
    public void PrepareChunks_RolledTailKeepsItsOriginBatchTime_WhileNewRecordsCarryTheCurrentFlushTime()
    {
        var sink = Sink();

        // Flush A leaves one record stashed (seq=0, origin=FlushA).
        sink.PrepareChunks(Many<FillAssistRuleOutcome.Sink>(1, FlushA), targetChunkRecords: 3);

        var chunks = sink.PrepareChunks(Many<FillAssistRuleOutcome.Sink>(2, FlushB), targetChunkRecords: 3);

        var chunk = Assert.Single(chunks).Chunk;
        Assert.Equal(3, chunk.Count);
        Assert.Equal(FlushA, chunk[0].BatchTime);
        Assert.Equal(FlushB, chunk[1].BatchTime);
        Assert.Equal(FlushB, chunk[2].BatchTime);
    }

    [Fact]
    public void PrepareChunks_PreservesPerTypeIsolation()
    {
        var sink = Sink();
        var mixed = Many<FillAssistRuleOutcome.Sink>(1, FlushA)
            .Concat(Many<GeneratedPassword.Sink>(2, FlushA))
            .ToList();

        var first = sink.PrepareChunks(mixed, targetChunkRecords: 2);

        // FillAssist's one record is stashed; GeneratedPassword's two emit immediately.
        var chunk = Assert.Single(first);
        AssertChunk(chunk, typeof(GeneratedPassword.Sink), expectedCount: 2, expectedSeqs: [0, 1]);

        // FillAssist's stash is independent of GeneratedPassword's cache state.
        var second = sink.PrepareChunks(Many<FillAssistRuleOutcome.Sink>(1, FlushB), targetChunkRecords: 2);
        AssertChunk(Assert.Single(second), typeof(FillAssistRuleOutcome.Sink), expectedCount: 2, expectedSeqs: [0, 0]);
    }

    [Fact]
    public void CollectAgedTails_OnlyReturnsTailsOlderThanThreshold()
    {
        var sink = Sink();

        // FillAssist stashes a tail stamped at FlushA.
        sink.PrepareChunks(Many<FillAssistRuleOutcome.Sink>(1, FlushA), targetChunkRecords: 10);
        // GeneratedPassword stashes a tail stamped at FlushC.
        sink.PrepareChunks(Many<GeneratedPassword.Sink>(1, FlushC), targetChunkRecords: 10);

        // At FlushC, maxDelay=30s → threshold=FlushB. FillAssist's tail (origin=FlushA) is older;
        // GeneratedPassword's is not.
        var swept = sink.CollectAgedTails(FlushC, TimeSpan.FromSeconds(30));

        var aged = Assert.Single(swept);
        Assert.Equal(typeof(FillAssistRuleOutcome.Sink), aged.SinkType);

        // GeneratedPassword's tail is still cached and can be swept once it ages past FlushC + 30s.
        var again = sink.CollectAgedTails(FlushC.AddSeconds(31), TimeSpan.FromSeconds(30));
        var finallyAged = Assert.Single(again);
        Assert.Equal(typeof(GeneratedPassword.Sink), finallyAged.SinkType);
    }

    [Fact]
    public void CollectAgedTails_DoesNotRedeliverTailsItAlreadyReturned()
    {
        var sink = Sink();
        sink.PrepareChunks(Many<FillAssistRuleOutcome.Sink>(1, FlushA), targetChunkRecords: 10);

        var first = sink.CollectAgedTails(FlushC, TimeSpan.FromSeconds(30));
        var second = sink.CollectAgedTails(FlushC, TimeSpan.FromSeconds(30));

        Assert.Single(first);
        Assert.Empty(second);
    }

    private static BlobStorageSink Sink()
    {
        var catalog = new SchemaCatalog(SchemaBuilder.FromAssembly(
            typeof(TelemetryServiceCollectionExtensions).Assembly));
        return new BlobStorageSink(
            Options.Create(new BlobStorageSinkSettings()),
            catalog,
            NullLogger<BlobStorageSink>.Instance);
    }

    private static List<TelemetrySinkRecord> Many<TSink>(int count, DateTimeOffset batchTime, int seqStart = 0)
        where TSink : TelemetrySinkRecord =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            var record = CreateDefault<TSink>();
            record.BatchTime = batchTime;
            record.Seq = seqStart + i;
            return record;
        })];

    /// <summary>
    /// Instantiates a Sink via reflection, bypassing the <c>required</c>-member compile check.
    /// Properties are left at CLR defaults; the chunking/tail-sweep tests exercise paths that only
    /// read <c>BatchTime</c>, <c>Seq</c>, and <c>GetType()</c>, so field values are immaterial.
    /// </summary>
    private static T CreateDefault<T>() where T : TelemetrySinkRecord =>
        (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void AssertChunk(
        (Type SinkType, IReadOnlyList<TelemetrySinkRecord> Chunk) chunk,
        Type expectedSinkType,
        int expectedCount,
        long?[] expectedSeqs)
    {
        Assert.Equal(expectedSinkType, chunk.SinkType);
        Assert.Equal(expectedCount, chunk.Chunk.Count);
        Assert.Equal(expectedSeqs, chunk.Chunk.Select(r => r.Seq));
    }
}
