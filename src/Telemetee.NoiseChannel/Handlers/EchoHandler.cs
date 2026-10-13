using Bit.Telemetee.NoiseChannel.Protocol;

namespace Bit.Telemetee.NoiseChannel.Handlers;

/// <summary>Completes one NK handshake and returns the client's payload inside message 2.</summary>
internal sealed class EchoHandler(NoiseStaticKey staticKey)
{
    public byte[] Handle(ReadOnlySpan<byte> message1)
    {
        using var responder = new NoiseNkResponder(staticKey.KeyPair);
        var payload = responder.ReadMessage(message1);
        return responder.WriteMessage(payload);
    }
}
