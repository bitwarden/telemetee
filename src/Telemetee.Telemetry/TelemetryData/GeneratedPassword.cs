using Bit.Telemetee.Telemetry.Schemas;

namespace Bit.Telemetee.Telemetry.TelemetryData;

/// <summary>
/// A client ran the password generator and either used or dismissed the result. Aggregating these
/// tells product which generator defaults users actually accept versus override, and how often each
/// character class / length range is produced in practice. Everything that could identify a specific
/// output is either bucketed or dropped: no character counts beyond coarse ranges, no sample of the
/// generated text, no entropy figure fine enough to narrow a single credential.
/// </summary>
[Telemetry("generated_password")]
public sealed class GeneratedPassword : TelemetryRecord
{
    /// <summary>"password" (random character) or "passphrase" (word-list). Kept so the two flows can be ranked.</summary>
    [Keep]
    public string Kind { get; init; } = string.Empty;

    /// <summary>Length in characters. Bucketed so an exact length can never fingerprint the record.</summary>
    [Bucket(nameof(LengthBuckets))]
    public int Length { get; init; }

    /// <summary>Word count when kind=passphrase, zero otherwise. Bucketed for the same reason as Length.</summary>
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

    public static readonly IBucketer<int> LengthBuckets = new NumericRangeBucketer<int>(
        new NumericRange("<8", null, 8),
        new NumericRange("8-11", 8, 12),
        new NumericRange("12-15", 12, 16),
        new NumericRange("16-19", 16, 20),
        new NumericRange("20-31", 20, 32),
        new NumericRange("32+", 32, null));

    public static readonly IBucketer<int> WordCountBuckets = new NumericRangeBucketer<int>(
        new NumericRange("0", null, 1),
        new NumericRange("1-3", 1, 4),
        new NumericRange("4-6", 4, 7),
        new NumericRange("7-9", 7, 10),
        new NumericRange("10+", 10, null));

    /// <summary>
    /// Sink shape for <see cref="GeneratedPassword"/>. Fields reflect the post-treatment types:
    /// bucketed numeric inputs become their range labels, kept fields keep their native type.
    /// </summary>
    public sealed class Sink : TelemetrySinkRecord, IRetained
    {
        public static TimeSpan RetentionTime => TimeSpan.FromYears(3);

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
