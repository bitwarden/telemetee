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

/// <summary>Base for the field treatments. Exactly one of these must be on every property that reaches the wire.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public abstract class TreatmentAttribute : Attribute
{
}

/// <summary>Field is forwarded verbatim.</summary>
public sealed class KeepAttribute : TreatmentAttribute;

/// <summary>
/// Field is replaced by its HMAC-SHA256 under the process-lifetime salt, hex-encoded. The value
/// comes from the record body: use this when a client-supplied identifier needs within-boot
/// correlation. For values that must come from a verified JWT instead, use
/// <see cref="AuthedAttribute"/>.
/// </summary>
public sealed class PseudonymizeAttribute : TreatmentAttribute;

/// <summary>
/// Field is verified against the named JWT claim on the enclosing authed group and then
/// pseudonymized the same way <see cref="PseudonymizeAttribute"/> would be. The client must send
/// the field; a value that does not equal the claim is rejected, as is a record that omits it.
/// Declaring any <see cref="AuthedAttribute"/> on a schema makes the schema an "authed schema":
/// its records are only accepted inside an authed group on the page.
/// </summary>
/// <param name="jwtClaim">The JWT claim name this field is checked against (for example, <c>sub</c>).</param>
public sealed class AuthedAttribute(string jwtClaim) : TreatmentAttribute
{
    public string JwtClaim { get; } = jwtClaim;
}

/// <summary>
/// Field is classified by an <see cref="IBucketer"/>. The bucketer lives in a <c>public static
/// readonly IBucketer</c> on the same class; <paramref name="bucketerMember"/> is its name (use
/// <c>nameof</c>). The applier emits the label the bucketer returns, or drops the whole record if
/// the bucketer returns null.
/// </summary>
public sealed class BucketAttribute(string bucketerMember) : TreatmentAttribute
{
    public string BucketerMember { get; } = bucketerMember;
}
