using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Bit.Telemetee.Telemetry.Schemas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet.Schema;
using Parquet.Serialization;

namespace Bit.Telemetee.Telemetry.Sink;

/// <summary>
/// Writes a flushed batch to an Azure Blob Storage container as Parquet, one blob per emitted
/// chunk under its own per-schema virtual directory. The sink is outside the privacy trust
/// boundary; anonymization runs in the schema layer before this.
///
/// Each flush's incoming records are grouped by schema and combined with any cached tail from
/// previous flushes. The combined per-schema stream is split into <c>TargetChunkRecords</c>-sized
/// chunks; full chunks write immediately, the final partial per schema goes back into the
/// per-schema tail cache. A sweep on every flusher tick emits any cached tail whose oldest record
/// has aged past <c>TailMaxDelay</c>, so quiet schemas eventually emit a partial blob instead of
/// starving. Each Parquet row carries its original flush's <c>batch_time</c>, so a blob that
/// mixes rows from several flushes does not loosen the per-row stamp accuracy.
/// </summary>
internal sealed class BlobStorageSink : ITelemetrySink
{
    private const string ParquetContentType = "application/vnd.apache.parquet";
    private const string BatchTimeColumn = "batch_time";

    private readonly BlobStorageSinkSettings _settings;
    private readonly ILogger<BlobStorageSink> _logger;
    private readonly Dictionary<string, List<TimedRecord>> _tails = new(StringComparer.Ordinal);
    private BlobContainerClient? _container;

    public BlobStorageSink(IOptions<BlobStorageSinkSettings> settings, ILogger<BlobStorageSink> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<SinkOutcome> SendAsync(
        IReadOnlyList<AnonymizedRecord> records,
        DateTimeOffset batchTime,
        CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync(cancellationToken);
        if (container is null)
        {
            if (records.Count > 0)
            {
                _logger.LogWarning("Blob Storage connection string is not set; dropping batch of {Count} records.", records.Count);
            }
            return new SinkOutcome(Written: 0, Lost: records.Count);
        }

        // Fold every cached tail into the new records and chunk the combined per-schema stream
        // so an active schema's tail gets first shot at filling a chunk this flush. Anything the
        // fold left behind whose oldest row has aged past the delay then goes out on its own so
        // a quiet schema does not starve. Both passes feed the same uploader; a chunk that fails
        // to serialize or upload is counted as lost but does not stop the remaining chunks.
        var fresh = PrepareChunks(records, batchTime, _settings.TargetChunkRecords);
        var aged = CollectAgedTails(batchTime, _settings.TailMaxDelay);

        var freshOutcome = await EmitChunksAsync(container, fresh, cancellationToken);
        var agedOutcome = await EmitChunksAsync(container, aged, cancellationToken);
        return new SinkOutcome(
            Written: freshOutcome.Written + agedOutcome.Written,
            Lost: freshOutcome.Lost + agedOutcome.Lost);
    }

    /// <summary>
    /// For each schema in <paramref name="records"/>, prepends the cached tail (if any) and
    /// splits the combined records into chunks of exactly <paramref name="targetChunkRecords"/>.
    /// New records are stamped with <paramref name="batchTime"/>; cached tail entries keep their
    /// original flush time. The final partial per schema goes back into the tail cache.
    /// </summary>
    internal List<(string Schema, IReadOnlyList<TimedRecord> Chunk)> PrepareChunks(
        IReadOnlyList<AnonymizedRecord> records,
        DateTimeOffset batchTime,
        int targetChunkRecords)
    {
        var result = new List<(string, IReadOnlyList<TimedRecord>)>();
        foreach (var group in records.GroupBy(r => r.Schema))
        {
            List<TimedRecord> combined = _tails.TryGetValue(group.Key, out var tail) ? [.. tail] : [];
            foreach (var record in group)
            {
                combined.Add(new TimedRecord(record, batchTime));
            }

            var offset = 0;
            while (offset + targetChunkRecords <= combined.Count)
            {
                result.Add((group.Key, combined.GetRange(offset, targetChunkRecords)));
                offset += targetChunkRecords;
            }

            var remaining = combined.Count - offset;
            if (remaining > 0)
            {
                _tails[group.Key] = combined.GetRange(offset, remaining);
            }
            else
            {
                _tails.Remove(group.Key);
            }
        }
        return result;
    }

    /// <summary>
    /// Returns every cached tail whose oldest record was stamped at or before
    /// <paramref name="now"/> - <paramref name="maxDelay"/>, removing them from the cache.
    /// </summary>
    internal List<(string Schema, IReadOnlyList<TimedRecord> Chunk)> CollectAgedTails(
        DateTimeOffset now,
        TimeSpan maxDelay)
    {
        var threshold = now - maxDelay;
        var result = new List<(string, IReadOnlyList<TimedRecord>)>();
        List<string>? expired = null;
        foreach (var (schema, tail) in _tails)
        {
            if (tail.Count > 0 && tail[0].OriginBatchTime <= threshold)
            {
                result.Add((schema, tail));
                (expired ??= []).Add(schema);
            }
        }
        if (expired is not null)
        {
            foreach (var schema in expired)
            {
                _tails.Remove(schema);
            }
        }
        return result;
    }

    private async Task<SinkOutcome> EmitChunksAsync(
        BlobContainerClient container,
        List<(string Schema, IReadOnlyList<TimedRecord> Chunk)> chunks,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var lost = 0;
        foreach (var (schema, chunk) in chunks)
        {
            if (await WriteChunkAsync(container, schema, chunk, cancellationToken))
            {
                written += chunk.Count;
            }
            else
            {
                lost += chunk.Count;
            }
        }
        return new SinkOutcome(written, lost);
    }

    private async Task<bool> WriteChunkAsync(
        BlobContainerClient container,
        string schema,
        IReadOnlyList<TimedRecord> chunk,
        CancellationToken cancellationToken)
    {
        var path = $"{schema}/{Guid.NewGuid():N}.parquet";
        byte[] bytes;
        try
        {
            bytes = await SerializeParquetAsync(chunk, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Parquet serialization failed for schema {Schema} ({Count} records); dropping chunk.",
                schema,
                chunk.Count);
            return false;
        }
        return await UploadAsync(container, path, bytes, cancellationToken);
    }

    private static async Task<byte[]> SerializeParquetAsync(
        IReadOnlyList<TimedRecord> chunk,
        CancellationToken cancellationToken)
    {
        var (names, types) = InferColumns(chunk);
        var fields = new DataField[names.Count + 1];
        fields[0] = new DataField<long?>(BatchTimeColumn);
        for (var i = 0; i < names.Count; i++)
        {
            fields[i + 1] = CreateDataField(names[i], types[i]);
        }
        var schema = new ParquetSchema(fields);

        var rows = new List<IDictionary<string, object?>>(chunk.Count);
        foreach (var timed in chunk)
        {
            var row = new Dictionary<string, object?>(names.Count + 1)
            {
                [BatchTimeColumn] = timed.OriginBatchTime.ToUnixTimeMilliseconds(),
            };
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                row[name] = timed.Record.Fields.TryGetValue(name, out var raw)
                    ? Coerce(raw, types[i])
                    : null;
            }
            rows.Add(row);
        }

        using var buffer = new MemoryStream();
        await ParquetSerializer.SerializeUntypedAsync(rows, schema, buffer, cancellationToken: cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// Walks every record to collect the column list (union of field names, insertion-ordered by
    /// first appearance) and infer a per-column CLR type. Mixed types in one column collapse to
    /// <see cref="string"/>; an all-null column also defaults to string.
    /// </summary>
    private static (List<string> Names, List<Type> Types) InferColumns(IReadOnlyList<TimedRecord> chunk)
    {
        var names = new List<string>();
        var types = new Dictionary<string, Type>();
        foreach (var timed in chunk)
        {
            foreach (var (name, value) in timed.Record.Fields)
            {
                if (!types.ContainsKey(name))
                {
                    names.Add(name);
                    types[name] = typeof(void);
                }
                if (value is null)
                {
                    continue;
                }
                var observed = NormalizeType(value.GetType());
                var current = types[name];
                if (current == typeof(void))
                {
                    types[name] = observed;
                }
                else if (current != observed)
                {
                    types[name] = typeof(string);
                }
            }
        }
        var typeList = new List<Type>(names.Count);
        foreach (var name in names)
        {
            var t = types[name];
            typeList.Add(t == typeof(void) ? typeof(string) : t);
        }
        return (names, typeList);
    }

    private static Type NormalizeType(Type t)
    {
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
        {
            return typeof(long);
        }
        if (t == typeof(float) || t == typeof(double))
        {
            return typeof(double);
        }
        if (t == typeof(bool))
        {
            return typeof(bool);
        }
        return typeof(string);
    }

    private static DataField CreateDataField(string name, Type type)
    {
        if (type == typeof(bool))
        {
            return new DataField<bool?>(name);
        }
        if (type == typeof(long))
        {
            return new DataField<long?>(name);
        }
        if (type == typeof(double))
        {
            return new DataField<double?>(name);
        }
        return new DataField<string>(name);
    }

    private static object? Coerce(object? value, Type targetType)
    {
        if (value is null)
        {
            return null;
        }
        if (targetType == typeof(bool))
        {
            return Convert.ToBoolean(value);
        }
        if (targetType == typeof(long))
        {
            return Convert.ToInt64(value);
        }
        if (targetType == typeof(double))
        {
            return Convert.ToDouble(value);
        }
        return value as string ?? value.ToString();
    }

    private async Task<bool> UploadAsync(
        BlobContainerClient container,
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var blob = container.GetBlobClient(path);
            using var content = new MemoryStream(bytes, writable: false);
            await blob.UploadAsync(
                content,
                new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = ParquetContentType } },
                cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Blob Storage upload of {Path} failed.", path);
            return false;
        }
    }

    private async Task<BlobContainerClient?> GetContainerAsync(CancellationToken cancellationToken)
    {
        if (_container is not null)
        {
            return _container;
        }
        if (string.IsNullOrEmpty(_settings.ConnectionString))
        {
            return null;
        }
        var container = new BlobContainerClient(_settings.ConnectionString, _settings.Container);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        _container = container;
        return _container;
    }

    /// <summary>One accepted record paired with the <c>batch_time</c> of the flush it arrived on.</summary>
    internal readonly record struct TimedRecord(AnonymizedRecord Record, DateTimeOffset OriginBatchTime);
}
