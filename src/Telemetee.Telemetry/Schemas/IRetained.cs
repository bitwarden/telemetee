namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// A sink record type that declares how long it may be stored.
/// </summary>
public interface IRetained
{
    static abstract TimeSpan RetentionTime { get; }
}
