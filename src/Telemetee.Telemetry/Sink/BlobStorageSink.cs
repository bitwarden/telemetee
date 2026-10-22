using System.Collections.Concurrent;
using System.Reflection;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Bit.Telemetee.Telemetry.Schemas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet.Serialization;

namespace Bit.Telemetee.Telemetry.Sink;

/// <summary>
/// Writes a flushed batch to an Azure Blob Storage container as Parquet, one blob per emitted
/// chunk under its own per-schema virtual directory. The sink is outside the privacy trust
/// boundary; anonymization runs in the schema layer before this.
///
/// Each flush's incoming records are grouped by their sink type and combined with any cached tail
/// from previous flushes. The combined per-type stream is split into <c>TargetChunkRecords</c>-
/// sized chunks; full chunks write immediately, the final partial per type goes back into the
/// per-type tail cache. A sweep on every flusher tick emits any cached tail whose oldest record
/// has aged past <c>TailMaxDelay</c>, so quiet schemas eventually emit a partial blob instead of
/// starving. Each record already carries its origin flush's <c>BatchTime</c> and per-flush
/// <c>Seq</c> on itself, so a blob that mixes rows from several flushes preserves per-row stamp
/// accuracy and arrival order without the sink having to intervene.
/// </summary>
internal sealed class BlobStorageSink : ITelemetrySink
{
    private const string ParquetContentType = "application/vnd.apache.parquet";

    private static readonly ConcurrentDictionary<Type, Func<IReadOnlyList<TelemetrySinkRecord>, Stream, CancellationToken, Task>> _serializers = new();

    private readonly BlobStorageSinkSettings _settings;
    private readonly SchemaCatalog _catalog;
    private readonly ILogger<BlobStorageSink> _logger;
    private readonly Dictionary<Type, List<TelemetrySinkRecord>> _tails = new();
    private BlobContainerClient? _container;

    public BlobStorageSink(
        IOptions<BlobStorageSinkSettings> settings,
        SchemaCatalog catalog,
        ILogger<BlobStorageSink> logger)
    {
        _settings = settings.Value;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<SinkOutcome> SendAsync(
        IReadOnlyList<TelemetrySinkRecord> records,
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

        // Fold every cached tail into the new records and chunk the combined per-type stream so
        // an active schema's tail gets first shot at filling a chunk this flush. Anything the
        // fold left behind whose oldest row has aged past the delay then goes out on its own so
        // a quiet schema does not starve. Both passes feed the same uploader; a chunk that fails
        // to serialize or upload is counted as lost but does not stop the remaining chunks.
        var fresh = PrepareChunks(records, _settings.TargetChunkRecords);
        var aged = CollectAgedTails(batchTime, _settings.TailMaxDelay);

        var freshOutcome = await EmitChunksAsync(container, fresh, cancellationToken);
        var agedOutcome = await EmitChunksAsync(container, aged, cancellationToken);
        return new SinkOutcome(
            Written: freshOutcome.Written + agedOutcome.Written,
            Lost: freshOutcome.Lost + agedOutcome.Lost);
    }

    /// <summary>
    /// For each sink type in <paramref name="records"/>, prepends the cached tail (if any) and
    /// splits the combined records into chunks of exactly <paramref name="targetChunkRecords"/>.
    /// The final partial per type goes back into the tail cache. Records keep whatever
    /// <see cref="TelemetrySinkRecord.BatchTime"/> and <see cref="TelemetrySinkRecord.Seq"/> the
    /// flusher assigned them on the flush they first arrived in; the sink never re-stamps.
    /// </summary>
    internal List<(Type SinkType, IReadOnlyList<TelemetrySinkRecord> Chunk)> PrepareChunks(
        IReadOnlyList<TelemetrySinkRecord> records,
        int targetChunkRecords)
    {
        var result = new List<(Type, IReadOnlyList<TelemetrySinkRecord>)>();
        foreach (var group in records.GroupBy(r => r.GetType()))
        {
            List<TelemetrySinkRecord> combined = _tails.TryGetValue(group.Key, out var tail) ? [.. tail] : [];
            combined.AddRange(group);

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
    internal List<(Type SinkType, IReadOnlyList<TelemetrySinkRecord> Chunk)> CollectAgedTails(
        DateTimeOffset now,
        TimeSpan maxDelay)
    {
        var threshold = now - maxDelay;
        var result = new List<(Type, IReadOnlyList<TelemetrySinkRecord>)>();
        List<Type>? expired = null;
        foreach (var (sinkType, tail) in _tails)
        {
            if (tail.Count > 0 && tail[0].BatchTime <= threshold)
            {
                result.Add((sinkType, tail));
                (expired ??= []).Add(sinkType);
            }
        }
        if (expired is not null)
        {
            foreach (var sinkType in expired)
            {
                _tails.Remove(sinkType);
            }
        }
        return result;
    }

    private async Task<SinkOutcome> EmitChunksAsync(
        BlobContainerClient container,
        List<(Type SinkType, IReadOnlyList<TelemetrySinkRecord> Chunk)> chunks,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var lost = 0;
        foreach (var (sinkType, chunk) in chunks)
        {
            if (await WriteChunkAsync(container, sinkType, chunk, cancellationToken))
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
        Type sinkType,
        IReadOnlyList<TelemetrySinkRecord> chunk,
        CancellationToken cancellationToken)
    {
        var descriptor = _catalog.ForSinkType(sinkType);
        var path = $"{descriptor.Name}/{Guid.NewGuid():N}.parquet";
        byte[] bytes;
        try
        {
            bytes = await SerializeParquetAsync(sinkType, chunk, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Parquet serialization failed for schema {Schema} ({Count} records); dropping chunk.",
                descriptor.Name,
                chunk.Count);
            return false;
        }
        return await UploadAsync(container, path, bytes, cancellationToken);
    }

    /// <summary>
    /// Hands the chunk to <c>ParquetSerializer.SerializeAsync&lt;T&gt;</c> for the concrete sink
    /// type. The Parquet schema is derived from the sink type's declared properties, so there is
    /// no runtime inference across the batch — every blob of a given schema has the same column
    /// list in the same order.
    /// </summary>
    private static async Task<byte[]> SerializeParquetAsync(
        Type sinkType,
        IReadOnlyList<TelemetrySinkRecord> chunk,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await GetSerializer(sinkType)(chunk, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// Resolves <c>ParquetSerializer.SerializeAsync&lt;T&gt;</c> once per sink type and caches the
    /// closed delegate so later chunks skip the <c>GetMethods</c> scan and <c>Invoke</c> boxing.
    /// </summary>
    private static Func<IReadOnlyList<TelemetrySinkRecord>, Stream, CancellationToken, Task> GetSerializer(Type sinkType) =>
        _serializers.GetOrAdd(sinkType, static t =>
            typeof(BlobStorageSink)
                .GetMethod(nameof(SerializeTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(t)
                .CreateDelegate<Func<IReadOnlyList<TelemetrySinkRecord>, Stream, CancellationToken, Task>>());

    private static Task SerializeTypedAsync<T>(
        IReadOnlyList<TelemetrySinkRecord> chunk,
        Stream destination,
        CancellationToken cancellationToken) where T : TelemetrySinkRecord =>
        ParquetSerializer.SerializeAsync(chunk.Cast<T>(), destination, null, null, cancellationToken);

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
}
