using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>Noise CipherState: AES-256-GCM with the 64-bit counter nonce in the last 8 bytes of a 12-byte IV.</summary>
internal sealed class CipherState : IDisposable
{
    private AesGcm? _aes;
    private ulong _nonce;

    public bool HasKey => _aes is not null;

    public void InitializeKey(ReadOnlySpan<byte> key)
    {
        _aes?.Dispose();
        _aes = new AesGcm(key, NoiseProtocol.TagLength);
        _nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (_aes is null)
        {
            return plaintext.ToArray();
        }

        var output = new byte[plaintext.Length + NoiseProtocol.TagLength];
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce);
        _aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
        _nonce++;
        return output;
    }

    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (_aes is null)
        {
            return ciphertext.ToArray();
        }

        if (ciphertext.Length < NoiseProtocol.TagLength)
        {
            throw new NoiseException("Ciphertext too short.");
        }

        var plaintextLength = ciphertext.Length - NoiseProtocol.TagLength;
        var output = new byte[plaintextLength];
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce);
        try
        {
            _aes.Decrypt(nonce, ciphertext[..plaintextLength], ciphertext[plaintextLength..], output, ad);
        }
        catch (CryptographicException)
        {
            throw new NoiseException("Authentication failed.");
        }

        _nonce++;
        return output;
    }

    private void WriteNonce(Span<byte> nonce)
    {
        if (_nonce == ulong.MaxValue)
        {
            throw new NoiseException("Nonce exhausted.");
        }

        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _nonce);
    }

    public void Dispose() => _aes?.Dispose();
}
