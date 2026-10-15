using System.Security.Cryptography;

namespace Bit.Telemetee.Telemetry.Pseudonymization;

/// <summary>
/// Per-process HMAC-SHA256 key used to pseudonymize fields marked
/// <see cref="Schemas.FieldTreatment.Pseudonymize"/>. Drawn once at boot from the OS CSPRNG, held in
/// memory for the process lifetime, and never logged, exported, or persisted. Loss on restart is the
/// design: no record from before a restart can be linked to one after.
/// </summary>
public sealed class PseudonymSalt : IDisposable
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);

    public string Pseudonymize(string value)
    {
        Span<byte> digest = stackalloc byte[32];
        HMACSHA256.HashData(_salt, System.Text.Encoding.UTF8.GetBytes(value), digest);
        return Convert.ToHexStringLower(digest);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_salt);
}
