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

    /// <summary>
    /// Processes a record that arrived inside an authed group. The matched schema must be authed;
    /// an unauthed schema sent in this scope is dropped as <see cref="DropReason.WrongAuthScope"/>.
    /// </summary>
    public SchemaApplyResult ApplyAuthed(
        ReadOnlySpan<byte> json,
        IReadOnlyDictionary<string, string> jwtClaims) =>
        ApplyCore(json, jwtClaims, authedScope: true);

    /// <summary>
    /// Processes a record that arrived outside any authed group. The matched schema must be
    /// unauthed; an authed schema sent in this scope is dropped as
    /// <see cref="DropReason.WrongAuthScope"/>.
    /// </summary>
    public SchemaApplyResult ApplyUnauthed(ReadOnlySpan<byte> json) =>
        ApplyCore(json, jwtClaims: null, authedScope: false);

    private SchemaApplyResult ApplyCore(
        ReadOnlySpan<byte> json,
        IReadOnlyDictionary<string, string>? jwtClaims,
        bool authedScope)
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

            // An authed schema may only arrive via ApplyAuthed; an unauthed schema only via
            // ApplyUnauthed. The entry-point split guarantees the caller's intent, this check
            // guarantees the record's declared schema agrees with it.
            if (descriptor.IsAuthed != authedScope)
            {
                return SchemaApplyResult.Failed(DropReason.WrongAuthScope);
            }

            var context = new TreatmentContext(_saltFactory.Current, jwtClaims);
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
