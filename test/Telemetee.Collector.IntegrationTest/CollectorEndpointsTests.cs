using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bit.Telemetee.Collector;
using Bit.Telemetee.NoiseChannel;
using Bit.Telemetee.NoiseChannel.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Xunit;

namespace Bit.Telemetee.Collector.IntegrationTest;

public class CollectorEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Prefix = "/0.1.0";

    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public CollectorEndpointsTests(WebApplicationFactory<Program> factory)
    {
        var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        _client = development.CreateClient();
        _services = development.Services;
    }

    [Fact]
    public async Task GetAttestation_ReturnsTokenCarryingNoisePublicKey()
    {
        var token = await GetTokenAsync();

        var payload = DecodeSegment(token, 1);
        var noisePub = Convert.FromBase64String(payload.GetProperty("x-ms-runtime").GetProperty("noise_pub").GetString()!);
        Assert.Equal(NoiseProtocol.PublicKeyLength, noisePub.Length);
        Assert.Equal("sevsnpvm", payload.GetProperty("x-ms-attestation-type").GetString());
    }

    [Fact]
    public async Task GetAttestation_KeyIdMatchesPublishedCerts()
    {
        var token = await GetTokenAsync();
        var header = DecodeSegment(token, 0);

        var jwks = await _client.GetFromJsonAsync<JsonElement>($"{Prefix}/dev-attestation/certs");

        Assert.Equal(jwks.GetProperty("keys")[0].GetProperty("kid").GetString(), header.GetProperty("kid").GetString());
    }

    [Fact]
    public async Task Echo_WithKeyFromToken_RoundTrips()
    {
        var token = await GetTokenAsync();
        var noisePub = Convert.FromBase64String(
            DecodeSegment(token, 1).GetProperty("x-ms-runtime").GetProperty("noise_pub").GetString()!);
        using var initiator = new NoiseNkInitiator(noisePub);

        using var response = await _client.PostAsync(
            $"{Prefix}/echo", new ByteArrayContent(initiator.WriteMessage("hello"u8)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = initiator.ReadMessage(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("hello"u8.ToArray(), echoed);
    }

    [Fact]
    public async Task Echo_WithWrongKey_IsRejected()
    {
        using var wrongStatic = KeyPair.Generate();
        using var initiator = new NoiseNkInitiator(wrongStatic.PublicKey);

        using var response = await _client.PostAsync(
            $"{Prefix}/echo", new ByteArrayContent(initiator.WriteMessage("hello"u8)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Echo_WithGarbage_IsRejected()
    {
        using var response = await _client.PostAsync($"{Prefix}/echo", new ByteArrayContent(new byte[10]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Echo_CountsOutcomes()
    {
        using var collector = new MetricCollector<long>(
            _services.GetRequiredService<IMeterFactory>(), NoiseChannelMetrics.MeterName, "telemetee.pages");
        var token = await GetTokenAsync();
        var noisePub = Convert.FromBase64String(
            DecodeSegment(token, 1).GetProperty("x-ms-runtime").GetProperty("noise_pub").GetString()!);
        using var initiator = new NoiseNkInitiator(noisePub);

        await _client.PostAsync($"{Prefix}/echo", new ByteArrayContent(initiator.WriteMessage("hello"u8)));
        await _client.PostAsync($"{Prefix}/echo", new ByteArrayContent(new byte[10]));
        await _client.PostAsync($"{Prefix}/echo", new ByteArrayContent(new byte[NoiseProtocol.MaxMessageLength + 1]));

        var outcomes = collector.GetMeasurementSnapshot().Select(m => (string?)m.Tags["outcome"]).ToList();
        Assert.Equal(["accepted", "rejected_noise", "rejected_size"], outcomes);
    }

    private async Task<string> GetTokenAsync()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>($"{Prefix}/attestation");
        return body.GetProperty("token").GetString()!;
    }

    private static JsonElement DecodeSegment(string token, int index) =>
        JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[index])).RootElement;
}
