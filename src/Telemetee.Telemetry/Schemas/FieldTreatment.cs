namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// How a single telemetry field is handled by the collector. The treatment is declared in the attested
/// source, so the measurement covers it: a client that trusts the collector's hostdata trusts that the
/// field will be treated as declared.
/// </summary>
public abstract record FieldTreatment
{
    private FieldTreatment() { }

    /// <summary>Field is forwarded verbatim.</summary>
    public sealed record Keep : FieldTreatment;

    /// <summary>
    /// Field is classified by <see cref="IBucketer"/>: the applier emits the label the bucketer
    /// returns, or drops the whole record if the bucketer returns null.
    /// </summary>
    public sealed record Bucket(IBucketer Bucketer) : FieldTreatment;

    /// <summary>Field is replaced by its HMAC-SHA256 under the process-lifetime salt, hex-encoded.</summary>
    public sealed record Pseudonymize : FieldTreatment;

    /// <summary>
    /// Field is verified against the named JWT claim on the enclosing authed group and then
    /// HMAC-SHA256 pseudonymized the same way <see cref="Pseudonymize"/> would be. The record is
    /// rejected if the field is absent or does not equal the claim value.
    /// </summary>
    public sealed record Authed(string JwtClaim) : FieldTreatment;
}
