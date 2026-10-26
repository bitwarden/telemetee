using Bit.Telemetee.Telemetry.IntegrationTest.Fixtures;
using Bit.Telemetee.Telemetry.TelemetryData;
using Xunit;

namespace Bit.Telemetee.Telemetry.IntegrationTest.Sink;

[Collection(AzuriteCollection.Name)]
public class AuthedGeneratedPasswordGoldenTests(AzuriteContainerFixture azurite)
    : ParquetGoldenTestsBase(azurite)
{
    [Fact]
    public Task RoundTripsThroughParquet() => AssertGoldenAsync(
        schemaName: "authed_generated_password",
        records:
        [
            Stamp(new AuthedGeneratedPassword.Sink
            {
                UserId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                Kind = "password",
                Length = "16-19",
                WordCount = "0",
                HasLowercase = true,
                HasUppercase = true,
                HasDigits = true,
                HasSpecial = true,
                Accepted = true,
            }, seq: 0),
        ]);
}
