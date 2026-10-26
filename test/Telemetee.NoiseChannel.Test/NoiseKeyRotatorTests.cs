using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bit.Telemetee.NoiseChannel.Test;

public class NoiseKeyRotatorTests
{
    private readonly IMeterFactory _meterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    [Fact]
    public async Task Rotates_OnEachTick_AndCountsOutcome()
    {
        var key = new NoiseStaticKey();
        var startPublicKey = SnapshotPublicKey(key);
        using var collector = new MetricCollector<long>(
            _meterFactory, NoiseChannelMetrics.MeterName, "telemetee.noise.key_rotations");

        var rotator = new NoiseKeyRotator(
            key,
            Options.Create(new NoiseChannelSettings { KeyRotationInterval = TimeSpan.FromMilliseconds(50) }),
            TimeProvider.System,
            new NoiseChannelMetrics(_meterFactory, "test"),
            NullLogger<NoiseKeyRotator>.Instance);

        using var cts = new CancellationTokenSource();
        await rotator.StartAsync(cts.Token);

        await WaitForRotationAsync(key, startPublicKey);

        await cts.CancelAsync();
        await rotator.StopAsync(CancellationToken.None);

        var outcomes = collector.GetMeasurementSnapshot().Select(m => (string?)m.Tags["outcome"]).ToList();
        Assert.Contains("ok", outcomes);
    }

    private static async Task WaitForRotationAsync(NoiseStaticKey key, byte[] startPublicKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (SnapshotPublicKey(key).AsSpan().SequenceEqual(startPublicKey))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Key did not rotate before deadline.");
            }
            await Task.Delay(10);
        }
    }

    private static byte[] SnapshotPublicKey(NoiseStaticKey key)
    {
        using var scope = key.AcquireScoped();
        return scope.PublicKey.ToArray();
    }
}
