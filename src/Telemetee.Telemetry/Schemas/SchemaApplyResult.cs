namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Outcome of running a schema's <see cref="SchemaDescriptor.Process"/> delegate on a raw
/// record. Either a typed sink record built from the input, or a <see cref="DropReason"/>
/// explaining why the record did not make it.
/// </summary>
internal readonly record struct SchemaApplyResult(TelemetrySinkRecord? Record, DropReason? Reason)
{
    public bool IsOk => Record is not null;
    public static SchemaApplyResult Ok(TelemetrySinkRecord record) => new(record, null);
    public static SchemaApplyResult Failed(DropReason reason) => new(null, reason);
}
