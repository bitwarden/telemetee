using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.TelemetryData;

/// <summary>
/// Teaches System.Text.Json which concrete <see cref="TelemetryRecord"/> subtype a given
/// <c>schema</c> discriminator maps to, and renames each subtype's properties PascalCase →
/// snake_case so the wire shape matches what <see cref="Schemas.SchemaBuilder"/> expects. Called
/// once at service registration so both ASP.NET Core model binding and the OpenAPI schema exporter
/// see the full polymorphism set without the base class needing to list derived types manually.
/// </summary>
internal static class TelemetryRecordTypeResolver
{
    public static string TelemetryRecordDiscriminatorPropertyName = typeof(TelemetryRecord).GetCustomAttribute<JsonPolymorphicAttribute>()!.TypeDiscriminatorPropertyName!;

    /// <summary>
    /// Returns an action that, when passed to <see cref="DefaultJsonTypeInfoResolver"/>'s
    /// <c>Modifiers</c>, attaches every <c>[Telemetry]</c>-marked subtype of
    /// <see cref="TelemetryRecord"/> in <paramref name="assembly"/> as a derived type and
    /// snake_cases their property names.
    /// </summary>
    public static Action<JsonTypeInfo> ForAssembly(Assembly assembly)
    {
        var derived = assembly.GetTypes()
            .Where(t => typeof(TelemetryRecord).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<TelemetryAttribute>()))
            .Where(pair => pair.Attr is not null)
            .Select(pair => new JsonDerivedType(pair.Type, pair.Attr!.Name))
            .ToList();

        return typeInfo =>
        {
            if (typeInfo.Type == typeof(TelemetryRecord))
            {
                typeInfo.PolymorphismOptions ??= new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = TelemetryRecordDiscriminatorPropertyName,
                    IgnoreUnrecognizedTypeDiscriminators = false,
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var d in derived)
                {
                    typeInfo.PolymorphismOptions.DerivedTypes.Add(d);
                }
                return;
            }

            if (typeof(TelemetryRecord).IsAssignableFrom(typeInfo.Type) && typeInfo.Kind == JsonTypeInfoKind.Object)
            {
                // Strict number handling keeps the OpenAPI document from declaring integer fields
                // as `type: ["integer", "string"]`, which the Rust generator turns into an unusable
                // empty wrapper struct. On the wire, telemetry integers are always numbers.
                typeInfo.NumberHandling = JsonNumberHandling.Strict;
                foreach (var property in typeInfo.Properties)
                {
                    property.Name = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
                    property.IsRequired = true;
                }
            }
        };
    }
}
