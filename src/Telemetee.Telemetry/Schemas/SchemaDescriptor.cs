using System.Text.Json;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Signature of a schema's compiled process delegate: take the record's parsed JSON object plus
/// the runtime context (salt, authed claims if any), apply every declared field treatment, and
/// return a typed sink record. Any treatment that cannot complete throws
/// <see cref="DropException"/>; the applier catches it and reports the embedded
/// <see cref="DropReason"/>.
/// </summary>
internal delegate TelemetrySinkRecord ProcessFunc(JsonElement root, TreatmentContext context);

/// <summary>
/// Everything the pipeline needs to accept, anonymize, and store one schema. Built once at startup
/// by <see cref="SchemaBuilder.FromType"/> after reflecting on the input type's attributes, its
/// nested <c>Sink</c>, and the sink's declared retention.
///
/// <list type="bullet">
///   <item><c>Name</c>: the wire discriminator from the input's <c>[Telemetry(name)]</c>.</item>
///   <item><c>IsAuthed</c>: declared on <see cref="TelemetryAttribute.Authed"/>; selects which handler scope accepts the record.</item>
///   <item><c>SinkType</c>: the nested <c>Sink</c> class <see cref="Process"/> returns.</item>
///   <item><c>RetentionTime</c>: the attested storage lifetime declared on the sink via <see cref="IRetained"/>.</item>
///   <item><c>Process</c>: the delegate the applier calls once the record's JSON parses. Compiled from the input's treatment attributes.</item>
/// </list>
/// </summary>
internal sealed record SchemaDescriptor(
    string Name,
    bool IsAuthed,
    Type InputType,
    Type SinkType,
    TimeSpan RetentionTime,
    ProcessFunc Process);
