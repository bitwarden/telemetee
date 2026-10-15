using System.Text.Json.Serialization;

namespace Bit.Telemetee.Telemetry.TelemetryData;

/// <summary>
/// Base for every <c>[Telemetry]</c>-marked POCO. Derived types are discovered at startup and
/// registered as polymorphic variants discriminated by the <c>schema</c> property, which is the
/// name given to the type's <c>[Telemetry(...)]</c> attribute. The wire shape is flat: the record
/// is a single JSON object whose <c>schema</c> property picks the variant and whose remaining
/// properties are the fields. Clients generated from the OpenAPI document consume this shape.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "schema")]
public abstract class TelemetryRecord;
