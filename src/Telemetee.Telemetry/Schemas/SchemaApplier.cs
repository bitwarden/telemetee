using System.Text.Json;
using Bit.Telemetee.Telemetry.Pseudonymization;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Dispatches an incoming record to the <see cref="SchemaDescriptor.Process"/> delegate of the
/// schema named by its <c>schema</c> discriminator. The process delegate produces a typed
/// <see cref="TelemetrySinkRecord"/> or throws <see cref="DropException"/>; the applier catches
/// the exception and reports the embedded <see cref="DropReason"/>. The applier is intentionally
/// thin — every per-field decision lives in the delegate <see cref="SchemaBuilder"/> compiled from
/// the schema's attributes.
/// </summary>
internal sealed class SchemaApplier
{
    private readonly SchemaCatalog _catalog;
    private readonly PseudonymSaltFactory _saltFactory;

    public SchemaApplier(SchemaCatalog catalog, PseudonymSaltFactory saltFactory)
    {
        _catalog = catalog;
        _saltFactory = saltFactory;
    }

    public SchemaApplyResult Apply(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            return SchemaApplyResult.Failed(DropReason.MalformedJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schemaName) ||
                schemaName.ValueKind != JsonValueKind.String)
            {
                return SchemaApplyResult.Failed(DropReason.MalformedRecord);
            }

            if (!_catalog.TryGetByName(schemaName.GetString()!, out var descriptor))
            {
                return SchemaApplyResult.Failed(DropReason.UnknownSchema);
            }

            var context = new TreatmentContext(_saltFactory.Current);
            try
            {
                var record = descriptor.Process(root, context);
                return SchemaApplyResult.Ok(record);
            }
            catch (DropException drop)
            {
                return SchemaApplyResult.Failed(drop.Reason);
            }
        }
    }
}
