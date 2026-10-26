using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>
/// Rotates the collector's Noise static key on
/// <see cref="NoiseChannelSettings.KeyRotationInterval"/>. The swap is atomic and the previous
/// key is left reachable for in-flight handshakes; a client whose attestation fetch saw the old
/// key and whose POST hits the new key gets a Noise decryption failure and retries after
/// re-attesting.
/// </summary>
internal sealed class NoiseKeyRotator(
    NoiseStaticKey staticKey,
    IOptions<NoiseChannelSettings> settings,
    TimeProvider timeProvider,
    NoiseChannelMetrics metrics,
    ILogger<NoiseKeyRotator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = settings.Value.KeyRotationInterval;
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    staticKey.Rotate();
                    metrics.Rotated();
                }
                catch (Exception error)
                {
                    metrics.RotationFailed();
                    logger.LogError(error, "Noise static key rotation failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
