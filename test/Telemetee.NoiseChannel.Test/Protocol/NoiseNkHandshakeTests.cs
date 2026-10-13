using Bit.Telemetee.NoiseChannel.Protocol;
using Xunit;

namespace Bit.Telemetee.NoiseChannel.Test.Protocol;

public class NoiseNkHandshakeTests
{
    [Fact]
    public void Handshake_RoundTripsPayloadsBothWays()
    {
        using var serverStatic = KeyPair.Generate();
        using var initiator = new NoiseNkInitiator(serverStatic.PublicKey);
        using var responder = new NoiseNkResponder(serverStatic);

        var message1 = initiator.WriteMessage("ping"u8);
        var request = responder.ReadMessage(message1);
        var message2 = responder.WriteMessage("pong"u8);
        var response = initiator.ReadMessage(message2);

        Assert.Equal("ping"u8.ToArray(), request);
        Assert.Equal("pong"u8.ToArray(), response);
    }

    [Fact]
    public void ReadMessage_WrongStaticKey_Throws()
    {
        using var serverStatic = KeyPair.Generate();
        using var otherStatic = KeyPair.Generate();
        using var initiator = new NoiseNkInitiator(otherStatic.PublicKey);
        using var responder = new NoiseNkResponder(serverStatic);

        var message1 = initiator.WriteMessage("ping"u8);

        Assert.Throws<NoiseException>(() => responder.ReadMessage(message1));
    }

    [Fact]
    public void ReadMessage_TamperedCiphertext_Throws()
    {
        using var serverStatic = KeyPair.Generate();
        using var initiator = new NoiseNkInitiator(serverStatic.PublicKey);
        using var responder = new NoiseNkResponder(serverStatic);

        var message1 = initiator.WriteMessage("ping"u8);
        message1[^1] ^= 0xFF;

        Assert.Throws<NoiseException>(() => responder.ReadMessage(message1));
    }

    [Fact]
    public void ReadMessage_TamperedEphemeral_Throws()
    {
        using var serverStatic = KeyPair.Generate();
        using var initiator = new NoiseNkInitiator(serverStatic.PublicKey);
        using var responder = new NoiseNkResponder(serverStatic);

        var message1 = initiator.WriteMessage("ping"u8);
        message1[5] ^= 0x01;

        Assert.Throws<NoiseException>(() => responder.ReadMessage(message1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(NoiseProtocol.PublicKeyLength + NoiseProtocol.TagLength - 1)]
    [InlineData(NoiseProtocol.MaxMessageLength + 1)]
    public void ReadMessage_InvalidLength_Throws(int length)
    {
        using var serverStatic = KeyPair.Generate();
        using var responder = new NoiseNkResponder(serverStatic);

        Assert.Throws<NoiseException>(() => responder.ReadMessage(new byte[length]));
    }

    [Fact]
    public void Initiator_ReadMessage_TamperedResponse_Throws()
    {
        using var serverStatic = KeyPair.Generate();
        using var initiator = new NoiseNkInitiator(serverStatic.PublicKey);
        using var responder = new NoiseNkResponder(serverStatic);

        responder.ReadMessage(initiator.WriteMessage("ping"u8));
        var message2 = responder.WriteMessage("pong"u8);
        message2[^1] ^= 0xFF;

        Assert.Throws<NoiseException>(() => initiator.ReadMessage(message2));
    }

    [Fact]
    public void WriteMessage_BeforeRead_Throws()
    {
        using var serverStatic = KeyPair.Generate();
        using var responder = new NoiseNkResponder(serverStatic);

        Assert.Throws<InvalidOperationException>(() => responder.WriteMessage("pong"u8));
    }
}
