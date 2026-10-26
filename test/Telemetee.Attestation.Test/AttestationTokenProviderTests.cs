using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Bit.Telemetee.NoiseChannel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bit.Telemetee.Attestation.Test;

public class AttestationTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTokenSource(params string[] tokens) : IAttestationTokenSource
    {
        private readonly Queue<string> _tokens = new(tokens);

        public int Calls { get; private set; }
        public byte[]? LastRuntimeData { get; private set; }

        public Task<string> GetTokenAsync(ReadOnlyMemory<byte> runtimeData, CancellationToken cancellationToken)
        {
            Calls++;
            LastRuntimeData = runtimeData.ToArray();
            return Task.FromResult(_tokens.Dequeue());
        }
    }

    private readonly FakeTimeProvider _time = new(Now);
    private readonly IMeterFactory _meterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();
    private readonly NoiseStaticKey _staticKey = new();

    private AttestationTokenProvider CreateProvider(FakeTokenSource source) => new(
        source,
        Options.Create(new AttestationSettings { RefreshMargin = TimeSpan.FromMinutes(30) }),
        _time,
        new AttestationMetrics(_meterFactory, "test"));

    [Fact]
    public async Task GetTokenAsync_SendsScopedPublicKeyAsRuntimeData()
    {
        var source = new FakeTokenSource(FakeJwt(Now.AddHours(8)));
        using var provider = CreateProvider(source);
        using var scope = _staticKey.AcquireScoped();

        await provider.GetTokenAsync(scope, CancellationToken.None);

        using var runtimeData = JsonDocument.Parse(source.LastRuntimeData!);
        Assert.Equal(
            Convert.ToBase64String(scope.PublicKey.Span),
            runtimeData.RootElement.GetProperty("noise_pub").GetString());
    }

    [Fact]
    public async Task GetTokenAsync_CachesUntilRefreshMargin()
    {
        var source = new FakeTokenSource(FakeJwt(Now.AddHours(8)), FakeJwt(Now.AddHours(16)));
        using var provider = CreateProvider(source);
        using var scope = _staticKey.AcquireScoped();

        var first = await provider.GetTokenAsync(scope, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(7).Add(TimeSpan.FromMinutes(29)));
        var second = await provider.GetTokenAsync(scope, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        var third = await provider.GetTokenAsync(scope, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task GetTokenAsync_RefreshesEagerly_WhenCallerPresentsRotatedKey()
    {
        var source = new FakeTokenSource(FakeJwt(Now.AddHours(8)), FakeJwt(Now.AddHours(16)));
        using var provider = CreateProvider(source);

        string first;
        using (var before = _staticKey.AcquireScoped())
        {
            first = await provider.GetTokenAsync(before, CancellationToken.None);
        }

        _time.Advance(TimeSpan.FromMinutes(10));
        _staticKey.Rotate();

        using var after = _staticKey.AcquireScoped();
        var second = await provider.GetTokenAsync(after, CancellationToken.None);

        Assert.Equal(2, source.Calls);
        Assert.NotEqual(first, second);
        using var runtimeData = JsonDocument.Parse(source.LastRuntimeData!);
        Assert.Equal(
            Convert.ToBase64String(after.PublicKey.Span),
            runtimeData.RootElement.GetProperty("noise_pub").GetString());
    }

    [Fact]
    public async Task GetTokenAsync_TokenWithoutExp_Throws()
    {
        using var provider = CreateProvider(new FakeTokenSource(Jwt("{}")));
        using var scope = _staticKey.AcquireScoped();

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(scope, CancellationToken.None));
    }

    [Fact]
    public async Task GetTokenAsync_CountsRefreshOutcomes()
    {
        using var collector = new MetricCollector<long>(_meterFactory, AttestationMetrics.MeterName, "telemetee.attestation.refreshes");
        using var provider = CreateProvider(new FakeTokenSource(FakeJwt(Now.AddHours(8)), Jwt("{}")));
        using var scope = _staticKey.AcquireScoped();

        await provider.GetTokenAsync(scope, CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(8));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(scope, CancellationToken.None));

        var outcomes = collector.GetMeasurementSnapshot().Select(m => (m.Tags["outcome"], m.Value)).ToList();
        Assert.Equal([("ok", 1L), ("error", 1L)], outcomes);
    }

    private static string FakeJwt(DateTimeOffset expiry) => Jwt($$"""{"exp":{{expiry.ToUnixTimeSeconds()}}}""");

    private static string Jwt(string payload)
    {
        var header = Base64Url.EncodeToString("""{"alg":"none"}"""u8);
        return $"{header}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}.";
    }
}
