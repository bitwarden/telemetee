using System.Net.Http.Json;
using System.Text.Json;
using Bit.Telemetee.Telemetry.Schemas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bit.Telemetee.Telemetry.Sink;

/// <summary>
/// Posts a flushed batch to the Datadog logs intake. The sink is outside the privacy trust boundary;
/// anonymization is the responsibility of the schema layer that runs before this.
/// </summary>
internal sealed class DatadogSink : ITelemetrySink
{
    public const string HttpClientName = "DatadogSink";

    private readonly IHttpClientFactory _httpFactory;
    private readonly TelemetrySettings _settings;
    private readonly ILogger<DatadogSink> _logger;

    public DatadogSink(IHttpClientFactory httpFactory, IOptions<TelemetrySettings> settings, ILogger<DatadogSink> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendAsync(
        IReadOnlyList<AnonymizedRecord> records,
        DateTimeOffset batchTime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_settings.DatadogApiKey))
        {
            _logger.LogWarning("Datadog API key is not set; dropping batch of {Count} records.", records.Count);
            return false;
        }

        var body = records
            .Select(record => new Dictionary<string, object?>
            {
                ["ddsource"] = _settings.DatadogSource,
                ["service"] = _settings.DatadogService,
                ["timestamp"] = batchTime.ToUnixTimeMilliseconds(),
                ["schema"] = record.Schema,
                ["fields"] = record.Fields,
            })
            .ToArray();

        var url = $"https://http-intake.logs.{_settings.DatadogSite}/api/v2/logs";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };
        request.Headers.Add("DD-API-KEY", _settings.DatadogApiKey);

        try
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Datadog intake returned {Status}; dropping batch of {Count} records.",
                    (int)response.StatusCode, records.Count);
                return false;
            }
            return true;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Datadog intake post failed; dropping batch of {Count} records.", records.Count);
            return false;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
}
