namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>
/// Responder side of one Noise NK handshake (<c>-&gt; e, es</c> / <c>&lt;- e, ee</c>). One instance serves one
/// request: the client's payload rides in message 1 and the reply rides in message 2, so no transport
/// phase follows.
/// </summary>
internal sealed class NoiseNkResponder : IDisposable
{
    private readonly KeyPair _staticKey;
    private readonly SymmetricState _state = new(NoiseProtocol.Name);
    private byte[]? _remoteEphemeral;

    public NoiseNkResponder(KeyPair staticKey)
    {
        _staticKey = staticKey;
        _state.MixHash([]);
        _state.MixHash(staticKey.PublicKey);
    }

    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        if (_remoteEphemeral is not null)
        {
            throw new InvalidOperationException("Message 1 already read.");
        }

        if (message.Length < NoiseProtocol.PublicKeyLength + NoiseProtocol.TagLength ||
            message.Length > NoiseProtocol.MaxMessageLength)
        {
            throw new NoiseException("Invalid message length.");
        }

        var remoteEphemeral = message[..NoiseProtocol.PublicKeyLength].ToArray();
        _state.MixHash(remoteEphemeral);
        _state.MixKey(_staticKey.Dh(remoteEphemeral));
        var payload = _state.DecryptAndHash(message[NoiseProtocol.PublicKeyLength..]);
        _remoteEphemeral = remoteEphemeral;
        return payload;
    }

    public byte[] WriteMessage(ReadOnlySpan<byte> payload)
    {
        if (_remoteEphemeral is null)
        {
            throw new InvalidOperationException("Message 1 must be read first.");
        }

        using var ephemeral = KeyPair.Generate();
        _state.MixHash(ephemeral.PublicKey);
        _state.MixKey(ephemeral.Dh(_remoteEphemeral));
        var ciphertext = _state.EncryptAndHash(payload);
        return [.. ephemeral.PublicKey, .. ciphertext];
    }

    public void Dispose() => _state.Dispose();
}
