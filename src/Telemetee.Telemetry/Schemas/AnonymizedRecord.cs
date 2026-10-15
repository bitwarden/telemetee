namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// A record after schema application: dropped fields are gone, pseudonymized fields are HMAC hex, and
/// bucketed fields are their range label. The batch stamps a timestamp onto this at flush, so the
/// record itself carries no arrival time.
/// <remarks>
/// TODO: strong typing for this value, possibly matching the input request type, such that output telemetry sinks can also benefit from strong type input
/// </remarks>
/// </summary>
public sealed record AnonymizedRecord(string Schema, IReadOnlyDictionary<string, object?> Fields);
