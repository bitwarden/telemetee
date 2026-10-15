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
            Enumerable.Range(0, 128).Select(i => (object)i),
            drained.Select(r => r.Fields["seq"]));
        Assert.Empty(buffer.Drain());
    }

    private static AnonymizedRecord Record(int seq) =>
        new("fill_assist_rule_outcome", new Dictionary<string, object?> { ["seq"] = seq });
}
