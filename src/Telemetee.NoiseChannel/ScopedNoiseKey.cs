using Bit.Telemetee.NoiseChannel.Protocol;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>
/// Per-DI-scope handle to the Noise static key current when the scope opened. Request handlers
/// take this so their key stays fixed for the scope's lifetime by construction. Dispose releases
/// the hold back to <see cref="NoiseStaticKey"/>, which then disposes the underlying key once it
/// has been rotated out of currency and no scope still holds it.
/// </summary>
public sealed class ScopedNoiseKey : IDisposable
{
    private readonly NoiseStaticKey _owner;
    private int _disposed;

    internal ScopedNoiseKey(KeyPair keyPair, NoiseStaticKey owner)
    {
        KeyPair = keyPair;
        _owner = owner;
    }

    /// <summary>The underlying <see cref="Protocol.KeyPair"/>, visible only to in-assembly callers
    /// that need to run DH (e.g., the responder). External callers get the public half via
    /// <see cref="PublicKey"/>.</summary>
    internal KeyPair KeyPair { get; }

    /// <summary>Uncompressed SEC1 P-256 point, 65 bytes. The key the collector's attestation
    /// token should bind to for the scope's lifetime.</summary>
    public ReadOnlyMemory<byte> PublicKey => KeyPair.PublicKey;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Release(KeyPair);
        }
    }
}
