using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.Sink;
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
        var records = Many("schema_a", 7);

        var chunks = sink.PrepareChunks(records, FlushA, targetChunkRecords: 3);

        Assert.Collection(
            chunks,
            c => AssertChunk(c, "schema_a", expectedCount: 3, expectedSeqs: [0, 1, 2]),
            c => AssertChunk(c, "schema_a", expectedCount: 3, expectedSeqs: [3, 4, 5]));

        // The one-record partial was retained; the next PrepareChunks will prepend it.
        var next = sink.PrepareChunks(Many("schema_a", 2), FlushB, targetChunkRecords: 3);
        AssertChunk(Assert.Single(next), "schema_a", expectedCount: 3, expectedSeqs: [6, 0, 1]);
    }

    [Fact]
    public void PrepareChunks_RolledTailKeepsItsOriginFlushTime_WhileNewRecordsCarryTheCurrentFlushTime()
    {
        var sink = Sink();

        // Flush A leaves one record stashed (seq=0, origin=FlushA).
        sink.PrepareChunks(Many("schema_a", 1), FlushA, targetChunkRecords: 3);

        var chunks = sink.PrepareChunks(Many("schema_a", 2), FlushB, targetChunkRecords: 3);

        var chunk = Assert.Single(chunks).Chunk;
        Assert.Equal(3, chunk.Count);
        Assert.Equal(FlushA, chunk[0].OriginBatchTime);
        Assert.Equal(FlushB, chunk[1].OriginBatchTime);
        Assert.Equal(FlushB, chunk[2].OriginBatchTime);
    }

    [Fact]
    public void PrepareChunks_PreservesPerSchemaIsolation()
    {
        var sink = Sink();
        var mixed = Many("schema_a", 1).Concat(Many("schema_b", 2)).ToList();

        var first = sink.PrepareChunks(mixed, FlushA, targetChunkRecords: 2);

        // schema_a's one record is stashed; schema_b's two emit immediately.
        var chunk = Assert.Single(first);
        AssertChunk(chunk, "schema_b", expectedCount: 2, expectedSeqs: [0, 1]);

        // schema_a's stash is independent of schema_b's cache state.
        var second = sink.PrepareChunks(Many("schema_a", 1), FlushB, targetChunkRecords: 2);
        AssertChunk(Assert.Single(second), "schema_a", expectedCount: 2, expectedSeqs: [0, 0]);
    }

    [Fact]
    public void CollectAgedTails_OnlyReturnsTailsOlderThanThreshold()
    {
        var sink = Sink();

        // schema_a stashes a tail stamped at FlushA.
        sink.PrepareChunks(Many("schema_a", 1), FlushA, targetChunkRecords: 10);
        // schema_b stashes a tail stamped at FlushC (30s later than FlushA + 30s = FlushB).
        sink.PrepareChunks(Many("schema_b", 1), FlushC, targetChunkRecords: 10);

        // At FlushC, maxDelay=30s → threshold=FlushB. schema_a's tail (origin=FlushA) is older; schema_b's is not.
        var swept = sink.CollectAgedTails(FlushC, TimeSpan.FromSeconds(30));

        var aged = Assert.Single(swept);
        Assert.Equal("schema_a", aged.Schema);

        // schema_b's tail is still cached and can be swept once it ages past FlushC + 30s.
        var again = sink.CollectAgedTails(FlushC.AddSeconds(31), TimeSpan.FromSeconds(30));
        var finallyAged = Assert.Single(again);
        Assert.Equal("schema_b", finallyAged.Schema);
    }

    [Fact]
    public void CollectAgedTails_DoesNotRedeliverTailsItAlreadyReturned()
    {
        var sink = Sink();
        sink.PrepareChunks(Many("schema_a", 1), FlushA, targetChunkRecords: 10);

        var first = sink.CollectAgedTails(FlushC, TimeSpan.FromSeconds(30));
        var second = sink.CollectAgedTails(FlushC, TimeSpan.FromSeconds(30));

        Assert.Single(first);
        Assert.Empty(second);
    }

    private static BlobStorageSink Sink() =>
        new(Options.Create(new BlobStorageSinkSettings()), NullLogger<BlobStorageSink>.Instance);

    private static List<AnonymizedRecord> Many(string schema, int count) =>
        [.. Enumerable.Range(0, count).Select(i =>
            new AnonymizedRecord(schema, new Dictionary<string, object?> { ["seq"] = i }))];

    private static void AssertChunk(
        (string Schema, IReadOnlyList<BlobStorageSink.TimedRecord> Chunk) chunk,
        string expectedSchema,
        int expectedCount,
        int[] expectedSeqs)
    {
        Assert.Equal(expectedSchema, chunk.Schema);
        Assert.Equal(expectedCount, chunk.Chunk.Count);
        Assert.Equal(expectedSeqs, chunk.Chunk.Select(t => (int)t.Record.Fields["seq"]!));
    }
}
