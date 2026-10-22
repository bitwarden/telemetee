namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Thrown by a <see cref="Treatments"/> helper to signal that the record should be dropped. The
/// applier catches it and converts to <see cref="SchemaApplyResult.Failed"/>. Exception-based
/// signaling keeps the compiled process body a single <c>new Sink { ... }</c> member-init with no
/// per-field short-circuit ladders.
/// </summary>
internal sealed class DropException : Exception
{
    public DropReason Reason { get; }
    public DropException(DropReason reason) { Reason = reason; }
}
