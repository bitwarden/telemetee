using System.Text.Json;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.TelemetryData;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using FromBodyAttribute = Microsoft.AspNetCore.Mvc.FromBodyAttribute;

namespace Bit.Telemetee.Telemetry;

/// <summary>
/// Maps the Development-only diagnostic telemetry endpoints. These exist for two reasons: anchoring
/// every <see cref="TelemetryRecord"/> subtype in the OpenAPI document so generated clients see the
/// full polymorphism set, and giving developers a plaintext path that bypasses the Noise channel
/// for inspecting what the schema applier would do with a record. One endpoint is unauthed; the
/// authed variant takes an <c>Authorization: Bearer &lt;jwt&gt;</c> header and runs the same
/// claim-verification path the Noise handler runs on authed groups. These must only be mounted
/// when <c>ASPNETCORE_ENVIRONMENT=Development</c>; production rego pins the environment to
/// Production so deployed images never register them.
/// </summary>
public static class TelemetryDevEndpointsExtensions
{
    public static RouteGroupBuilder MapDevelopmentTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("");
        group.WithTags("DevelopmentTelemetry");

        group.MapPost("", (
                [FromBody] TelemetryRecord record,
                SchemaApplier applier,
                IOptions<JsonOptions> jsonOptions) =>
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(record, jsonOptions.Value.SerializerOptions);
                var result = applier.ApplyUnauthed(json);
                return result.IsOk
                    ? Results.Ok(result.Record)
                    : Results.BadRequest(new { reason = result.Reason!.ToString() });
            })
            .WithName("DevelopmentTelemetry")
            .WithDescription(
                "Development only. Accepts a plaintext TelemetryRecord, runs it through the same " +
                "schema applier the Noise-channel path uses, and returns the anonymized output.");

        group.MapPost("authed", (
                HttpRequest request,
                [FromBody] TelemetryRecord record,
                SchemaApplier applier,
                IOptions<JsonOptions> jsonOptions) =>
            {
                if (!TryReadBearerToken(request, out var token))
                {
                    return Results.BadRequest(new { reason = "missing_bearer_token" });
                }
                var claims = JwtClaims.TryParse(token);
                if (claims is null)
                {
                    return Results.BadRequest(new { reason = "auth_malformed" });
                }

                var json = JsonSerializer.SerializeToUtf8Bytes(record, jsonOptions.Value.SerializerOptions);
                var result = applier.ApplyAuthed(json, claims);
                return result.IsOk
                    ? Results.Ok(result.Record)
                    : Results.BadRequest(new { reason = result.Reason!.ToString() });
            })
            .WithName("DevelopmentTelemetryAuthed")
            .WithDescription(
                "Development only. Accepts a plaintext TelemetryRecord plus an 'Authorization: " +
                "Bearer <jwt>' header, parses the token's claims the same way the Noise-channel " +
                "handler does, and returns the anonymized output or a 400 with the drop reason.");

        return group;
    }

    private static bool TryReadBearerToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!request.Headers.TryGetValue("Authorization", out var values))
        {
            return false;
        }
        var value = values.ToString();
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length)
        {
            return false;
        }
        token = value[prefix.Length..];
        return true;
    }
}
