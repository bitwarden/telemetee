namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// A declared telemetry type and the treatment for each of its fields. Enforced by attestation: a client
/// that trusts this build trusts that incoming records matching this schema will be transformed exactly
/// as declared, and that fields not listed here are dropped.
/// </summary>
public sealed record TelemetrySchema(string Name, IReadOnlyDictionary<string, FieldTreatment> Fields)
{
    /// <summary>
    /// True when any field uses the <see cref="FieldTreatment.Authed"/> treatment. Authed schemas
    /// are only accepted inside an authed group on the page; unauthed schemas are only accepted
    /// outside one. A schema is either authed or unauthed, never both — the two cases live as
    /// separate schemas by design.
    /// </summary>
    public bool IsAuthed { get; } = Fields.Values.OfType<FieldTreatment.Authed>().Any();
}
