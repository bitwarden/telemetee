using Bit.Telemetee.NoiseChannel.Protocol;
using Xunit;

namespace Bit.Telemetee.NoiseChannel.Test;

public class NoiseStaticKeyTests
{
    [Fact]
    public void AcquireScoped_PublicKey_IsAnUncompressedSec1Point()
    {
        var key = new NoiseStaticKey();
        using var scope = key.AcquireScoped();

        Assert.Equal(65, scope.PublicKey.Length);
        Assert.Equal(0x04, scope.PublicKey.Span[0]);
    }

    [Fact]
    public void Rotate_ReplacesPublicKey()
    {
        var key = new NoiseStaticKey();
        byte[] before;
        using (var scope = key.AcquireScoped())
        {
            before = scope.PublicKey.ToArray();
        }

        key.Rotate();

        using var after = key.AcquireScoped();
        Assert.False(after.PublicKey.Span.SequenceEqual(before));
    }

    [Fact]
    public void AcquireScoped_HeldKey_StaysUsableAcrossRotation()
    {
        var staticKey = new NoiseStaticKey();
        using var scope = staticKey.AcquireScoped();
        var captured = scope.KeyPair;

        staticKey.Rotate();

        // Still usable because the scope has not released its hold.
        _ = captured.Dh(captured.PublicKey);
    }

    [Fact]
    public void Rotate_WhenNoScopeHoldsPreviousKey_DisposesItImmediately()
    {
        var staticKey = new NoiseStaticKey();
        KeyPair captured;
        using (var scope = staticKey.AcquireScoped())
        {
            captured = scope.KeyPair;
        } // scope released; current key's refcount back to 0 (not yet retired).

        staticKey.Rotate();

        Assert.Throws<ObjectDisposedException>(() => captured.Dh(captured.PublicKey));
    }

    [Fact]
    public void ScopedNoiseKey_LastRelease_DisposesRetiredKey()
    {
        var staticKey = new NoiseStaticKey();
        var scope = staticKey.AcquireScoped();
        var captured = scope.KeyPair;

        staticKey.Rotate();
        _ = captured.Dh(captured.PublicKey); // still alive until the scope releases

        scope.Dispose();

        Assert.Throws<ObjectDisposedException>(() => captured.Dh(captured.PublicKey));
    }

    [Fact]
    public void ScopedNoiseKey_Dispose_IsIdempotent()
    {
        var staticKey = new NoiseStaticKey();
        var scope = staticKey.AcquireScoped();

        scope.Dispose();
        scope.Dispose(); // must not double-release and under-count other scopes' holds.

        // A fresh acquire + release cycle should still work.
        using var another = staticKey.AcquireScoped();
    }
}
