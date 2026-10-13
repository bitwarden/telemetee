namespace Bit.Telemetee.NoiseChannel;

/// <summary>
/// The collector's Noise static key for the lifetime of this process. Only the public half is exposed; it is
/// what the attestation token binds to, so other libraries read it and nothing else.
/// </summary>
public interface INoiseStaticKey
{
    /// <summary>Uncompressed SEC1 P-256 point, 65 bytes.</summary>
    ReadOnlyMemory<byte> PublicKey { get; }
}
