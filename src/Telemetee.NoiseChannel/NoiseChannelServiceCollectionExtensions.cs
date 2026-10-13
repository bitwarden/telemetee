using Bit.Telemetee.NoiseChannel.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>Registration entry point for the Noise channel library.</summary>
public static class NoiseChannelServiceCollectionExtensions
{
    /// <summary>Registers the process-lifetime Noise static key and the handshake handlers.</summary>
    public static IServiceCollection AddNoiseChannel(this IServiceCollection services)
    {
        services.TryAddSingleton<NoiseStaticKey>();
        services.TryAddSingleton<INoiseStaticKey>(sp => sp.GetRequiredService<NoiseStaticKey>());
        services.TryAddSingleton<EchoHandler>();
        services.TryAddSingleton<NoiseChannelMetrics>();
        return services;
    }
}
