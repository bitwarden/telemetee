using Azure.Storage.Blobs;
using Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;
using Bit.Telemetee.Telemetry.Schemas;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Sink;

/// <summary>
/// Base harness for per-schema Parquet golden tests. One class per schema derives from this and
/// declares its records; shared plumbing (Azurite container, record stamping, blob fetch, golden
/// assertion) lives here so each schema class stays focused on the records and whatever extra
/// value-level assertions it wants to tighten the contract with.
/// </summary>
public abstract class ParquetGoldenTestsBase
{
    protected static readonly DateTime FixedBatchTime =
        new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    protected static DateTimeOffset FixedBatchTimeOffset => new(FixedBatchTime, TimeSpan.Zero);

    private readonly AzuriteContainerFixture _azurite;

    protected ParquetGoldenTestsBase(AzuriteContainerFixture azurite)
    {
        _azurite = azurite;
    }

    /// <summary>
    /// Writes <paramref name="records"/> through <c>BlobStorageSink</c> as a single flush that
    /// emits exactly one blob (chunk size pinned to <c>records.Length</c>) and returns the blob's
    /// bytes. Use for a derived test that needs raw bytes to assert on something beyond the
    /// golden-file contract.
    /// </summary>
    protected async Task<byte[]> WriteAndFetchParquetAsync(string schemaName, TelemetrySinkRecord[] records)
    {
        var (sink, container) = SinkBuilder.Build(_azurite, targetChunkRecords: records.Length);
        var outcome = await sink.SendAsync(records, FixedBatchTimeOffset, CancellationToken.None);
        Assert.Equal(records.Length, outcome.Written);
        Assert.Equal(0, outcome.Lost);
        return await DownloadSingleBlobAsync(container, prefix: $"{schemaName}/");
    }

    /// <summary>
    /// Baseline per-schema check: writes <paramref name="records"/> and compares the emitted
    /// Parquet schema + rows against the committed goldens under <c>Golden/</c>. A diff means the
    /// Data Services contract has drifted — see <see cref="GoldenAssertions"/> for the message.
    /// </summary>
    protected async Task AssertGoldenAsync(string schemaName, TelemetrySinkRecord[] records)
    {
        var bytes = await WriteAndFetchParquetAsync(schemaName, records);
        await GoldenAssertions.AssertMatchesGoldenAsync(bytes, schemaName);
    }

    protected static TelemetrySinkRecord Stamp(TelemetrySinkRecord record, long seq)
    {
        record.BatchTime = FixedBatchTime;
        record.Seq = seq;
        return record;
    }

    private static async Task<byte[]> DownloadSingleBlobAsync(BlobContainerClient container, string prefix)
    {
        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(prefix: prefix))
        {
            names.Add(item.Name);
        }
        var only = Assert.Single(names);
        var blob = container.GetBlobClient(only);
        using var buffer = new MemoryStream();
        await blob.DownloadToAsync(buffer);
        return buffer.ToArray();
    }
}
