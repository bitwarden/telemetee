using Bit.Telemetee.Telemetry.Pages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bit.Telemetee.Telemetry;

/// <summary>Maps the telemetry endpoint as a Minimal API endpoint group.</summary>
public static class TelemetryEndpointsExtensions
{
    private const string OctetStream = "application/octet-stream";

    /// <summary>Header the client sends to declare which collector version it believes it is talking to.</summary>
    public const string TargetVersionHeader = "Telemetee-Target-Version";

    /// <summary>
    /// Attaches the telemetry endpoint to an empty group; the host owns the route prefix.
    /// Pages whose <see cref="TargetVersionHeader"/> does not equal <paramref name="collectorVersion"/>
    /// are rejected with 400 before any crypto work. The version check is why there is no URL version
    /// segment: an attestation measurement is pinned per build, so a stale client reaching a newer
    /// collector must fail loudly rather than silently flow records through a potentially-changed schema.
    /// </summary>
    public static RouteGroupBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints, string collectorVersion)
    {
        var group = endpoints.MapGroup("");
        group.WithTags("Telemetry");

        group.MapPost("telemetry", async (
                HttpRequest request,
                TelemetryHandler handler,
                TelemetryMetrics metrics,
                CancellationToken cancellationToken) =>
            {
                if (!request.Headers.TryGetValue(TargetVersionHeader, out var declared) ||
                    !string.Equals(declared.ToString(), collectorVersion, StringComparison.Ordinal))
                {
                    metrics.PageRejectedVersion();
                    return Results.BadRequest();
                }

                var body = await ReadPageAsync(request, cancellationToken);
                if (body is null)
                {
                    metrics.PageRejectedSize();
                    return Results.BadRequest();
                }

                var response = handler.HandlePage(body);
                return Results.Bytes(response, OctetStream);
            })
            .Accepts<byte[]>(OctetStream)
            .Produces(StatusCodes.Status200OK, contentType: OctetStream)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("Telemetry")
            .WithDescription(
                $"Accepts an exactly-{PageFormat.PageLength}-byte NK message 1 page carrying a " +
                $"'{TargetVersionHeader}' header equal to this collector's version, and returns an " +
                $"exactly-{PageFormat.ResponseLength}-byte NK message 2 with an empty payload.");

        return group;
    }

    private static async Task<byte[]?> ReadPageAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength != PageFormat.PageLength)
        {
            return null;
        }

        var buffer = new byte[PageFormat.PageLength];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return null;
            }
            offset += read;
        }

        var trailing = new byte[1];
        if (await request.Body.ReadAsync(trailing.AsMemory(), cancellationToken) != 0)
        {
            return null;
        }

        return buffer;
    }
}
