using Xunit;

namespace Bit.Telemetee.Collector.IntegrationTest;

public class DiagnosticsLockdownTests
{
    [Fact]
    public void Collect_ReportsEveryEnvVarNotSetToZero()
    {
        using var env = EnvScope.AllDisabled();
        env.Set("DOTNET_EnableDiagnostics", "1");
        env.Set("DOTNET_EnableDiagnostics_Debugger", null);

        var problems = DiagnosticsLockdown.Collect();

        Assert.Contains(problems, p => p.StartsWith("DOTNET_EnableDiagnostics must"));
        Assert.Contains(problems, p => p.StartsWith("DOTNET_EnableDiagnostics_Debugger must"));
        Assert.DoesNotContain(problems, p => p.StartsWith("DOTNET_EnableDiagnostics_IPC must"));
        Assert.DoesNotContain(problems, p => p.StartsWith("DOTNET_EnableDiagnostics_Profiler must"));
    }

    [Fact]
    public void Collect_ReportsCustomDiagnosticPorts()
    {
        using var env = EnvScope.AllDisabled();
        env.Set("DOTNET_DiagnosticPorts", "/tmp/custom-port");

        var problems = DiagnosticsLockdown.Collect();

        Assert.Contains(problems, p => p.Contains("DOTNET_DiagnosticPorts"));
    }

    [Fact]
    public void Collect_ReportsSocketPresentForThisPid()
    {
        using var env = EnvScope.AllDisabled();
        var fake = Path.Combine(env.TempDir, $"dotnet-diagnostic-{Environment.ProcessId}-faux-socket");
        File.WriteAllBytes(fake, []);

        var problems = DiagnosticsLockdown.Collect();

        Assert.Contains(problems, p => p.Contains(fake));
    }

    [Fact]
    public void Collect_IgnoresSocketsForOtherPids()
    {
        using var env = EnvScope.AllDisabled();
        var otherPid = Environment.ProcessId + 1;
        File.WriteAllBytes(Path.Combine(env.TempDir, $"dotnet-diagnostic-{otherPid}-x-socket"), []);

        Assert.Empty(DiagnosticsLockdown.Collect());
    }

    [Fact]
    public void Collect_IsEmptyWhenEverythingIsLockedDown()
    {
        using var env = EnvScope.AllDisabled();

        Assert.Empty(DiagnosticsLockdown.Collect());
    }

    private sealed class EnvScope : IDisposable
    {
        private static readonly string[] TrackedVars =
        [
            "DOTNET_EnableDiagnostics",
            "DOTNET_EnableDiagnostics_IPC",
            "DOTNET_EnableDiagnostics_Debugger",
            "DOTNET_EnableDiagnostics_Profiler",
            "DOTNET_DiagnosticPorts",
            "TMPDIR",
        ];

        private readonly Dictionary<string, string?> _snapshot = new();

        public string TempDir { get; }

        private EnvScope()
        {
            foreach (var name in TrackedVars)
            {
                _snapshot[name] = Environment.GetEnvironmentVariable(name);
            }
            TempDir = Directory.CreateTempSubdirectory("telemetee-diag-lockdown-").FullName;
        }

        public static EnvScope AllDisabled()
        {
            var scope = new EnvScope();
            scope.Set("DOTNET_EnableDiagnostics", "0");
            scope.Set("DOTNET_EnableDiagnostics_IPC", "0");
            scope.Set("DOTNET_EnableDiagnostics_Debugger", "0");
            scope.Set("DOTNET_EnableDiagnostics_Profiler", "0");
            scope.Set("DOTNET_DiagnosticPorts", null);
            scope.Set("TMPDIR", scope.TempDir);
            return scope;
        }

        public void Set(string name, string? value) =>
            Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach (var (name, value) in _snapshot)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            try { Directory.Delete(TempDir, recursive: true); } catch { }
        }
    }
}
