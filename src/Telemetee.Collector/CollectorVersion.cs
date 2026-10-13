using System.Reflection;

namespace Bit.Telemetee.Collector;

/// <summary>The exact collector version, used as the URL path segment clients route on.</summary>
internal static class CollectorVersion
{
    public static readonly string Current = typeof(CollectorVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("Assembly has no informational version.");
}
