using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Attestation.Development;

/// <summary>
/// Signs RS256 tokens shaped like MAA's SEV-SNP tokens with a self-signed key generated at startup, and
/// publishes that key the way MAA does: a JWKS whose entries carry <c>kid</c> and <c>x5c</c>.
/// </summary>
internal sealed class DevelopmentAttestationIssuer : IDisposable
{
    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyId;
    private readonly string _issuer;
    private readonly DevelopmentAttestationSettings _settings;
    private readonly TimeProvider _timeProvider;

    public DevelopmentAttestationIssuer(IOptions<DevelopmentAttestationSettings> settings, TimeProvider timeProvider)
    {
        _settings = settings.Value;
        _timeProvider = timeProvider;
        _issuer = _settings.Issuer.ToString().TrimEnd('/');

        var request = new CertificateRequest(
            $"CN={_settings.Issuer.Host}", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = timeProvider.GetUtcNow();
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
        var der = certificate.Export(X509ContentType.Cert);
        _keyId = Base64Url.EncodeToString(SHA256.HashData(der));
        Jwks = new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "RSA",
                ["kid"] = _keyId,
                ["alg"] = "RS256",
                ["use"] = "sig",
                ["x5c"] = new JsonArray(Convert.ToBase64String(der)),
            }),
        };
    }

    public JsonObject Jwks { get; }

    public string Issue(ReadOnlySpan<byte> runtimeData)
    {
        var now = _timeProvider.GetUtcNow();
        var header = new JsonObject
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["kid"] = _keyId,
            ["jku"] = $"{_issuer}/certs",
        };
        var payload = new JsonObject
        {
            ["iss"] = _issuer,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(_settings.TokenLifetime).ToUnixTimeSeconds(),
            ["jti"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            ["x-ms-ver"] = "1.0",
            ["x-ms-attestation-type"] = "sevsnpvm",
            ["x-ms-compliance-status"] = "azure-compliant-uvm",
            ["x-ms-sevsnpvm-is-debuggable"] = false,
            ["x-ms-sevsnpvm-hostdata"] = _settings.HostData,
            ["x-ms-runtime"] = JsonNode.Parse(runtimeData),
        };

        var signingInput = $"{Encode(header)}.{Encode(payload)}";
        var signature = _key.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static string Encode(JsonNode node) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(node));

    public void Dispose() => _key.Dispose();
}
