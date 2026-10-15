using System.Text.Json;

namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Classifies a JSON number into the first <see cref="NumericRange"/> whose half-open interval
/// contains it. Returns null if the value is not a number or no range matches.
/// </summary>
public sealed class NumericRangeBucketer : IBucketer
{
    private readonly IReadOnlyList<NumericRange> _ranges;

    public NumericRangeBucketer(params NumericRange[] ranges)
    {
        _ranges = ranges;
    }

    public string? Classify(JsonElement value)
    {
        if (!value.TryGetDouble(out var number))
        {
            return null;
        }
        foreach (var range in _ranges)
        {
            var aboveMin = range.MinInclusive is null || number >= range.MinInclusive.Value;
            var belowMax = range.MaxExclusive is null || number < range.MaxExclusive.Value;
            if (aboveMin && belowMax)
            {
                return range.Label;
            }
        }
        return null;
    }
}

/// <summary>One half-open numeric range with the label emitted when a value falls into it.</summary>
/// <param name="Label">The label emitted when a value falls in this range.</param>
/// <param name="MinInclusive">Lower bound; null means unbounded below.</param>
/// <param name="MaxExclusive">Upper bound; null means unbounded above.</param>
public sealed record NumericRange(string Label, double? MinInclusive, double? MaxExclusive);
