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
/// Maps the Development-only diagnostic telemetry endpoint. It exists for two reasons: anchoring
/// every <see cref="TelemetryRecord"/> subtype in the OpenAPI document so generated clients see the
/// full polymorphism set, and giving developers a plaintext path that bypasses the Noise channel
/// for inspecting what the schema applier would do with a record. It must only be mounted when
/// <c>ASPNETCORE_ENVIRONMENT=Development</c>; production rego pins the environment to Production
/// so deployed images never register it.
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
                var result = applier.Apply(json);
                return result.IsOk
                    ? Results.Ok(result.Record)
                    : Results.BadRequest(new { reason = result.Reason!.ToString() });
            })
            .WithName("DevelopmentTelemetry")
            .WithDescription(
                "Development only. Accepts a plaintext TelemetryRecord, runs it through the same " +
                "schema applier the Noise-channel path uses, and returns the anonymized output.");

        return group;
    }
}
