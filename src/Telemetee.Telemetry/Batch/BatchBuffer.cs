using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.Batch;

/// <summary>
/// Thread-safe in-memory batch of accepted records. Fixed capacity; records that arrive while the
/// buffer is full are rejected and counted. Lost on restart, by design: a persistent buffer would
/// outlive the MAA measurement the client used to trust sharing.
/// </summary>
internal sealed class BatchBuffer
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private List<TelemetrySinkRecord> _records;

    public BatchBuffer(int capacity)
    {
        _capacity = capacity;
        _records = new List<TelemetrySinkRecord>(capacity);
    }

    /// <summary>Returns true if the record was accepted, false if the buffer was full.</summary>
    public bool TryAdd(TelemetrySinkRecord record)
    {
        lock (_gate)
        {
            if (_records.Count >= _capacity)
            {
                return false;
            }
            _records.Add(record);
            return true;
        }
    }

    /// <summary>
    /// Removes everything in the buffer in arrival order and resets it. The flusher relies on this
    /// order: within a batch, records keep the sequence in which they entered the buffer, so a
    /// downstream query that groups by any pseudonymized field will see that user's events in the
    /// order they happened. Batch-time coarsening is the only timing granularity that leaves the
    /// collector; precise per-record arrival time is never emitted.
    /// </summary>
    public List<TelemetrySinkRecord> Drain()
    {
        lock (_gate)
        {
            var drained = _records;
            _records = new List<TelemetrySinkRecord>(_capacity);
            return drained;
        }
    }
}
