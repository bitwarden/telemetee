namespace Bit.Telemetee.Attestation.Development;

internal sealed class DevelopmentAttestationTokenSource(DevelopmentAttestationIssuer issuer) : IAttestationTokenSource
{
    public Task<string> GetTokenAsync(ReadOnlyMemory<byte> runtimeData, CancellationToken cancellationToken) =>
        Task.FromResult(issuer.Issue(runtimeData.Span));
}
