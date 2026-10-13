using Bit.Telemetee.NoiseChannel.Protocol;

namespace Bit.Telemetee.NoiseChannel;

internal sealed class NoiseStaticKey : INoiseStaticKey, IDisposable
{
    internal KeyPair KeyPair { get; } = KeyPair.Generate();

    public ReadOnlyMemory<byte> PublicKey => KeyPair.PublicKey;

    public void Dispose() => KeyPair.Dispose();
}
