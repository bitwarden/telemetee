using System.Buffers.Text;
using System.Text.Json;
using Bit.Telemetee.NoiseChannel;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Attestation;

/// <summary>
/// Serves the current token for this process's Noise static key, refreshing it ahead of expiry. The
/// runtime data is fixed for the process lifetime because the key is.
/// </summary>
internal sealed class AttestationTokenProvider(
    IAttestationTokenSource source,
    INoiseStaticKey staticKey,
    IOptions<AttestationSettings> settings,
    TimeProvider timeProvider,
    AttestationMetrics metrics) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly byte[] _runtimeData = JsonSerializer.SerializeToUtf8Bytes(
        new RuntimeData(Convert.ToBase64String(staticKey.PublicKey.Span)));
    private string? _token;
    private DateTimeOffset _refreshAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && timeProvider.GetUtcNow() < _refreshAt)
        {
            return _token;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && timeProvider.GetUtcNow() < _refreshAt)
            {
                return _token;
            }

            string token;
            try
            {
                token = await source.GetTokenAsync(_runtimeData, cancellationToken);
                _refreshAt = ReadExpiry(token) - settings.Value.RefreshMargin;
            }
            catch
            {
                metrics.RefreshFailed();
                throw;
            }

            metrics.Refreshed();
            _token = token;
            return token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static DateTimeOffset ReadExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Attestation token is not a JWT.");
        }

        using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        if (!payload.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds))
        {
            throw new InvalidOperationException("Attestation token has no exp claim.");
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    public void Dispose() => _refreshLock.Dispose();

    private sealed record RuntimeData(string noise_pub);
}
