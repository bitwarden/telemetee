using System.Text.Json;
using Bit.Telemetee.Telemetry.Pseudonymization;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>Applies a <see cref="TelemetrySchema"/> to a raw JSON record.</summary>
internal sealed class SchemaApplier
{
    private readonly Dictionary<string, TelemetrySchema> _schemas;
    private readonly PseudonymSalt _salt;

    public SchemaApplier(IEnumerable<TelemetrySchema> schemas, PseudonymSalt salt)
    {
        var materialized = schemas as IReadOnlyCollection<TelemetrySchema> ?? [.. schemas];
        EnsureUniqueNames(materialized);
        _schemas = materialized.ToDictionary(s => s.Name);
        _salt = salt;
    }

    /// <summary>
    /// Throws if two schemas share a <see cref="TelemetrySchema.Name"/>. The applier routes records
    /// to a schema by name, so a duplicate would silently shadow the earlier declaration. Called from
    /// <see cref="TelemetryServiceCollectionExtensions.AddTelemetry"/> at service registration time
    /// so the error surfaces at host boot, not at the first request.
    /// </summary>
    internal static void EnsureUniqueNames(IEnumerable<TelemetrySchema> schemas)
    {
        var seen = new HashSet<string>();
        foreach (var schema in schemas)
        {
            if (!seen.Add(schema.Name))
            {
                throw new InvalidOperationException(
                    $"Two telemetry schemas declare the name '{schema.Name}'. Each [Telemetry(...)] " +
                    "name must be unique.");
            }
        }
    }

    /// <summary>
    /// Applies the schema named by the record's <c>schema</c> field to a record that arrived inside
    /// an authed group. The matched schema must be authed; an unauthed schema sent in this scope is
    /// dropped as <see cref="DropReason.WrongAuthScope"/>.
    /// </summary>
    public SchemaApplyResult ApplyAuthed(
        ReadOnlySpan<byte> json,
        IReadOnlyDictionary<string, string> jwtClaims) =>
        ApplyCore(json, jwtClaims, authedScope: true);

    /// <summary>
    /// Applies the schema named by the record's <c>schema</c> field to a record that arrived outside
    /// any authed group. The matched schema must be unauthed; an authed schema sent in this scope is
    /// dropped as <see cref="DropReason.WrongAuthScope"/>.
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

            if (!_schemas.TryGetValue(schemaName.GetString()!, out var schema))
            {
                return SchemaApplyResult.Failed(DropReason.UnknownSchema);
            }

            // An authed schema may only arrive via ApplyAuthed; an unauthed schema only via
            // ApplyUnauthed. The entry-point split guarantees the caller's intent, this check
            // guarantees the record's declared schema agrees with it.
            if (schema.IsAuthed != authedScope)
            {
                return SchemaApplyResult.Failed(DropReason.WrongAuthScope);
            }

            var output = new Dictionary<string, object?>();
            foreach (var field in root.EnumerateObject())
            {
                if (field.NameEquals("schema"))
                {
                    continue;
                }

                if (!schema.Fields.TryGetValue(field.Name, out var treatment))
                {
                    // Fields not in the schema are implicitly dropped. This keeps older collector
                    // builds forward-compatible with clients that know about additional fields: the
                    // sink never sees them, and the record still carries whatever the schema does
                    // declare. A schema that wants a field *present* must declare it.
                    continue;
                }

                switch (treatment)
                {
                    case FieldTreatment.Keep:
                        output[field.Name] = ToPlain(field.Value);
                        break;
                    case FieldTreatment.Pseudonymize:
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            return SchemaApplyResult.Failed(DropReason.MalformedRecord);
                        }
                        output[field.Name] = _salt.Pseudonymize(field.Value.GetString()!);
                        break;
                    case FieldTreatment.Authed authed:
                        if (field.Value.ValueKind != JsonValueKind.String)
                        {
                            return SchemaApplyResult.Failed(DropReason.MalformedRecord);
                        }
                        // Reaching an Authed case requires schema.IsAuthed, and the scope check
                        // above guarantees jwtClaims is non-null whenever that is true.
                        if (!jwtClaims!.TryGetValue(authed.JwtClaim, out var expected))
                        {
                            return SchemaApplyResult.Failed(DropReason.MissingAuthClaim);
                        }
                        var provided = field.Value.GetString()!;
                        if (!string.Equals(provided, expected, StringComparison.Ordinal))
                        {
                            return SchemaApplyResult.Failed(DropReason.ClaimMismatch);
                        }
                        output[field.Name] = _salt.Pseudonymize(provided);
                        break;
                    case FieldTreatment.Bucket bucket:
                        var label = bucket.Bucketer.Classify(field.Value);
                        if (label is null)
                        {
                            return SchemaApplyResult.Failed(DropReason.MalformedRecord);
                        }
                        output[field.Name] = label;
                        break;
                    default:
                        // FieldTreatment is a closed hierarchy (private constructor, nested sealed
                        // records), so a new subtype requires editing this file. The throw ensures the
                        // edit is noisy if someone adds a subtype without updating this switch.
                        throw new InvalidOperationException(
                            $"Unhandled field treatment: {treatment.GetType().Name}");
                }
            }

            // Every declared [Authed] field must have been present in the record for verification
            // to have run. A record that omits an authed field asserted nothing, so it cannot be
            // trusted even if the JWT is otherwise valid.
            foreach (var (fieldName, treatment) in schema.Fields)
            {
                if (treatment is FieldTreatment.Authed && !output.ContainsKey(fieldName))
                {
                    return SchemaApplyResult.Failed(DropReason.MissingAuthClaim);
                }
            }

            return SchemaApplyResult.Ok(new AnonymizedRecord(schema.Name, output));
        }
    }

    private static object? ToPlain(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var i) ? i : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };
}

internal readonly record struct SchemaApplyResult(AnonymizedRecord? Record, DropReason? Reason)
{
    public bool IsOk => Record is not null;
    public static SchemaApplyResult Ok(AnonymizedRecord record) => new(record, null);
    public static SchemaApplyResult Failed(DropReason reason) => new(null, reason);
}

/// <summary>Reason a record did not reach the batch buffer. Reported as a metric tag.</summary>
internal enum DropReason
{
    MalformedJson,
    MalformedRecord,
    UnknownSchema,
    OversizeRecord,
    BufferFull,
    /// <summary>Authed schema sent in an unauthed group, or vice versa.</summary>
    WrongAuthScope,
    /// <summary>Authed field was absent from the record, or the token carried no value for the required JWT claim.</summary>
    MissingAuthClaim,
    /// <summary>Authed field was present but did not equal the corresponding JWT claim.</summary>
    ClaimMismatch,
    /// <summary>The JWT accompanying an authed record failed to parse.</summary>
    AuthMalformed,
}
