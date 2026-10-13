namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>
/// Initiator side of one Noise NK handshake. The production initiator is the Rust client; this one exists
/// so the responder can be exercised in tests without a second implementation.
/// </summary>
internal sealed class NoiseNkInitiator : IDisposable
{
    private readonly byte[] _remoteStatic;
    private readonly SymmetricState _state = new(NoiseProtocol.Name);
    private KeyPair? _ephemeral;

    public NoiseNkInitiator(byte[] remoteStatic)
    {
        _remoteStatic = remoteStatic;
        _state.MixHash([]);
        _state.MixHash(remoteStatic);
    }

    public byte[] WriteMessage(ReadOnlySpan<byte> payload)
    {
        if (_ephemeral is not null)
        {
            throw new InvalidOperationException("Message 1 already written.");
        }

        _ephemeral = KeyPair.Generate();
        _state.MixHash(_ephemeral.PublicKey);
        _state.MixKey(_ephemeral.Dh(_remoteStatic));
        var ciphertext = _state.EncryptAndHash(payload);
        return [.. _ephemeral.PublicKey, .. ciphertext];
    }

    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        if (_ephemeral is null)
        {
            throw new InvalidOperationException("Message 1 must be written first.");
        }

        if (message.Length < NoiseProtocol.PublicKeyLength + NoiseProtocol.TagLength)
        {
            throw new NoiseException("Invalid message length.");
        }

        var remoteEphemeral = message[..NoiseProtocol.PublicKeyLength];
        _state.MixHash(remoteEphemeral);
        _state.MixKey(_ephemeral.Dh(remoteEphemeral));
        return _state.DecryptAndHash(message[NoiseProtocol.PublicKeyLength..]);
    }

    public void Dispose()
    {
        _state.Dispose();
        _ephemeral?.Dispose();
    }
}
