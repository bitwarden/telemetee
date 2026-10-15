using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bit.Telemetee.Collector;
using Bit.Telemetee.NoiseChannel.Protocol;
using Bit.Telemetee.Telemetry;
using Bit.Telemetee.Telemetry.Batch;
using Bit.Telemetee.Telemetry.Pages;
using Bit.Telemetee.Telemetry.Schemas;
using Bit.Telemetee.Telemetry.Sink;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Bit.Telemetee.Collector.IntegrationTest;

public class CollectorEndpointsTests : IClassFixture<CollectorEndpointsTests.Fixture>
{
    private const string CollectorVersion = "0.1.0";

    private readonly HttpClient _client;
    private readonly IServiceProvider _services;
    private readonly CapturingSink _sink;

    public CollectorEndpointsTests(Fixture fixture)
    {
        _client = fixture.Client;
        _services = fixture.Services;
        _sink = fixture.Sink;
        _sink.Reset();
        _services.GetRequiredService<BatchBuffer>().Drain();
    }

    [Fact]
    public async Task GetAttestation_ReturnsTokenCarryingNoisePublicKey()
    {
        var token = await GetTokenAsync();
        var payload = DecodeSegment(token, 1);

        var noisePub = Convert.FromBase64String(
            payload.GetProperty("x-ms-runtime").GetProperty("noise_pub").GetString()!);
        Assert.Equal(NoiseProtocol.PublicKeyLength, noisePub.Length);
        Assert.Equal("sevsnpvm", payload.GetProperty("x-ms-attestation-type").GetString());
    }

    [Fact]
    public async Task Telemetry_RejectsBodyNotEqualToPageSize_WithoutRunningHandshake()
    {
        using var collector = CountingPages();

        using var response = await PostTelemetryAsync(new byte[PageFormat.PageLength - 1]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var outcomes = collector.GetMeasurementSnapshot().Select(m => (string?)m.Tags["outcome"]).ToList();
        Assert.Equal(["rejected_size"], outcomes);
    }

    [Fact]
    public async Task Telemetry_RejectsBodyThatIsNotExactlyOnePage()
    {
        using var response = await PostTelemetryAsync(new byte[PageFormat.PageLength * 2]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Telemetry_RejectsMissingTargetVersionHeader_BeforeReadingBody()
    {
        using var collector = CountingPages();

        using var response = await _client.PostAsync(
            "/telemetry", new ByteArrayContent(new byte[PageFormat.PageLength]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var outcomes = collector.GetMeasurementSnapshot().Select(m => (string?)m.Tags["outcome"]).ToList();
        Assert.Equal(["rejected_version"], outcomes);
    }

    [Fact]
    public async Task Telemetry_RejectsMismatchedTargetVersionHeader_BeforeReadingBody()
    {
        using var collector = CountingPages();

        using var response = await PostTelemetryAsync(
            new byte[PageFormat.PageLength], targetVersion: "9.9.9");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var outcomes = collector.GetMeasurementSnapshot().Select(m => (string?)m.Tags["outcome"]).ToList();
        Assert.Equal(["rejected_version"], outcomes);
    }

    [Fact]
    public async Task Telemetry_ReturnsConstantSizeReply_WhateverTheOutcome()
    {
        var staticKey = await GetCollectorStaticKeyAsync();
        var pages = new[]
        {
            BuildPage(staticKey, Record("fill_assist_rule_outcome", AcceptedFields)),
            BuildPage(staticKey, Record("not_a_schema", new Dictionary<string, object?> { ["foo"] = "bar" })),
            BuildPage(
                staticKey,
                Record("fill_assist_rule_outcome", AcceptedFields),
                Record("fill_assist_rule_outcome", new Dictionary<string, object?> { ["not_declared"] = "x" })),
        };

        foreach (var page in pages)
        {
            using var response = await PostTelemetryAsync(page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(PageFormat.ResponseLength, body.Length);
        }
    }

    [Fact]
    public async Task Telemetry_HandshakeFailure_StillReturnsResponseLength()
    {
        var garbage = new byte[PageFormat.PageLength];
        Random.Shared.NextBytes(garbage);

        using var response = await PostTelemetryAsync(garbage);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PageFormat.ResponseLength, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task DevelopmentTelemetry_AcceptsFlatRecord_AndReturnsAnonymizedOutput()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "fill_assist_rule_outcome",
            ["rule_id"] = "r1",
            ["success"] = true,
            ["domain"] = "a.example",
            ["client_version_major"] = 2025,
        };

        using var response = await _client.PostAsJsonAsync("/dev/telemetry", record);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fill_assist_rule_outcome", body.GetProperty("schema").GetString());
        var fields = body.GetProperty("fields");
        Assert.Equal("r1", fields.GetProperty("rule_id").GetString());
        Assert.Equal("2025", fields.GetProperty("client_version_major").GetString());
    }

    [Fact]
    public async Task DevelopmentTelemetry_AcceptsGeneratedPassword_BucketsLengthAndWordCount()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "generated_password",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        };

        using var response = await _client.PostAsJsonAsync("/dev/telemetry", record);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("generated_password", body.GetProperty("schema").GetString());
        var fields = body.GetProperty("fields");
        Assert.Equal("password", fields.GetProperty("kind").GetString());
        Assert.Equal("12-15", fields.GetProperty("length").GetString());
        Assert.Equal("0", fields.GetProperty("word_count").GetString());
        Assert.True(fields.GetProperty("has_lowercase").GetBoolean());
        Assert.True(fields.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task DevelopmentTelemetry_UnknownSchemaDiscriminator_Returns400()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "not_a_schema",
            ["foo"] = "bar",
        };

        using var response = await _client.PostAsJsonAsync("/dev/telemetry", record);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentTelemetryAuthed_VerifiesUserIdAgainstJwtSub_AndReturnsPseudonymizedOutput()
    {
        const string sub = "user-abc";
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "authed_generated_password",
            ["user_id"] = sub,
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        };

        using var response = await PostAuthedDevTelemetryAsync(record, BuildUnsignedJwt(sub));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authed_generated_password", body.GetProperty("schema").GetString());
        var fields = body.GetProperty("fields");
        var userId = fields.GetProperty("user_id").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", userId);
        Assert.NotEqual(sub, userId);
        Assert.Equal("12-15", fields.GetProperty("length").GetString());
    }

    [Fact]
    public async Task DevelopmentTelemetryAuthed_RejectsRecordWhoseUserIdDoesNotMatchJwtSub()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "authed_generated_password",
            ["user_id"] = "someone-else",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        };

        using var response = await PostAuthedDevTelemetryAsync(record, BuildUnsignedJwt(sub: "user-abc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ClaimMismatch", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DevelopmentTelemetryAuthed_WithoutBearerToken_Returns400()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "authed_generated_password",
            ["user_id"] = "user-abc",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        };

        using var response = await _client.PostAsJsonAsync("/dev/telemetry/authed", record);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("missing_bearer_token", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DevelopmentTelemetryAuthed_WithMalformedJwt_Returns400()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "authed_generated_password",
            ["user_id"] = "user-abc",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        };

        using var response = await PostAuthedDevTelemetryAsync(record, "not-a-jwt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("auth_malformed", body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DevelopmentTelemetryAuthed_UnauthedSchema_Returns400_WithWrongAuthScope()
    {
        var record = new Dictionary<string, object?>
        {
            ["schema"] = "generated_password",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        };

        using var response = await PostAuthedDevTelemetryAsync(record, BuildUnsignedJwt(sub: "user-abc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("WrongAuthScope", body.GetProperty("reason").GetString());
    }

    private Task<HttpResponseMessage> PostAuthedDevTelemetryAsync(
        IReadOnlyDictionary<string, object?> record,
        string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/dev/telemetry/authed")
        {
            Content = JsonContent.Create(record),
        };
        request.Headers.Add("Authorization", $"Bearer {bearerToken}");
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Telemetry_AuthedGroup_PseudonymizesUserIdAfterVerifyingAgainstJwtSub_AndFlowsToSink()
    {
        var staticKey = await GetCollectorStaticKeyAsync();
        const string sub = "user-abc";
        var jwt = BuildUnsignedJwt(sub);

        var authedRecord = Record("authed_generated_password", new Dictionary<string, object?>
        {
            ["user_id"] = sub,
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        });
        var page = BuildPage(
            staticKey,
            [new AuthedGroupJson(jwt, [authedRecord])],
            []);

        using var response = await PostTelemetryAsync(page);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var flusher = _services.GetServices<IHostedService>().OfType<BatchFlusher>().Single();
        await flusher.FlushOnceAsync(CancellationToken.None);

        var batch = Assert.Single(_sink.Batches);
        var record = Assert.Single(batch.Records);
        Assert.Equal("authed_generated_password", record.Schema);

        var userId = Assert.IsType<string>(record.Fields["user_id"]);
        Assert.Matches("^[0-9a-f]{64}$", userId);
        Assert.NotEqual(sub, userId);

        Assert.Equal("12-15", record.Fields["length"]);
        Assert.Equal("password", record.Fields["kind"]);
    }

    [Fact]
    public async Task Telemetry_AuthedGroup_RejectsRecordWhoseUserIdDoesNotMatchJwtSub()
    {
        var staticKey = await GetCollectorStaticKeyAsync();
        var jwt = BuildUnsignedJwt(sub: "user-abc");

        var authedRecord = Record("authed_generated_password", new Dictionary<string, object?>
        {
            ["user_id"] = "someone-else",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        });
        var page = BuildPage(
            staticKey,
            [new AuthedGroupJson(jwt, [authedRecord])],
            []);

        using var droppedCounter = new MetricCollector<long>(
            _services.GetRequiredService<IMeterFactory>(),
            TelemetryMetrics.MeterName,
            "telemetee.batch.records_dropped");

        using var response = await PostTelemetryAsync(page);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reasons = droppedCounter.GetMeasurementSnapshot().Select(m => (string?)m.Tags["reason"]).ToList();
        Assert.Contains("claim_mismatch", reasons);

        var flusher = _services.GetServices<IHostedService>().OfType<BatchFlusher>().Single();
        await flusher.FlushOnceAsync(CancellationToken.None);
        Assert.Empty(_sink.Batches);
    }

    [Fact]
    public async Task Telemetry_AuthedSchemaInUnauthedGroup_IsRejected_AsWrongAuthScope()
    {
        var staticKey = await GetCollectorStaticKeyAsync();
        var misplaced = Record("authed_generated_password", new Dictionary<string, object?>
        {
            ["user_id"] = "ignored",
            ["kind"] = "password",
            ["length"] = 14,
            ["word_count"] = 0,
            ["has_lowercase"] = true,
            ["has_uppercase"] = true,
            ["has_digits"] = true,
            ["has_special"] = false,
            ["accepted"] = true,
        });
        var page = BuildPage(staticKey, [], [misplaced]);

        using var droppedCounter = new MetricCollector<long>(
            _services.GetRequiredService<IMeterFactory>(),
            TelemetryMetrics.MeterName,
            "telemetee.batch.records_dropped");

        using var response = await PostTelemetryAsync(page);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var reasons = droppedCounter.GetMeasurementSnapshot().Select(m => (string?)m.Tags["reason"]).ToList();
        Assert.Contains("wrong_auth_scope", reasons);

        var flusher = _services.GetServices<IHostedService>().OfType<BatchFlusher>().Single();
        await flusher.FlushOnceAsync(CancellationToken.None);
        Assert.Empty(_sink.Batches);
    }

    /// <summary>
    /// Builds an unsigned JWT: header + claims payload + empty signature. The Phase-A collector
    /// parses but does not verify tokens, so an unsigned token is enough to exercise the authed
    /// path end-to-end. Phase B replaces this with a dev-signed issuer parallel to dev-attestation.
    /// </summary>
    private static string BuildUnsignedJwt(string sub)
    {
        var header = Base64Url.EncodeToString("""{"alg":"none","typ":"JWT"}"""u8);
        var payload = Base64Url.EncodeToString(
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["sub"] = sub }));
        return $"{header}.{payload}.";
    }

    [Fact]
    public async Task Telemetry_AcceptedRecord_FlowsThroughToSink()
    {
        var staticKey = await GetCollectorStaticKeyAsync();
        var page = BuildPage(staticKey, Record("fill_assist_rule_outcome", AcceptedFields));

        using var response = await PostTelemetryAsync(page);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var flusher = _services.GetServices<IHostedService>().OfType<BatchFlusher>().Single();
        await flusher.FlushOnceAsync(CancellationToken.None);

        var batch = Assert.Single(_sink.Batches);
        var record = Assert.Single(batch.Records);
        Assert.Equal("fill_assist_rule_outcome", record.Schema);
        Assert.Equal(true, record.Fields["success"]);
        Assert.Equal("r1", record.Fields["rule_id"]);
        Assert.Equal("a.example", record.Fields["domain"]);
        Assert.Equal("2025", record.Fields["client_version_major"]);
    }

    private async Task<string> GetTokenAsync()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/attestation");
        return body.GetProperty("token").GetString()!;
    }

    private Task<HttpResponseMessage> PostTelemetryAsync(byte[] body, string? targetVersion = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/telemetry")
        {
            Content = new ByteArrayContent(body),
        };
        request.Headers.Add(TelemetryEndpointsExtensions.TargetVersionHeader, targetVersion ?? CollectorVersion);
        return _client.SendAsync(request);
    }

    private async Task<byte[]> GetCollectorStaticKeyAsync()
    {
        var token = await GetTokenAsync();
        var payload = DecodeSegment(token, 1);
        return Convert.FromBase64String(payload.GetProperty("x-ms-runtime").GetProperty("noise_pub").GetString()!);
    }

    private static JsonElement DecodeSegment(string token, int index) =>
        JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[index])).RootElement;

    private MetricCollector<long> CountingPages() => new(
        _services.GetRequiredService<IMeterFactory>(), TelemetryMetrics.MeterName, "telemetee.pages");

    private static readonly Dictionary<string, object?> AcceptedFields = new()
    {
        ["rule_id"] = "r1",
        ["success"] = true,
        ["domain"] = "a.example",
        ["client_version_major"] = 2025,
    };

    private static RecordJson Record(string schema, IReadOnlyDictionary<string, object?> fields) =>
        new(schema, fields);

    private static byte[] BuildPage(byte[] staticKey, params RecordJson[] records) =>
        BuildPage(staticKey, [], records);

    private static byte[] BuildPage(
        byte[] staticKey,
        IReadOnlyList<AuthedGroupJson> authedGroups,
        IReadOnlyList<RecordJson> unauthedRecords)
    {
        var cborWriter = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Lax);
        cborWriter.WriteStartMap(2);
        cborWriter.WriteTextString("authed");
        cborWriter.WriteStartArray(authedGroups.Count);
        foreach (var group in authedGroups)
        {
            cborWriter.WriteStartMap(2);
            cborWriter.WriteTextString("auth");
            cborWriter.WriteTextString(group.Token);
            cborWriter.WriteTextString("events");
            cborWriter.WriteStartArray(group.Events.Count);
            foreach (var record in group.Events)
            {
                WriteEventCbor(cborWriter, record);
            }
            cborWriter.WriteEndArray();
            cborWriter.WriteEndMap();
        }
        cborWriter.WriteEndArray();
        cborWriter.WriteTextString("unauthed");
        cborWriter.WriteStartArray(unauthedRecords.Count);
        foreach (var record in unauthedRecords)
        {
            WriteEventCbor(cborWriter, record);
        }
        cborWriter.WriteEndArray();
        cborWriter.WriteEndMap();

        var body = cborWriter.Encode();
        var plaintext = new byte[PageFormat.PlaintextLength];
        BinaryPrimitives.WriteUInt16BigEndian(plaintext.AsSpan(0), (ushort)body.Length);
        body.CopyTo(plaintext.AsSpan(PageFormat.BodyLengthPrefixBytes));

        using var initiator = new NoiseNkInitiator(staticKey);
        return initiator.WriteMessage(plaintext);
    }

    private static void WriteEventCbor(System.Formats.Cbor.CborWriter writer, RecordJson record)
    {
        writer.WriteStartMap(record.Fields.Count + 1);
        writer.WriteTextString("schema");
        writer.WriteTextString(record.Schema);
        foreach (var (name, value) in record.Fields)
        {
            writer.WriteTextString(name);
            switch (value)
            {
                case string s: writer.WriteTextString(s); break;
                case bool b: writer.WriteBoolean(b); break;
                case int i: writer.WriteInt32(i); break;
                case long l: writer.WriteInt64(l); break;
                case double d: writer.WriteDouble(d); break;
                case null: writer.WriteNull(); break;
                default: writer.WriteTextString(value.ToString() ?? ""); break;
            }
        }
        writer.WriteEndMap();
    }

    private sealed record RecordJson(string Schema, IReadOnlyDictionary<string, object?> Fields);

    private sealed record AuthedGroupJson(string Token, IReadOnlyList<RecordJson> Events);

    public sealed class Fixture : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        public HttpClient Client { get; }
        public IServiceProvider Services { get; }
        public CapturingSink Sink { get; }

        public Fixture()
        {
            Sink = new CapturingSink();
            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<ITelemetrySink>();
                        services.AddSingleton<ITelemetrySink>(Sink);
                    });
                });
            Client = _factory.CreateClient();
            Services = _factory.Services;
        }

        public void Dispose() => _factory.Dispose();
    }

    public sealed class CapturingSink : ITelemetrySink
    {
        private readonly List<CapturedBatch> _batches = [];

        public IReadOnlyList<CapturedBatch> Batches
        {
            get
            {
                lock (_batches)
                {
                    return [.. _batches];
                }
            }
        }

        public void Reset()
        {
            lock (_batches)
            {
                _batches.Clear();
            }
        }

        public Task<bool> SendAsync(
            IReadOnlyList<AnonymizedRecord> records,
            DateTimeOffset batchTime,
            CancellationToken cancellationToken)
        {
            lock (_batches)
            {
                _batches.Add(new CapturedBatch(batchTime, [.. records]));
            }
            return Task.FromResult(true);
        }
    }

    public sealed record CapturedBatch(DateTimeOffset Time, IReadOnlyList<AnonymizedRecord> Records);
}
