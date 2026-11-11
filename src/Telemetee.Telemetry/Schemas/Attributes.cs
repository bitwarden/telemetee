namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Names a POCO as a telemetry schema. The <paramref name="name"/> is what records carry on the wire;
/// renaming the C# type does not change it, so the wire contract survives refactors.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TelemetryAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Base for the property treatments. Exactly one of these must be on every property that reaches the wire.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public abstract class TreatmentAttribute : Attribute
{
}

/// <summary>Field is forwarded verbatim.</summary>
public sealed class KeepAttribute : TreatmentAttribute;

/// <summary>
/// Field is replaced by its HMAC-SHA256 under the pseudonym salt, hex-encoded. The designated home
/// for this treatment is a client-generated <c>flow_id</c>: pseudonymizing it makes the correlation
/// token one-way at the collector while within-flow correlation remains intact. Applied to any
/// other kind of identifier it would re-enable the exact cross-event joins the collector exists to
/// prevent, so reach for it only on <c>flow_id</c> unless a schema review has blessed another use.
/// </summary>
public sealed class PseudonymizeAttribute : TreatmentAttribute;

/// <summary>
/// Field is classified by an <see cref="IBucketer{TIn, TOut}"/>. The bucketer lives in a <c>public
/// static readonly</c> member on the same class; <paramref name="bucketerMember"/> is its name
/// (use <c>nameof</c>). The compiled process emits whatever the bucketer returns in place of the
/// raw value, or drops the whole record if the bucketer returns null.
/// </summary>
public sealed class BucketAttribute(string bucketerMember) : TreatmentAttribute
{
    public string BucketerMember { get; } = bucketerMember;
}
