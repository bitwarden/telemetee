using Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Sink;

/// <summary>
/// Verifies that the chunking logic (covered in-process by <c>BlobStorageSinkTests</c>) actually
/// produces N distinct blobs under <c>{schema}/</c> when a single flush spans multiple chunks.
/// This covers the integration between chunking and upload — the in-process tests stop at the
/// chunk boundary and never contact a container.
/// </summary>
[Collection(AzuriteCollection.Name)]
public class BlobStorageSinkMultiBlobTests
{
    private static readonly DateTime FixedBatchTime =
        new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly AzuriteContainerFixture _azurite;

    public BlobStorageSinkMultiBlobTests(AzuriteContainerFixture azurite)
    {
        _azurite = azurite;
    }

    [Fact]
    public async Task OneFlushAboveTargetChunkRecords_EmitsMultipleBlobsUnderSchemaPrefix()
    {
        var (sink, container) = SinkBuilder.Build(_azurite, targetChunkRecords: 2);

        var records = Enumerable.Range(0, 5)
            .Select(i => (TelemetrySinkRecord)new FillAssistRuleOutcome.Sink
            {
                RuleId = $"r{i}",
                Success = i % 2 == 0,
                Domain = $"a{i}.example",
                ClientVersionMajor = "2025",
            })
            .Select((r, i) => { r.BatchTime = FixedBatchTime; r.Seq = i; return r; })
            .ToList();

        var outcome = await sink.SendAsync(records, new DateTimeOffset(FixedBatchTime, TimeSpan.Zero), CancellationToken.None);

        // Two chunks of 2 emit, the fifth record stays in the tail cache for the next flush.
        Assert.Equal(4, outcome.Written);
        Assert.Equal(0, outcome.Lost);

        var names = new List<string>();
        await foreach (var item in container.GetBlobsAsync(prefix: "fill_assist_rule_outcome/"))
        {
            names.Add(item.Name);
        }
        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.EndsWith(".parquet", n));
    }
}
