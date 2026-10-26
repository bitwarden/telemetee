using System.ComponentModel.DataAnnotations;

namespace Bit.Telemetee.NoiseChannel;

/// <summary>
/// Operator-tunable knobs for the Noise channel. The code default is dev-only; production values
/// come from the ARM template.
/// </summary>
public sealed class NoiseChannelSettings
{
    /// <summary>
    /// How often the collector replaces its Noise static key with a freshly generated one. The
    /// attested public key stays valid for exactly this long, so this is the exposure window for
    /// any single key. The floor keeps rotation from flooding the attestation sidecar with
    /// re-attestation requests; the ceiling keeps the exposure window bounded even when an
    /// operator misconfigures it.
    /// </summary>
    [Range(typeof(TimeSpan), "00:15:00", "7.00:00:00")]
    public TimeSpan KeyRotationInterval { get; set; } = TimeSpan.FromHours(24);
}
