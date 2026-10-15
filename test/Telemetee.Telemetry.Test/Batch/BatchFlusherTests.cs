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
    public async Task Flush_StampsBatchTimeOnEveryRecord()
    {
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var env = Env(time: new FakeTimeProvider(now));
        env.Buffer.TryAdd(Record("a"));
        env.Buffer.TryAdd(Record("b"));

        await env.Flusher.FlushOnceAsync(CancellationToken.None);

        var batch = Assert.Single(env.Sink.Batches);
        Assert.Equal(now, batch.Time);
        Assert.Equal(2, batch.Records.Count);
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
        Assert.Equal(tags, batch.Records.Select(r => r.Fields["x"]));
    }

    [Fact]
    public async Task FailedFlush_DropsBatch_AndCountsSinkFailure()
    {
        var env = Env(sink: new FailingSink());
        env.Buffer.TryAdd(Record("x"));

        using var sinkFailures = new MetricCollector<long>(
            env.Factory, TelemetryMetrics.MeterName, "telemetee.batch.sink_failures");

        await env.Flusher.FlushOnceAsync(CancellationToken.None);

        Assert.Single(sinkFailures.GetMeasurementSnapshot());
        Assert.Empty(env.Buffer.Drain());
    }

    private static AnonymizedRecord Record(string tag) =>
        new("fill_assist_rule_outcome", new Dictionary<string, object?> { ["x"] = tag });

    private static TestEnv Env(ITelemetrySink? sink = null, TimeProvider? time = null)
    {
        var factory = new TestMeterFactory();
        var buffer = new BatchBuffer(32);
        var recording = sink as RecordingSink ?? new RecordingSink();
        var actualSink = sink ?? recording;
        var flusher = new BatchFlusher(
            buffer,
            actualSink,
            Options.Create(new TelemetrySettings()),
            new TelemetryMetrics(factory, "test"),
            time ?? TimeProvider.System,
            NullLogger<BatchFlusher>.Instance);
        return new TestEnv(flusher, buffer, recording, factory);
    }

    private sealed record TestEnv(BatchFlusher Flusher, BatchBuffer Buffer, RecordingSink Sink, TestMeterFactory Factory);

    private sealed class RecordingSink : ITelemetrySink
    {
        public List<(DateTimeOffset Time, IReadOnlyList<AnonymizedRecord> Records)> Batches { get; } = [];

        public Task<bool> SendAsync(IReadOnlyList<AnonymizedRecord> records, DateTimeOffset batchTime, CancellationToken cancellationToken)
        {
            Batches.Add((batchTime, [.. records]));
            return Task.FromResult(true);
        }
    }

    private sealed class FailingSink : ITelemetrySink
    {
        public Task<bool> SendAsync(IReadOnlyList<AnonymizedRecord> records, DateTimeOffset batchTime, CancellationToken cancellationToken) =>
            Task.FromResult(false);
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
