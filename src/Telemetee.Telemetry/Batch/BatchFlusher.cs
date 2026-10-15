using Bit.Telemetee.Telemetry.Sink;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Telemetry.Batch;

/// <summary>
/// Drains the batch buffer on a fixed cadence, stamps the drained records with the flush time, and
/// posts them to the sink in arrival order. Order preservation is deliberate: downstream research
/// queries that group by any pseudonymized field see that user's events in the sequence they
/// happened, without the collector having to pre-commit to a correlation key or emit per-record
/// timestamps. A failed post drops the batch; retry would be state that outlives the flush and is
/// deliberately out of this milestone.
/// </summary>
internal sealed class BatchFlusher : BackgroundService
{
    private readonly BatchBuffer _buffer;
    private readonly ITelemetrySink _sink;
    private readonly TelemetrySettings _settings;
    private readonly TelemetryMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<BatchFlusher> _logger;

    public BatchFlusher(
        BatchBuffer buffer,
        ITelemetrySink sink,
        IOptions<TelemetrySettings> settings,
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

    internal async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        var records = _buffer.Drain();
        if (records.Count == 0)
        {
            _metrics.FlushEmpty();
            return;
        }

        var batchTime = _time.GetUtcNow();
        try
        {
            var sent = await _sink.SendAsync(records, batchTime, cancellationToken);
            if (sent)
            {
                _metrics.FlushSucceeded(records.Count);
            }
            else
            {
                _metrics.FlushFailed(records.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Batch flush threw; dropping {Count} records.", records.Count);
            _metrics.FlushFailed(records.Count);
        }
    }
}
