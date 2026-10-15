using System.Buffers.Text;
using System.Text.Json;

namespace Bit.Telemetee.Telemetry;

/// <summary>
/// Phase-A JWT handling: parse the payload for its string-valued claims so the schema applier can
/// verify <c>[Authed]</c> fields against them. The signature, expiry, issuer, and audience are not
/// checked yet; that is Phase B. Shared between the Noise-channel handler and the plaintext dev
/// endpoint so both paths agree on how a token decomposes into claims.
/// </summary>
internal static class JwtClaims
{
    /// <summary>
    /// Base64url-decodes the JWT payload segment and returns its claims as strings. Returns
    /// <c>null</c> if the token is structurally invalid or the payload is not a JSON object.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? TryParse(string token)
    {
        var firstDot = token.IndexOf('.');
        if (firstDot <= 0)
        {
            return null;
        }
        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot <= firstDot + 1)
        {
            return null;
        }
        var payloadSegment = token.AsSpan(firstDot + 1, secondDot - firstDot - 1);
        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(payloadSegment);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                claims[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };
            }
            return claims;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
