using System.Text.Json.Nodes;
using Bit.Telemetee.Telemetry.TelemetryData;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Bit.Telemetee.Telemetry;

/// <summary>
/// Extensions that make the OpenAPI document emit a shape the openapi-generator Rust template
/// turns into a proper tagged union for <see cref="TelemetryRecord"/>. Call from the host's
/// <c>AddOpenApi</c> lambda.
/// </summary>
public static class TelemetryOpenApiExtensions
{
    /// <summary>
    /// Rewrites the <see cref="TelemetryRecord"/> schema's <c>anyOf</c> to <c>oneOf</c> (what
    /// JsonPolymorphic actually models) and aligns each <c>discriminator.mapping</c> entry with
    /// whatever name the schema exporter settled on for the matching variant.
    /// </summary>
    public static OpenApiOptions UseTelemetryRecordPolymorphism(this OpenApiOptions options)
    {
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type != typeof(TelemetryRecord))
            {
                return Task.CompletedTask;
            }
            if (schema.AnyOf is { Count: > 0 } anyOf
                && (schema.OneOf is null || schema.OneOf.Count == 0))
            {
                schema.OneOf = anyOf;
                schema.AnyOf = null;
            }
            return Task.CompletedTask;
        });
        // Discriminator mapping targets a stale schema name because marking subtype properties
        // IsRequired=true changes the generated name after the mapping is populated. A document
        // transformer runs after components are finalized, which is when we can match each mapping
        // key to the schema whose `schema` property enum contains that discriminator value.
        options.AddDocumentTransformer((document, _, _) =>
        {
            if (document.Components?.Schemas is not { } components
                || !components.TryGetValue(nameof(TelemetryRecord), out var record)
                || record.Discriminator?.Mapping is not { Count: > 0 } mapping
                || record.OneOf is not { Count: > 0 } variants)
            {
                return Task.CompletedTask;
            }
            var discriminatorKey = TelemetryRecordTypeResolver.TelemetryRecordDiscriminatorPropertyName;
            foreach (var mappingKey in mapping.Keys.ToList())
            {
                foreach (var variant in variants.OfType<OpenApiSchemaReference>())
                {
                    var id = variant.Reference?.Id;
                    if (id is null || !components.TryGetValue(id, out var variantSchema))
                    {
                        continue;
                    }
                    if (variantSchema.Properties is null
                        || !variantSchema.Properties.TryGetValue(discriminatorKey, out var disc)
                        || disc.Enum is not { Count: > 0 } enumValues)
                    {
                        continue;
                    }
                    if (enumValues.Any(v => v is JsonValue jv && jv.TryGetValue<string>(out var s) && s == mappingKey))
                    {
                        mapping[mappingKey] = new OpenApiSchemaReference(id);
                        break;
                    }
                }
            }
            return Task.CompletedTask;
        });
        return options;
    }
}
