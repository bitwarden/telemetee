namespace Bit.Telemetee.Collector;

/// <summary>
/// Refuses to start if the .NET diagnostic subsystem could expose live process memory. See
/// <c>docs/decisions.md</c> § "Why .NET diagnostics are off in Production" for the threat
/// model this closes.
/// </summary>
internal static class DiagnosticsLockdown
{
    private static readonly string[] RequiredDisabledVars =
    [
        "DOTNET_EnableDiagnostics",
        "DOTNET_EnableDiagnostics_IPC",
        "DOTNET_EnableDiagnostics_Debugger",
        "DOTNET_EnableDiagnostics_Profiler",
    ];

    public static void AssertDisabled(ILogger logger)
    {
        var problems = Collect();
        if (problems.Count == 0)
        {
            return;
        }

        foreach (var problem in problems)
        {
            logger.LogCritical("Diagnostics lockdown violated: {Problem}", problem);
        }
        throw new InvalidOperationException(
            "Refusing to start: .NET diagnostic endpoints could expose in-memory secrets. "
            + string.Join(" ", problems));
    }

    internal static List<string> Collect()
    {
        var problems = new List<string>();

        foreach (var name in RequiredDisabledVars)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value != "0")
            {
                problems.Add($"{name} must be \"0\" at process start (observed \"{value ?? "<unset>"}\").");
            }
        }

        var diagnosticPorts = Environment.GetEnvironmentVariable("DOTNET_DiagnosticPorts");
        if (!string.IsNullOrEmpty(diagnosticPorts))
        {
            problems.Add($"DOTNET_DiagnosticPorts must be unset (observed \"{diagnosticPorts}\").");
        }

        var tmp = Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
        if (Directory.Exists(tmp))
        {
            var pattern = $"dotnet-diagnostic-{Environment.ProcessId}-*";
            foreach (var entry in Directory.EnumerateFileSystemEntries(tmp, pattern))
            {
                problems.Add($"diagnostic endpoint reachable at {entry}.");
            }
        }

        return problems;
    }
}
