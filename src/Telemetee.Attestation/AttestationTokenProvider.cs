using System.Buffers.Text;
using System.Text.Json;
using Bit.Telemetee.NoiseChannel;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Attestation;

/// <summary>
/// Serves the MAA token bound to a caller-supplied <see cref="ScopedNoiseKey"/>. Refreshes ahead
/// of the token's expiry and also whenever the caller presents a different public key than the
/// cached token was minted for, so the token handed out always binds to the key the caller is
/// about to use.
/// </summary>
internal sealed class AttestationTokenProvider(
    IAttestationTokenSource source,
    IOptions<AttestationSettings> settings,
    TimeProvider timeProvider,
    AttestationMetrics metrics) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt;
    private byte[]? _tokenPublicKey;

    public async Task<string> GetTokenAsync(ScopedNoiseKey scopedKey, CancellationToken cancellationToken)
    {
        var publicKey = scopedKey.PublicKey;
        if (_token is not null
            && timeProvider.GetUtcNow() < _refreshAt
            && _tokenPublicKey is not null
            && publicKey.Span.SequenceEqual(_tokenPublicKey))
        {
            return _token;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null
                && timeProvider.GetUtcNow() < _refreshAt
                && _tokenPublicKey is not null
                && publicKey.Span.SequenceEqual(_tokenPublicKey))
            {
                return _token;
            }

            var runtimeData = JsonSerializer.SerializeToUtf8Bytes(
                new RuntimeData(Convert.ToBase64String(publicKey.Span)));

            string token;
            try
            {
                token = await source.GetTokenAsync(runtimeData, cancellationToken);
                _refreshAt = ReadExpiry(token) - settings.Value.RefreshMargin;
            }
            catch
            {
                metrics.RefreshFailed();
                throw;
            }

            metrics.Refreshed();
            _token = token;
            _tokenPublicKey = publicKey.ToArray();
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
