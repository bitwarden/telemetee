using System.Text.Json;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Classifies an input value into a bucket label. A schema's <c>[Bucket(nameof(X))]</c> attribute
/// names a <c>public static readonly IBucketer</c> on the same type; the applier calls
/// <see cref="Classify"/> for each record and emits the returned label in place of the raw value.
/// Returning null means "no bucket matches this value," and the whole record is dropped.
///
/// Input is a <see cref="JsonElement"/> so a bucketer can accept any JSON-expressible type (number,
/// string for GUIDs or semvers or hostnames, bool, etc.) without the applier having to type-switch.
/// </summary>
public interface IBucketer
{
    string? Classify(JsonElement value);
}
