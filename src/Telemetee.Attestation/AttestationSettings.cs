namespace Bit.Telemetee.Attestation;

/// <summary>Configuration for obtaining MAA tokens. The host binds this from its own configuration section.</summary>
public sealed class AttestationSettings
{
    /// <summary>Base URL of the <c>mcr.microsoft.com/aci/skr</c> sidecar in the container group.</summary>
    public Uri SidecarUrl { get; set; } = new("http://localhost:8080");

    /// <summary>
    /// Host name of the Microsoft shared attestation provider, for example
    /// <c>sharedeus2.eus2.attest.azure.net</c>. Clients pin this issuer, so it must be a shared instance.
    /// </summary>
    public string MaaEndpoint { get; set; } = string.Empty;

    /// <summary>How long before the token's <c>exp</c> a fresh token is requested.</summary>
    public TimeSpan RefreshMargin { get; set; } = TimeSpan.FromMinutes(30);
}
