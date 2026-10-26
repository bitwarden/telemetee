using Bit.Telemetee.Telemetry.Sink;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Telemetry.Batch;

/// <summary>
/// Drains the batch buffer on a fixed cadence, stamps each drained record with the flush time and
/// a per-flush monotonic sequence number, and posts them to the sink in arrival order. Order
/// preservation is deliberate: downstream research queries that group by any pseudonymized field
/// see that user's events in the sequence they happened. The sink reports
/// per-call written and lost counts; the flusher routes those into the per-flush outcome (sent,
/// partial, failed, empty) and the records-lost counter. Lost records are not retried.
/// </summary>
internal sealed class BatchFlusher : BackgroundService
{
    private readonly BatchBuffer _buffer;
    private readonly ITelemetrySink _sink;
    private readonly TelemetryIngestionSettings _settings;
    private readonly TelemetryMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<BatchFlusher> _logger;

    public BatchFlusher(
        BatchBuffer buffer,
        ITelemetrySink sink,
        IOptions<TelemetryIngestionSettings> settings,
        TelemetryMetrics metrics,
        TimeProvider time,
        ILogger<BatchFlusher> logger)
    {
        _buffer = buffer;
        _sink = sink;
        _settings = settings.Value;
        _metrics = metrics;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_settings.FlushInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Drains the buffer and calls the sink on every tick, including ticks where the buffer was
    /// empty. The empty call is intentional: sinks that carry per-flush state (for example the
    /// Blob Storage sink's tail cache) rely on the empty tick to flush aged state out even when
    /// nothing new arrived.
    /// </summary>
    internal async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        var records = _buffer.Drain();
        var batchTime = _time.GetUtcNow();
        for (var seq = 0L; seq < records.Count; seq++)
        {
            var record = records[(int)seq];
            record.BatchTime = batchTime.UtcDateTime;
            record.Seq = seq;
        }
        try
        {
            var outcome = await _sink.SendAsync(records, batchTime, cancellationToken);
            if (outcome.Written == 0 && outcome.Lost == 0)
            {
                _metrics.FlushEmpty();
            }
            else if (outcome.Lost == 0)
            {
                _metrics.FlushSucceeded(outcome.Written);
            }
            else if (outcome.Written == 0)
            {
                _metrics.FlushFailed(outcome.Lost);
            }
            else
            {
                _metrics.FlushPartiallyFailed(outcome.Written, outcome.Lost);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Batch flush threw; dropping {Count} records.", records.Count);
            _metrics.FlushFailed(records.Count);
        }
    }
}
