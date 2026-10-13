using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bit.Telemetee.Attestation.Test;

public class SidecarAttestationTokenSourceTests
{
    private sealed class CannedHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private static (SidecarAttestationTokenSource Source, CannedHandler Handler) Create(HttpResponseMessage response)
    {
        var handler = new CannedHandler(response);
        var services = new ServiceCollection();
        services.AddHttpClient(SidecarAttestationTokenSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var settings = Options.Create(new AttestationSettings
        {
            SidecarUrl = new Uri("http://sidecar:8080"),
            MaaEndpoint = "sharedeus2.eus2.attest.azure.net",
        });
        return (new SidecarAttestationTokenSource(factory, settings), handler);
    }

    [Fact]
    public async Task GetTokenAsync_PostsRuntimeDataToSidecar()
    {
        var (source, handler) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"token":"a.b.c"}"""),
        });
        var runtimeData = """{"noise_pub":"x"}"""u8.ToArray();

        var token = await source.GetTokenAsync(runtimeData, CancellationToken.None);

        Assert.Equal("a.b.c", token);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://sidecar:8080/attest/maa", handler.Request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("sharedeus2.eus2.attest.azure.net", body.RootElement.GetProperty("maa_endpoint").GetString());
        Assert.Equal(Convert.ToBase64String(runtimeData), body.RootElement.GetProperty("runtime_data").GetString());
    }

    [Fact]
    public async Task GetTokenAsync_SidecarError_Throws()
    {
        var (source, _) = Create(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":"nope"}"""),
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetTokenAsync("{}"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task GetTokenAsync_MissingToken_Throws()
    {
        var (source, _) = Create(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetTokenAsync("{}"u8.ToArray(), CancellationToken.None));
    }
}
