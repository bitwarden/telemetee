using Bit.Telemetee.Attestation;
using Bit.Telemetee.Attestation.Development;
using Bit.Telemetee.Collector;
using Bit.Telemetee.NoiseChannel;
using Bit.Telemetee.Telemetry;
using Bit.Telemetee.Telemetry.Sink;

var builder = WebApplication.CreateBuilder(args);
builder.UseBitwardenSdk();

builder.Services.AddOpenApi(options => options.UseTelemetryRecordPolymorphism());
builder.Services.AddHealthChecks();
builder.Services.AddCollectorMetrics(builder.Configuration, builder.Environment);

builder.Services.Configure<AttestationSettings>(builder.Configuration.GetSection("Attestation"));
var useDevelopmentIssuer = builder.Configuration.GetValue<bool>("Attestation:UseDevelopmentIssuer");
if (useDevelopmentIssuer)
{
    builder.Services.Configure<DevelopmentAttestationSettings>(builder.Configuration.GetSection("Attestation:Development"));
    builder.Services.AddDevelopmentAttestation();
}

builder.Services.AddOptions<TelemetryIngestionSettings>()
    .Bind(builder.Configuration.GetSection("TelemetryIngestion"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<BlobStorageSinkSettings>()
    .Bind(builder.Configuration.GetSection("BlobStorageSink"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddNoiseChannel();
builder.Services.AddAttestation(CollectorVersion.Current);
builder.Services.AddTelemetry(CollectorVersion.Current);

var app = builder.Build();

app.UseSecurityHeaders();
app.MapVersionEndpoint();
app.MapHealthChecks("/healthz");

app.MapGroup("/attestation").MapAttestationEndpoints();
app.MapTelemetryEndpoints(CollectorVersion.Current);
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapGroup("/dev/telemetry").MapDevelopmentTelemetryEndpoints();
}
if (useDevelopmentIssuer)
{
    app.MapGroup("/dev/attestation").MapDevelopmentAttestationIssuerEndpoints();
}

app.Run();

namespace Bit.Telemetee.Collector
{
    /// <summary>Entry point marker for integration tests.</summary>
    public partial class Program;
}
