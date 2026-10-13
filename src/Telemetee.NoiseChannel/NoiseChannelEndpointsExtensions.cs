using Bit.Telemetee.NoiseChannel.Handlers;
using Bit.Telemetee.NoiseChannel.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>Maps the Noise channel endpoints as a Minimal API endpoint group.</summary>
public static class NoiseChannelEndpointsExtensions
{
    private const string OctetStream = "application/octet-stream";

    /// <summary>Attaches the Noise channel endpoints to an empty group; the host owns the route prefix.</summary>
    public static RouteGroupBuilder MapNoiseChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("");
        group.WithTags("NoiseChannel");

        group.MapPost("echo", EchoAsync)
            .Accepts<byte[]>(OctetStream)
            .Produces(StatusCodes.Status200OK, contentType: OctetStream)
            .Produces(StatusCodes.Status400BadRequest)
            .WithName("Echo")
            .WithDescription("Completes a Noise NK handshake and returns the message 1 payload inside message 2.");

        return group;
    }

    private static async Task<IResult> EchoAsync(
        HttpRequest request,
        EchoHandler handler,
        NoiseChannelMetrics metrics,
        ILogger<EchoHandler> logger,
        CancellationToken cancellationToken)
    {
        var message1 = await ReadBodyAsync(request, cancellationToken);
        if (message1 is null)
        {
            metrics.RejectedSize();
            return Results.BadRequest();
        }

        try
        {
            var message2 = handler.Handle(message1);
            metrics.Accepted();
            return Results.Bytes(message2, OctetStream);
        }
        catch (NoiseException exception)
        {
            metrics.RejectedNoise();
            logger.LogDebug(exception, "Rejected Noise message");
            return Results.BadRequest();
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > NoiseProtocol.MaxMessageLength)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.Length > NoiseProtocol.MaxMessageLength ? null : buffer.ToArray();
    }
}
