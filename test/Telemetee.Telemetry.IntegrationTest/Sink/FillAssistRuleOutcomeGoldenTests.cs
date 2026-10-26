using Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Sink;

[Collection(AzuriteCollection.Name)]
public class FillAssistRuleOutcomeGoldenTests(AzuriteContainerFixture azurite)
    : ParquetGoldenTestsBase(azurite)
{
    [Fact]
    public Task RoundTripsThroughParquet() => AssertGoldenAsync(
        schemaName: "fill_assist_rule_outcome",
        records:
        [
            Stamp(new FillAssistRuleOutcome.Sink
            {
                RuleId = "r-fill-login",
                Success = true,
                Domain = "example.com",
                ClientVersionMajor = "2025",
            }, seq: 0),
            Stamp(new FillAssistRuleOutcome.Sink
            {
                RuleId = "r-fill-card",
                Success = false,
                Domain = "shop.example",
                ClientVersionMajor = "2026+",
            }, seq: 1),
        ]);
}
