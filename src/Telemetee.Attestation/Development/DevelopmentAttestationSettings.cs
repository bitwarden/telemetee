namespace Bit.Telemetee.Attestation.Development;

/// <summary>Configuration for the development token issuer. The host binds this from its own configuration section.</summary>
public sealed class DevelopmentAttestationSettings
{
    /// <summary>
    /// Public URL the issuer's endpoints are mounted at. Becomes the token's <c>iss</c>, and
    /// <c>&lt;Issuer&gt;/certs</c> must serve the JWKS.
    /// </summary>
    public Uri Issuer { get; set; } = new("http://localhost:5000/0.1.0/dev-attestation");

    /// <summary>Value emitted as <c>x-ms-sevsnpvm-hostdata</c>, 64 hex characters.</summary>
    public string HostData { get; set; } = new('0', 64);

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);
}
