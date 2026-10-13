using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Attestation;

/// <summary>Requests tokens from the attestation sidecar's <c>POST /attest/maa</c> endpoint.</summary>
internal sealed class SidecarAttestationTokenSource(
    IHttpClientFactory httpClientFactory,
    IOptions<AttestationSettings> settings) : IAttestationTokenSource
{
    public const string HttpClientName = "AttestationSidecar";

    public async Task<string> GetTokenAsync(ReadOnlyMemory<byte> runtimeData, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var request = new SidecarRequest(settings.Value.MaaEndpoint, Convert.ToBase64String(runtimeData.Span));
        using var response = await client.PostAsJsonAsync(
            new Uri(settings.Value.SidecarUrl, "attest/maa"), request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SidecarResponse>(cancellationToken);
        if (string.IsNullOrEmpty(body?.Token))
        {
            throw new InvalidOperationException("Attestation sidecar returned no token.");
        }

        return body.Token;
    }

    private sealed record SidecarRequest(
        [property: JsonPropertyName("maa_endpoint")] string MaaEndpoint,
        [property: JsonPropertyName("runtime_data")] string RuntimeData);

    private sealed record SidecarResponse([property: JsonPropertyName("token")] string? Token);
}
