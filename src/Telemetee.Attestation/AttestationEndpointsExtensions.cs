using Bit.Telemetee.Attestation.Development;
using Bit.Telemetee.NoiseChannel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bit.Telemetee.Attestation;

/// <summary>Maps the attestation endpoints as Minimal API endpoint groups.</summary>
public static class AttestationEndpointsExtensions
{
    /// <summary>Attaches the token endpoint to an empty group; the host owns the route prefix.</summary>
    public static RouteGroupBuilder MapAttestationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("");
        group.WithTags("Attestation");

        group.MapGet("", async (
                    AttestationTokenProvider provider,
                    ScopedNoiseKey scopedKey,
                    CancellationToken cancellationToken) =>
                Results.Ok(new AttestationResponse(
                    await provider.GetTokenAsync(scopedKey, cancellationToken))))
            .WithName("GetAttestation")
            .WithDescription("Returns the MAA token binding this collector's code identity to its Noise static key.");

        return group;
    }

    /// <summary>
    /// Attaches the development issuer's JWKS endpoint. The host must mount it at the path named by
    /// <see cref="DevelopmentAttestationSettings.Issuer"/> so clients resolve <c>&lt;iss&gt;/certs</c>.
    /// </summary>
    public static RouteGroupBuilder MapDevelopmentAttestationIssuerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("");
        group.WithTags("DevelopmentAttestation");

        group.MapGet("certs", (DevelopmentAttestationIssuer issuer) => Results.Ok(issuer.Jwks))
            .WithName("GetDevelopmentAttestationCerts")
            .WithDescription("JWKS for the development attestation issuer. Not present in production.");

        return group;
    }

    private sealed record AttestationResponse(string Token);
}
