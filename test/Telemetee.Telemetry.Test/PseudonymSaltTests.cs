using Bit.Telemetee.Telemetry.Pseudonymization;
using Xunit;

namespace Bit.Telemetee.Telemetry.Test;

public class PseudonymSaltTests
{
    [Fact]
    public void SameInput_SameSalt_YieldsSameOutput()
    {
        using var salt = new PseudonymSalt();
        Assert.Equal(salt.Pseudonymize("hello"), salt.Pseudonymize("hello"));
    }

    [Fact]
    public void SameInput_DifferentSalt_YieldsDifferentOutput()
    {
        using var a = new PseudonymSalt();
        using var b = new PseudonymSalt();
        Assert.NotEqual(a.Pseudonymize("hello"), b.Pseudonymize("hello"));
    }

    [Fact]
    public void OutputIsHex256Bits()
    {
        using var salt = new PseudonymSalt();
        var hex = salt.Pseudonymize("x");
        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.Contains(c, "0123456789abcdef"));
    }
}
