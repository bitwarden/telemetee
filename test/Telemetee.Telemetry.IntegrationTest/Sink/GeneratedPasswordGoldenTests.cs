using Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Sink;

[Collection(AzuriteCollection.Name)]
public class GeneratedPasswordGoldenTests(AzuriteContainerFixture azurite)
    : ParquetGoldenTestsBase(azurite)
{
    [Fact]
    public Task RoundTripsThroughParquet() => AssertGoldenAsync(
        schemaName: "generated_password",
        records:
        [
            Stamp(new GeneratedPassword.Sink
            {
                Kind = "password",
                Length = "12-15",
                WordCount = "0",
                HasLowercase = true,
                HasUppercase = true,
                HasDigits = true,
                HasSpecial = false,
                Accepted = true,
            }, seq: 0),
            Stamp(new GeneratedPassword.Sink
            {
                Kind = "passphrase",
                Length = "20-31",
                WordCount = "4-6",
                HasLowercase = true,
                HasUppercase = false,
                HasDigits = false,
                HasSpecial = false,
                Accepted = false,
            }, seq: 1),
        ]);
}
