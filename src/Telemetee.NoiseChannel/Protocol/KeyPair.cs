using System.Security.Cryptography;

namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>P-256 key pair used for Noise DH. Public keys are exchanged as uncompressed SEC1 points to match snow.</summary>
internal sealed class KeyPair : IDisposable
{
    private readonly ECDiffieHellman _key;

    private KeyPair(ECDiffieHellman key)
    {
        _key = key;
        var q = key.ExportParameters(false).Q;
        PublicKey = new byte[NoiseProtocol.PublicKeyLength];
        PublicKey[0] = 0x04;
        q.X!.CopyTo(PublicKey, 1);
        q.Y!.CopyTo(PublicKey, 33);
    }

    public byte[] PublicKey { get; }

    public static KeyPair Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Returns the x-coordinate of the shared point, which is what snow's P-256 DH emits.</summary>
    public byte[] Dh(ReadOnlySpan<byte> remotePublicKey)
    {
        if (remotePublicKey.Length != NoiseProtocol.PublicKeyLength || remotePublicKey[0] != 0x04)
        {
            throw new NoiseException("Invalid public key encoding.");
        }

        try
        {
            using var remote = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = remotePublicKey[1..33].ToArray(),
                    Y = remotePublicKey[33..65].ToArray(),
                },
            });
            return _key.DeriveRawSecretAgreement(remote.PublicKey);
        }
        catch (CryptographicException)
        {
            throw new NoiseException("Invalid public key.");
        }
    }

    public void Dispose() => _key.Dispose();
}
