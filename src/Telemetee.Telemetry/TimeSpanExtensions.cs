namespace Bit.Telemetee.Telemetry;

public static class TimeSpanExtensions
{
    extension(TimeSpan)
    {
        /// <summary>
        /// Returns a <see cref="TimeSpan"/> representing the given number of whole years,
        /// defined as 365 days each. Actual years are strictly longer than 365 days, so
        /// the generated TimeSpan spans <i>at most</i> parameter input <paramref name="years"/>.
        /// </summary>
        public static TimeSpan FromYears(int years) => TimeSpan.FromDays(years * 365);
    }
}
