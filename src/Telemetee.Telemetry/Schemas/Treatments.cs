using System.Text.Json;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Pre-written converters that <see cref="SchemaBuilder"/> composes into the per-schema process
/// delegate at startup, one call per field. Each helper reads one JSON property as the declared
/// input type, applies the field treatment, and returns the output type. A missing or wrong-typed
/// field, a bucketer that returns null, an Authed field that does not match its claim — anything
/// that would prevent the record from being emitted — throws <see cref="DropException"/>, which
/// the applier catches and reports as the appropriate <see cref="DropReason"/>.
///
/// The exception-based signaling keeps the compiled process body a single
/// <c>new Sink { ... }</c> member-init expression. Nothing in the body has to short-circuit the
/// chain by hand: the first helper that throws unwinds past every later binding, and the applier
/// turns the exception into a drop.
/// </summary>
internal static class Treatments
{
    public static string KeepString(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new DropException(DropReason.MalformedRecord);
        }
        return element.GetString()!;
    }

    public static bool KeepBool(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new DropException(DropReason.MalformedRecord),
        };
    }

    public static int KeepInt(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            throw new DropException(DropReason.MalformedRecord);
        }
        return value;
    }

    public static long KeepLong(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            throw new DropException(DropReason.MalformedRecord);
        }
        return value;
    }

    public static float KeepFloat(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var value))
        {
            throw new DropException(DropReason.MalformedRecord);
        }
        return value;
    }

    public static double KeepDouble(JsonElement root, string name)
    {
        var element = RequireProperty(root, name);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
        {
            throw new DropException(DropReason.MalformedRecord);
        }
        return value;
    }

    public static string PseudonymizeString(JsonElement root, string name, TreatmentContext context)
    {
        var raw = KeepString(root, name);
        return context.Salt.Pseudonymize(raw);
    }

    /// <summary>
    /// Reads <paramref name="name"/> as a string, verifies it equals the enclosing authed group's
    /// JWT claim <paramref name="jwtClaim"/>, and returns the HMAC pseudonym of the value. Throws
    /// <see cref="DropReason.MissingAuthClaim"/> if the token carried no value for the claim and
    /// <see cref="DropReason.ClaimMismatch"/> if the asserted value differs from the claim.
    /// </summary>
    public static string Authed(JsonElement root, string name, string jwtClaim, TreatmentContext context)
    {
        var provided = KeepString(root, name);
        if (context.Claims is null || !context.Claims.TryGetValue(jwtClaim, out var expected))
        {
            throw new DropException(DropReason.MissingAuthClaim);
        }
        if (!string.Equals(provided, expected, StringComparison.Ordinal))
        {
            throw new DropException(DropReason.ClaimMismatch);
        }
        return context.Salt.Pseudonymize(provided);
    }

    public static string Bucket(JsonElement root, string name, IBucketer<double> bucketer)
    {
        var value = KeepDouble(root, name);
        return bucketer.Classify(value) ?? throw new DropException(DropReason.MalformedRecord);
    }

    public static string Bucket(JsonElement root, string name, IBucketer<long> bucketer)
    {
        var value = KeepLong(root, name);
        return bucketer.Classify(value) ?? throw new DropException(DropReason.MalformedRecord);
    }

    public static string Bucket(JsonElement root, string name, IBucketer<int> bucketer)
    {
        var value = KeepInt(root, name);
        return bucketer.Classify(value) ?? throw new DropException(DropReason.MalformedRecord);
    }

    public static string Bucket(JsonElement root, string name, IBucketer<string> bucketer)
    {
        var value = KeepString(root, name);
        return bucketer.Classify(value) ?? throw new DropException(DropReason.MalformedRecord);
    }

    private static JsonElement RequireProperty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            throw new DropException(DropReason.MalformedRecord);
        }
        return element;
    }
}
