namespace Bit.Telemetee.NoiseChannel.Protocol;

/// <summary>
/// Constants for the single Noise protocol the collector speaks. The suite is chosen so the server
/// needs nothing outside the .NET BCL: ECDH P-256, AES-256-GCM, and SHA-256.
/// </summary>
internal static class NoiseProtocol
{
    public const string Name = "Noise_NK_P256_AESGCM_SHA256";

    /// <summary>Uncompressed SEC1 point: 0x04 || X || Y.</summary>
    public const int PublicKeyLength = 65;

    public const int HashLength = 32;

    public const int TagLength = 16;

    /// <summary>Maximum Noise message length per the specification.</summary>
    public const int MaxMessageLength = 65535;
}
