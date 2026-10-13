using Bit.Telemetee.NoiseChannel.Protocol;
using Xunit;

namespace Bit.Telemetee.NoiseChannel.Test.Protocol;

public class KeyPairTests
{
    [Fact]
    public void PublicKey_IsUncompressedSec1Point()
    {
        using var key = KeyPair.Generate();

        Assert.Equal(NoiseProtocol.PublicKeyLength, key.PublicKey.Length);
        Assert.Equal(0x04, key.PublicKey[0]);
    }

    [Fact]
    public void Dh_IsSymmetric()
    {
        using var a = KeyPair.Generate();
        using var b = KeyPair.Generate();

        Assert.Equal(a.Dh(b.PublicKey), b.Dh(a.PublicKey));
        Assert.Equal(32, a.Dh(b.PublicKey).Length);
    }

    [Fact]
    public void Dh_PointNotOnCurve_Throws()
    {
        using var a = KeyPair.Generate();
        using var b = KeyPair.Generate();
        var bogus = (byte[])b.PublicKey.Clone();
        bogus[40] ^= 0x01;

        Assert.Throws<NoiseException>(() => a.Dh(bogus));
    }

    [Fact]
    public void Dh_CompressedEncoding_Throws()
    {
        using var a = KeyPair.Generate();
        var compressed = new byte[33];
        compressed[0] = 0x02;

        Assert.Throws<NoiseException>(() => a.Dh(compressed));
    }
}
