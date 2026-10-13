namespace Bit.Telemetee.Attestation;

/// <summary>Obtains a MAA token whose <c>x-ms-runtime</c> claim carries the given JSON runtime data.</summary>
internal interface IAttestationTokenSource
{
    Task<string> GetTokenAsync(ReadOnlyMemory<byte> runtimeData, CancellationToken cancellationToken);
}
