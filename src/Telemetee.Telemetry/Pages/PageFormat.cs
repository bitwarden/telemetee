using Bit.Telemetee.NoiseChannel.Protocol;

namespace Bit.Telemetee.Telemetry.Pages;

/// <summary>
/// Fixed layout for one telemetry page. Every <c>POST /telemetry</c> body is exactly one page, so the
/// edge sees constant request and response sizes regardless of what the page contains.
/// </summary>
/// <remarks>
/// Plaintext layout inside one Noise envelope:
/// <c>[uint16-BE body_length] [CBOR body] [0x00 padding to PlaintextLength]</c>.
///
/// The CBOR body is a single map:
/// <code>
/// {
///   "authed":   [{ "auth": "&lt;jwt&gt;", "events": [&lt;cbor-event&gt;, ...] }, ...],
///   "unauthed": [&lt;cbor-event&gt;, ...]
/// }
/// </code>
/// Each <c>cbor-event</c> is a CBOR map with a <c>schema</c> discriminator and the schema's fields.
/// Grouping authed events under their JWT keeps the token overhead out of each record. Unauthed
/// records go in the sibling list with no token at all.
/// </remarks>
public static class PageFormat
{
    /// <summary>One page on the wire, including NK message 1 overhead.</summary>
    public const int PageLength = 4096;

    /// <summary>
    /// Plaintext bytes inside one page after NK message 1 overhead
    /// (ephemeral public key + AEAD tag).
    /// </summary>
    public const int PlaintextLength =
        PageLength - NoiseProtocol.PublicKeyLength - NoiseProtocol.TagLength;

    /// <summary>
    /// Length of one NK message 2 with an empty payload: ephemeral public key plus AEAD tag.
    /// </summary>
    public const int ResponseLength = NoiseProtocol.PublicKeyLength + NoiseProtocol.TagLength;

    /// <summary>
    /// Per-event ceiling for the client-side packer. Not enforced on the server, which only caps
    /// the whole body via <see cref="MaxBodyLength"/>; clients drop oversize events before they
    /// reach the wire.
    /// </summary>
    public const int MaxRecordLength = 1024;

    /// <summary>Bytes used by the length prefix in front of the CBOR body.</summary>
    public const int BodyLengthPrefixBytes = 2;

    /// <summary>Maximum length of the CBOR body (what fits in the plaintext region after the prefix).</summary>
    public const int MaxBodyLength = PlaintextLength - BodyLengthPrefixBytes;
}
