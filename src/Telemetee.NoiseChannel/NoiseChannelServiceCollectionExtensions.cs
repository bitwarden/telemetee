using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>Registration entry point for the Noise channel library.</summary>
public static class NoiseChannelServiceCollectionExtensions
{
    /// <summary>Registers the Noise static key and the background rotator that cycles it. The
    /// host must bind <see cref="NoiseChannelSettings"/> from configuration.</summary>
    public static IServiceCollection AddNoiseChannel(this IServiceCollection services, string collectorVersion)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NoiseStaticKey>();
        services.TryAddSingleton(sp => new NoiseChannelMetrics(sp.GetRequiredService<IMeterFactory>(), collectorVersion));
        services.TryAddScoped(sp => sp.GetRequiredService<NoiseStaticKey>().AcquireScoped());
        services.AddHostedService<NoiseKeyRotator>();
        return services;
    }
}
