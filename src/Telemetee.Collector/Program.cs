using Bit.Telemetee.Attestation;
using Bit.Telemetee.Attestation.Development;
using Bit.Telemetee.Collector;
using Bit.Telemetee.NoiseChannel;

var builder = WebApplication.CreateBuilder(args);
builder.UseBitwardenSdk();

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddCollectorMetrics(builder.Configuration, builder.Environment);

builder.Services.Configure<AttestationSettings>(builder.Configuration.GetSection("Attestation"));
var useDevelopmentIssuer = builder.Configuration.GetValue<bool>("Attestation:UseDevelopmentIssuer");
if (useDevelopmentIssuer)
{
    builder.Services.Configure<DevelopmentAttestationSettings>(builder.Configuration.GetSection("Attestation:Development"));
    builder.Services.AddDevelopmentAttestation();
}

builder.Services.AddNoiseChannel();
builder.Services.AddAttestation();

var app = builder.Build();

app.UseSecurityHeaders();
app.MapVersionEndpoint();
app.MapHealthChecks("/healthz");
app.MapOpenApi();

var collector = app.MapGroup($"/{CollectorVersion.Current}");
collector.MapGroup("/attestation").MapAttestationEndpoints();
collector.MapNoiseChannelEndpoints();
if (useDevelopmentIssuer)
{
    collector.MapGroup("/dev-attestation").MapDevelopmentAttestationIssuerEndpoints();
}

app.Run();

namespace Bit.Telemetee.Collector
{
    /// <summary>Entry point marker for integration tests.</summary>
    public partial class Program;
}
