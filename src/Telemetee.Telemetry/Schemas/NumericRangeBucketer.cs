namespace Bit.Telemetee.Telemetry.Schemas;

/// <summary>
/// Classifies a numeric value of type <typeparamref name="T"/> into the first
/// <see cref="NumericRange"/> whose half-open interval contains it, emitting the range's label.
/// Returns null when no range matches. Ranges are declared as <see cref="double"/> so one bucketer
/// definition can serve any <see cref="IConvertible"/> numeric input: the classifier converts
/// <paramref name="value"/> to <see cref="double"/> before comparing.
/// </summary>
public sealed class NumericRangeBucketer<T> : IBucketer<T>
    where T : IConvertible
{
    private readonly IReadOnlyList<NumericRange> _ranges;

    public NumericRangeBucketer(params NumericRange[] ranges)
    {
        _ranges = ranges;
    }

    public string? Classify(T value)
    {
        var number = value.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
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
