using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.TelemetryData;

/// <summary>
/// Same event as <see cref="GeneratedPassword"/>, but reported from an authenticated session so
/// the <c>user_id</c> dimension is available at research time. The client sends <see cref="UserId"/>
/// as the plaintext id the record is about; the collector rejects the record if that value does
/// not equal the <c>sub</c> claim on the enclosing authed group's JWT, and otherwise
/// HMAC-pseudonymizes it under the per-boot salt before anything reaches the sink.
/// </summary>
[Telemetry("authed_generated_password")]
public sealed class AuthedGeneratedPassword : TelemetryRecord
{
    /// <summary>
    /// Plaintext user id the client claims this record belongs to. Verified by the collector
    /// against the JWT's <c>sub</c> claim; the output on the wire is its HMAC-pseudonym.
    /// </summary>
    [Authed("sub")]
    public string UserId { get; init; } = string.Empty;

    /// <summary>"password" (random character) or "passphrase" (word-list).</summary>
    [Keep]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Length in characters, bucketed.</summary>
    [Bucket(nameof(LengthBuckets))]
    public int Length { get; init; }

    /// <summary>Word count when kind=passphrase, zero otherwise, bucketed.</summary>
    [Bucket(nameof(WordCountBuckets))]
    public int WordCount { get; init; }

    /// <summary>Whether the lowercase character class was enabled.</summary>
    [Keep]
    public bool HasLowercase { get; init; }

    /// <summary>Whether the uppercase character class was enabled.</summary>
    [Keep]
    public bool HasUppercase { get; init; }

    /// <summary>Whether the digit character class was enabled.</summary>
    [Keep]
    public bool HasDigits { get; init; }

    /// <summary>Whether the special-character class was enabled.</summary>
    [Keep]
    public bool HasSpecial { get; init; }

    /// <summary>Whether the user accepted the generated value (copied, filled, or saved it).</summary>
    [Keep]
    public bool Accepted { get; init; }

    public static readonly IBucketer<int> LengthBuckets = GeneratedPassword.LengthBuckets;
    public static readonly IBucketer<int> WordCountBuckets = GeneratedPassword.WordCountBuckets;

    /// <summary>
    /// Sink shape for <see cref="AuthedGeneratedPassword"/>. The <c>user_id</c> field is a hex
    /// HMAC pseudonym rather than the raw id; the rest mirror <see cref="GeneratedPassword.Sink"/>.
    /// </summary>
    public sealed class Sink : TelemetrySinkRecord, IRetained
    {
        public static TimeSpan RetentionTime => TimeSpan.FromYears(3);

        public required string UserId { get; init; }
        public required string Kind { get; init; }
        public required string Length { get; init; }
        public required string WordCount { get; init; }
        public required bool HasLowercase { get; init; }
        public required bool HasUppercase { get; init; }
        public required bool HasDigits { get; init; }
        public required bool HasSpecial { get; init; }
        public required bool Accepted { get; init; }
    }
}
