using System.Diagnostics.Metrics;
using Bit.Telemetee.Attestation.Development;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bit.Telemetee.Attestation;

/// <summary>Registration entry points for the attestation library.</summary>
public static class AttestationServiceCollectionExtensions
{
    /// <summary>
    /// Registers token acquisition through the attestation sidecar. Requires <c>AddNoiseChannel()</c> and a
    /// bound <see cref="AttestationSettings"/>.
    /// </summary>
    public static IServiceCollection AddAttestation(this IServiceCollection services, string collectorVersion)
    {
        services.AddHttpClient(SidecarAttestationTokenSource.HttpClientName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAttestationTokenSource, SidecarAttestationTokenSource>();
        services.TryAddSingleton(sp => new AttestationMetrics(sp.GetRequiredService<IMeterFactory>(), collectorVersion));
        services.TryAddSingleton<AttestationTokenProvider>();
        return services;
    }

    /// <summary>
    /// Registers a self-signed token issuer that mimics MAA's claims, for running outside Azure. Call before
    /// <see cref="AddAttestation"/> so it wins the <c>TryAdd</c>. Requires a bound
    /// <see cref="DevelopmentAttestationSettings"/>.
    /// </summary>
    public static IServiceCollection AddDevelopmentAttestation(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DevelopmentAttestationIssuer>();
        services.TryAddSingleton<IAttestationTokenSource, DevelopmentAttestationTokenSource>();
        return services;
    }
}
