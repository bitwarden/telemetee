using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Bit.Telemetee.Attestation.Development;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bit.Telemetee.Attestation.Test.Development;

public class DevelopmentAttestationIssuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static DevelopmentAttestationIssuer CreateIssuer(DevelopmentAttestationSettings? settings = null) =>
        new(Options.Create(settings ?? new DevelopmentAttestationSettings()), new FakeTimeProvider(Now));

    [Fact]
    public void Issue_EmitsMaaShapedClaims()
    {
        var settings = new DevelopmentAttestationSettings
        {
            Issuer = new Uri("http://localhost:5000/dev/attestation/"),
            HostData = new string('a', 64),
            TokenLifetime = TimeSpan.FromHours(8),
        };
        using var issuer = CreateIssuer(settings);

        var token = issuer.Issue("""{"noise_pub":"abc"}"""u8);
        var (header, payload) = Decode(token);

        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("http://localhost:5000/dev/attestation/certs", header.GetProperty("jku").GetString());
        Assert.Equal("http://localhost:5000/dev/attestation", payload.GetProperty("iss").GetString());
        Assert.Equal(Now.ToUnixTimeSeconds(), payload.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddHours(8).ToUnixTimeSeconds(), payload.GetProperty("exp").GetInt64());
        Assert.Equal("sevsnpvm", payload.GetProperty("x-ms-attestation-type").GetString());
        Assert.Equal("azure-compliant-uvm", payload.GetProperty("x-ms-compliance-status").GetString());
        Assert.False(payload.GetProperty("x-ms-sevsnpvm-is-debuggable").GetBoolean());
        Assert.Equal(new string('a', 64), payload.GetProperty("x-ms-sevsnpvm-hostdata").GetString());
        Assert.Equal("abc", payload.GetProperty("x-ms-runtime").GetProperty("noise_pub").GetString());
    }

    [Fact]
    public void Issue_SignatureVerifiesAgainstPublishedCertificate()
    {
        using var issuer = CreateIssuer();

        var token = issuer.Issue("{}"u8);
        var parts = token.Split('.');
        var (header, _) = Decode(token);

        var jwk = issuer.Jwks["keys"]![0]!;
        Assert.Equal(jwk["kid"]!.GetValue<string>(), header.GetProperty("kid").GetString());
        using var certificate = X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(jwk["x5c"]![0]!.GetValue<string>()));
        using var rsa = certificate.GetRSAPublicKey()!;
        var verified = rsa.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        Assert.True(verified);
    }

    private static (JsonElement Header, JsonElement Payload) Decode(string token)
    {
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        return (
            JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement,
            JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement);
    }
}
