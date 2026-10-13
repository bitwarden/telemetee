using System.Security.Cryptography;
using System.Text;

namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>Noise SymmetricState over SHA-256, per revision 34 of the specification.</summary>
internal sealed class SymmetricState : IDisposable
{
    private readonly CipherState _cipher = new();
    private byte[] _chainingKey;
    private byte[] _hash;

    public SymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        _hash = new byte[NoiseProtocol.HashLength];
        if (name.Length <= NoiseProtocol.HashLength)
        {
            name.CopyTo(_hash, 0);
        }
        else
        {
            _hash = SHA256.HashData(name);
        }

        _chainingKey = (byte[])_hash.Clone();
    }

    public void MixKey(ReadOnlySpan<byte> input)
    {
        var (chainingKey, key) = Hkdf(_chainingKey, input);
        CryptographicOperations.ZeroMemory(_chainingKey);
        _chainingKey = chainingKey;
        _cipher.InitializeKey(key);
        CryptographicOperations.ZeroMemory(key);
    }

    public void MixHash(ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_hash);
        hash.AppendData(data);
        _hash = hash.GetHashAndReset();
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = _cipher.EncryptWithAd(_hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = _cipher.DecryptWithAd(_hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    private static (byte[] Output1, byte[] Output2) Hkdf(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> input)
    {
        var tempKey = HMACSHA256.HashData(chainingKey, input);
        ReadOnlySpan<byte> one = [0x01];
        var output1 = HMACSHA256.HashData(tempKey, one);
        var output2 = HMACSHA256.HashData(tempKey, (ReadOnlySpan<byte>)[.. output1, 0x02]);
        CryptographicOperations.ZeroMemory(tempKey);
        return (output1, output2);
    }

    public void Dispose()
    {
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_chainingKey);
    }
}
