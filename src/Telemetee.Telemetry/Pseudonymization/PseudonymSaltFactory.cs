namespace Bit.Telemetee.Telemetry.Pseudonymization;

/// <summary>
/// Hands out the <see cref="PseudonymSalt"/> instance the collector should use for records being
/// processed right now.
/// </summary>
public sealed class PseudonymSaltFactory : IDisposable
{
    private readonly PseudonymSalt _current = new();

    public PseudonymSalt Current => _current;

    public void Dispose() => _current.Dispose();
}
