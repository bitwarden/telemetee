using Azure.Storage.Blobs;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.Sink;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;

/// <summary>
/// Wires a <see cref="BlobStorageSink"/> to a fresh Azurite-backed container. One sink per test
/// so the per-type tail cache stays test-local; each call mints its own container name so blob
/// state does not leak between tests sharing the same <see cref="AzuriteContainerFixture"/>.
/// </summary>
internal static class SinkBuilder
{
    public static (BlobStorageSink Sink, BlobContainerClient Container) Build(
        AzuriteContainerFixture azurite,
        int targetChunkRecords = 512,
        TimeSpan? tailMaxDelay = null)
    {
        var settings = new BlobStorageSinkSettings
        {
            ConnectionString = azurite.ConnectionString,
            Container = NewContainerName(),
            TargetChunkRecords = targetChunkRecords,
            TailMaxDelay = tailMaxDelay ?? TimeSpan.FromMinutes(30),
        };
        var catalog = new SchemaCatalog(SchemaBuilder.FromAssembly(
            typeof(TelemetryServiceCollectionExtensions).Assembly));
        var sink = new BlobStorageSink(
            Options.Create(settings),
            catalog,
            NullLogger<BlobStorageSink>.Instance);
        var container = new BlobContainerClient(settings.ConnectionString, settings.Container);
        return (sink, container);
    }

    private static string NewContainerName() => $"it-{Guid.NewGuid():N}"[..24];
}
