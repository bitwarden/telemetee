using Bit.Telemetee.Telemetry.Batch;
using Bit.Telemetee.Telemetry.Schemas;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Batch;

public class BatchBufferTests
{
    [Fact]
    public void TryAdd_ReturnsFalseOnceCapacityReached()
    {
        var buffer = new BatchBuffer(2);
        Assert.True(buffer.TryAdd(Record(1)));
        Assert.True(buffer.TryAdd(Record(2)));
        Assert.False(buffer.TryAdd(Record(3)));
    }

    [Fact]
    public void Drain_PreservesInsertionOrder_AndEmptiesTheBuffer()
    {
        var buffer = new BatchBuffer(1024);
        for (var i = 0; i < 128; i++)
        {
            buffer.TryAdd(Record(i));
        }

        var drained = buffer.Drain();
        Assert.Equal(128, drained.Count);
        Assert.Equal(
            Enumerable.Range(0, 128),
            drained.Cast<TaggedSink>().Select(r => r.Tag));
        Assert.Empty(buffer.Drain());
    }

    private static TelemetrySinkRecord Record(int tag) => new TaggedSink { Tag = tag };

    private sealed class TaggedSink : TelemetrySinkRecord
    {
        public required int Tag { get; init; }
    }
}
