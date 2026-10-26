using Testcontainers.Azurite;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;

/// <summary>
/// Boots a single Azurite container for the lifetime of the test collection and hands out its
/// connection string. Mirrors the Microsoft.Extensions emulator-backed test pattern (one
/// container per collection) rather than per-test containers, which would add seconds per test;
/// per-test blob isolation comes from <see cref="SinkBuilder"/> minting a fresh storage
/// container name on every call.
/// </summary>
public sealed class AzuriteContainerFixture : IAsyncLifetime
{
    // Testcontainers.Azurite 4.1.0 pins the default image to 3.28.0, which predates the blob API
    // version that current Azure.Storage.Blobs sends. Pin to a current tag so the SDK round-trip
    // matches what the production SDK would do against a real storage account.
    private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.35.0";

    private readonly AzuriteContainer _container =
        new AzuriteBuilder().WithImage(AzuriteImage).Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteContainerFixture>
{
    public const string Name = "Azurite";
}
