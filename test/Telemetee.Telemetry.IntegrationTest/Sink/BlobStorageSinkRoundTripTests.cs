using System.Runtime.CompilerServices;
using Azure.Storage.Blobs.Models;
using Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Sink;

/// <summary>
/// Round-trip tests that exercise the sink's real Azure Blob SDK path against Azurite:
/// container create-on-first-use, blob path layout, content-type header, and the reflection-based
/// generic serializer dispatch for every registered sink type. The in-process unit tests under
/// <c>Telemetee.Telemetry.Test</c> cover the chunking/tail-sweep orchestration without touching
/// the serializer; this suite covers the parts only a real SDK + server can observe.
/// </summary>
[Collection(AzuriteCollection.Name)]
public class BlobStorageSinkRoundTripTests
{
    private static readonly DateTime FixedBatchTime = FixedBatchTimeOffset.UtcDateTime;

    private static DateTimeOffset FixedBatchTimeOffset => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly AzuriteContainerFixture _azurite;

    public BlobStorageSinkRoundTripTests(AzuriteContainerFixture azurite)
    {
        _azurite = azurite;
    }

    [Fact]
    public async Task FirstFlush_CreatesContainerIfMissing()
    {
        var (sink, container) = SinkBuilder.Build(_azurite, targetChunkRecords: 1);

        Assert.False(await container.ExistsAsync());

        var record = Stamp(new FillAssistRuleOutcome.Sink
        {
            RuleId = "r",
            Success = true,
            Domain = "a.example",
            ClientVersionMajor = "2025",
        });

        var outcome = await sink.SendAsync([record], FixedBatchTimeOffset, CancellationToken.None);

        Assert.Equal(1, outcome.Written);
        Assert.True(await container.ExistsAsync());
    }

    [Fact]
    public async Task WrittenBlob_LandsAtSchemaDirectoryWithParquetContentType()
    {
        var (sink, container) = SinkBuilder.Build(_azurite, targetChunkRecords: 1);

        var record = Stamp(new FillAssistRuleOutcome.Sink
        {
            RuleId = "r",
            Success = true,
            Domain = "a.example",
            ClientVersionMajor = "2025",
        });

        await sink.SendAsync([record], FixedBatchTimeOffset, CancellationToken.None);

        var items = new List<BlobItem>();
        await foreach (var item in container.GetBlobsAsync())
        {
            items.Add(item);
        }
        var only = Assert.Single(items);
        Assert.StartsWith("fill_assist_rule_outcome/", only.Name);
        Assert.EndsWith(".parquet", only.Name);

        var props = await container.GetBlobClient(only.Name).GetPropertiesAsync();
        Assert.Equal("application/vnd.apache.parquet", props.Value.ContentType);
    }

    /// <summary>
    /// The sink dispatches to <c>ParquetSerializer.SerializeAsync&lt;T&gt;</c> through a cached
    /// generic delegate built via reflection. The in-process tests never execute that path; this
    /// one sends a single uninitialized record of every registered sink type so a newly added
    /// type whose property shape Parquet.Net cannot serialize fails here, not in production.
    /// </summary>
    [Fact]
    public async Task GenericSerializerDispatch_WorksForEveryRegisteredSinkType()
    {
        var catalog = new SchemaCatalog(SchemaBuilder.FromAssembly(
            typeof(TelemetryServiceCollectionExtensions).Assembly));

        foreach (var descriptor in catalog.All)
        {
            var (sink, container) = SinkBuilder.Build(_azurite, targetChunkRecords: 1);

            var record = (TelemetrySinkRecord)RuntimeHelpers.GetUninitializedObject(descriptor.SinkType);
            record.BatchTime = FixedBatchTime;
            record.Seq = 0;

            var outcome = await sink.SendAsync([record], FixedBatchTimeOffset, CancellationToken.None);
            Assert.True(
                outcome.Written == 1,
                $"Expected sink type {descriptor.SinkType.Name} ({descriptor.Name}) to serialize and upload, got Written={outcome.Written}, Lost={outcome.Lost}.");
        }
    }

    private static TelemetrySinkRecord Stamp(TelemetrySinkRecord record)
    {
        record.BatchTime = FixedBatchTime;
        record.Seq = 0;
        return record;
    }
}
