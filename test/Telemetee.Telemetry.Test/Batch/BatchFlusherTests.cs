using System.Diagnostics.Metrics;
using Bit.Telemetee.Telemetry.Batch;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.Sink;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test.Batch;

public class BatchFlusherTests
{
    [Fact]
    public async Task Flush_EmptyBuffer_SendsNothing()
    {
        var env = Env();
        await env.Flusher.FlushOnceAsync(CancellationToken.None);
        Assert.Empty(env.Sink.Batches);
    }

    [Fact]
    public async Task Flush_StampsBatchTimeAndMonotonicSeqOnEveryRecord()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var env = Env(time: new FakeTimeProvider(now));
        env.Buffer.TryAdd(Record("a"));
        env.Buffer.TryAdd(Record("b"));

        await env.Flusher.FlushOnceAsync(CancellationToken.None);

        var batch = Assert.Single(env.Sink.Batches);
        Assert.Equal(now, batch.Time);
        Assert.Equal(2, batch.Records.Count);
        Assert.All(batch.Records, r => Assert.Equal(now, r.BatchTime));
        Assert.Equal([0L, 1L], batch.Records.Select(r => r.Seq));
    }

    [Fact]
    public async Task Flush_PreservesArrivalOrder_SoResearchQueriesCanSliceByAnyPseudonym()
    {
        var env = Env();
        var tags = new[] { "u1-a", "u2-x", "u1-b", "u3-a", "u1-c", "u2-y" };
        foreach (var tag in tags)
        {
            env.Buffer.TryAdd(Record(tag));
        }

        await env.Flusher.FlushOnceAsync(CancellationToken.None);

        var batch = Assert.Single(env.Sink.Batches);
        Assert.Equal(tags, batch.Records.Cast<TaggedSink>().Select(r => r.Tag));
    }

    [Fact]
    public async Task FailedFlush_DropsBatch_AndCountsEveryLostRecord()
    {
        var env = Env(sink: new FailingSink());
        env.Buffer.TryAdd(Record("x"));
        env.Buffer.TryAdd(Record("y"));
        env.Buffer.TryAdd(Record("z"));

        using var sinkFailures = new MetricCollector<long>(
            env.Factory, TelemetryMetrics.MeterName, "telemetee.batch.sink_failures");

        await env.Flusher.FlushOnceAsync(CancellationToken.None);

        var measurement = Assert.Single(sinkFailures.GetMeasurementSnapshot());
        Assert.Equal(3, measurement.Value);
        Assert.Empty(env.Buffer.Drain());
    }

    [Fact]
    public async Task PartialFlush_CountsOnlyTheLostRecords_AndReportsPartialOutcome()
    {
        // Three records in; sink reports two written and one lost. The lost count is what feeds
        // the sink_failures counter — the two that landed must not be counted as failures.
        var sink = new PartialSink(written: 2, lost: 1);
        var env = Env(sink: sink);
        env.Buffer.TryAdd(Record("x"));
        env.Buffer.TryAdd(Record("y"));
        env.Buffer.TryAdd(Record("z"));

        using var sinkFailures = new MetricCollector<long>(
            env.Factory, TelemetryMetrics.MeterName, "telemetee.batch.sink_failures");
        using var flushes = new MetricCollector<long>(
            env.Factory, TelemetryMetrics.MeterName, "telemetee.batch.flushes");

        await env.Flusher.FlushOnceAsync(CancellationToken.None);

        var failure = Assert.Single(sinkFailures.GetMeasurementSnapshot());
        Assert.Equal(1, failure.Value);
        var flush = Assert.Single(flushes.GetMeasurementSnapshot());
        Assert.Equal("partial", flush.Tags["outcome"]);
    }

    private static TelemetrySinkRecord Record(string tag) => new TaggedSink { Tag = tag };

    private sealed class TaggedSink : TelemetrySinkRecord
    {
        public required string Tag { get; init; }
    }

    private static TestEnv Env(ITelemetrySink? sink = null, TimeProvider? time = null)
    {
        var factory = new TestMeterFactory();
        var buffer = new BatchBuffer(32);
        var recording = sink as RecordingSink ?? new RecordingSink();
        var actualSink = sink ?? recording;
        var flusher = new BatchFlusher(
            buffer,
            actualSink,
            Options.Create(new TelemetryIngestionSettings()),
            new TelemetryMetrics(factory, "test"),
            time ?? TimeProvider.System,
            NullLogger<BatchFlusher>.Instance);
        return new TestEnv(flusher, buffer, recording, factory);
    }

    private sealed record TestEnv(BatchFlusher Flusher, BatchBuffer Buffer, RecordingSink Sink, TestMeterFactory Factory);

    private sealed class RecordingSink : ITelemetrySink
    {
        public List<(DateTimeOffset Time, IReadOnlyList<TelemetrySinkRecord> Records)> Batches { get; } = [];

        public Task<SinkOutcome> SendAsync(IReadOnlyList<TelemetrySinkRecord> records, DateTimeOffset batchTime, CancellationToken cancellationToken)
        {
            // The flusher calls SendAsync on every tick. Test assertions here only care about calls
            // that actually carried records.
            if (records.Count > 0)
            {
                Batches.Add((batchTime, [.. records]));
            }
            return Task.FromResult(new SinkOutcome(Written: records.Count, Lost: 0));
        }
    }

    private sealed class FailingSink : ITelemetrySink
    {
        public Task<SinkOutcome> SendAsync(IReadOnlyList<TelemetrySinkRecord> records, DateTimeOffset batchTime, CancellationToken cancellationToken) =>
            Task.FromResult(new SinkOutcome(Written: 0, Lost: records.Count));
    }

    private sealed class PartialSink(int written, int lost) : ITelemetrySink
    {
        public Task<SinkOutcome> SendAsync(IReadOnlyList<TelemetrySinkRecord> records, DateTimeOffset batchTime, CancellationToken cancellationToken) =>
            Task.FromResult(new SinkOutcome(Written: written, Lost: lost));
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters)
            {
                meter.Dispose();
            }
            _meters.Clear();
        }
    }
}
