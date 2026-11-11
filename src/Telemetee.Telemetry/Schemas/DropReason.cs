namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>Reason a record did not reach the batch buffer. Reported as a metric tag.</summary>
internal enum DropReason
{
    MalformedJson,
    MalformedRecord,
    UnknownSchema,
    OversizeRecord,
    BufferFull,
}
