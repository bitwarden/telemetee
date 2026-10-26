using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Parquet.Serialization;
using Xunit.Sdk;

namespace Bit.Telemetee.Telemetry.IntegrationTest;

/// <summary>
/// Compares the Parquet byte stream produced by <c>BlobStorageSink</c> against a committed text
/// golden under <c>Golden/</c>. Two files are checked per schema:
/// <list type="bullet">
///   <item><c>&lt;schema&gt;.schema.txt</c> — one column per line: name, CLR type, nullability, in file order.</item>
///   <item><c>&lt;schema&gt;.rows.txt</c> — pipe-delimited values, one row per line, columns in file order.</item>
/// </list>
///
/// Raw Parquet bytes are not deterministic (writer version string and footer UUIDs vary across
/// runs), so the goldens are text projections of the schema + data that Data Services actually
/// consumes. Set <c>TELEMETEE_UPDATE_GOLDENS=1</c> to overwrite them.
/// </summary>
internal static class GoldenAssertions
{
    private const string UpdateEnvVar = "TELEMETEE_UPDATE_GOLDENS";

    private const string ContractBanner =
        """
        # PARQUET CONTRACT — Data Services
        # This file documents the exact Parquet shape telemetee emits for this schema.
        # A diff here means the on-disk layout has changed. Before updating this file,
        # notify the Data Services team — their ingestion jobs read this exact layout.
        # Regenerate: TELEMETEE_UPDATE_GOLDENS=1 dotnet test
        # See docs/batching-and-sink.md § Parquet contract.
        """;

    public static async Task AssertMatchesGoldenAsync(
        byte[] parquetBytes,
        string schemaName,
        [CallerFilePath] string callerFilePath = "")
    {
        var result = await DeserializeAsync(parquetBytes);
        var schemaText = DumpSchema(result);
        var rowsText = DumpRows(result);

        AssertOrUpdate($"{schemaName}.schema.txt", schemaText, callerFilePath);
        AssertOrUpdate($"{schemaName}.rows.txt", rowsText, callerFilePath);
    }

    private static async Task<DeserializationResult<Dictionary<string, object>>> DeserializeAsync(byte[] parquetBytes)
    {
        using var stream = new MemoryStream(parquetBytes, writable: false);
        return await ParquetSerializer.DeserializeUntypedAsync(stream);
    }

    private static string DumpSchema(DeserializationResult<Dictionary<string, object>> result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# column | clr_type | nullable");
        foreach (var field in result.Schema.DataFields)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{field.Name} | {field.ClrType.FullName} | {field.IsNullable}");
        }
        return sb.ToString();
    }

    private static string DumpRows(DeserializationResult<Dictionary<string, object>> result)
    {
        var fields = result.Schema.DataFields;
        var sb = new StringBuilder();
        sb.AppendLine("# " + string.Join(" | ", fields.Select(f => f.Name)));
        foreach (var row in result.Data)
        {
            var cells = fields.Select(f => FormatCell(row.TryGetValue(f.Name, out var v) ? v : null));
            sb.AppendLine(string.Join(" | ", cells));
        }
        return sb.ToString();
    }

    private static void AssertOrUpdate(string filename, string actualBody, string callerFilePath)
    {
        // Read and write against the source Golden/ in-repo rather than a bin/ copy. One source of
        // truth means a regenerate pass does not leave a stale copy next to the test binary that
        // would fail a subsequent --no-build run.
        var sourcePath = ResolveSourcePath(filename, callerFilePath);
        var actual = $"{ContractBanner}\n\n{actualBody.TrimEnd()}\n";

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));
            File.WriteAllText(sourcePath, actual);
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new XunitException(
                $"""
                Golden file not found: {filename}
                Expected at: {sourcePath}

                If this is a new schema, generate the golden with:
                  {UpdateEnvVar}=1 dotnet test

                Then review the file (it is a contract with Data Services) and commit it.
                """);
        }

        var expected = File.ReadAllText(sourcePath).ReplaceLineEndings("\n");
        actual = actual.ReplaceLineEndings("\n");
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new XunitException(
                $"""
                Parquet contract drift detected in {filename}.
                The on-disk Parquet layout no longer matches the committed golden.

                BEFORE updating the golden:
                  1. Confirm the change is intentional.
                  2. Notify the Data Services team — their ingestion jobs read this exact layout.
                  3. Regenerate with: {UpdateEnvVar}=1 dotnet test
                  4. Review the diff on the golden file in review.

                Golden at: {sourcePath}

                {ShortDiff(expected, actual)}
                """);
        }
    }

    /// <summary>
    /// Line-by-line diff of two normalized-newline strings. Golden files are long (schema dumps
    /// are one line per column) and a full expected/actual dump drowns the real information, so
    /// this returns only the differing lines with their line numbers, capped at five entries, and
    /// truncates individual lines over 200 characters.
    /// </summary>
    private static string ShortDiff(string expected, string actual, int maxDiffsShown = 5)
    {
        var expLines = expected.Split('\n');
        var actLines = actual.Split('\n');
        var max = Math.Max(expLines.Length, actLines.Length);
        var diffs = new List<(int LineNum, string Expected, string Actual)>();
        for (var i = 0; i < max; i++)
        {
            var e = i < expLines.Length ? expLines[i] : "<missing>";
            var a = i < actLines.Length ? actLines[i] : "<missing>";
            if (!string.Equals(e, a, StringComparison.Ordinal))
            {
                diffs.Add((i + 1, e, a));
            }
        }
        if (diffs.Count == 0)
        {
            return "(line-by-line comparison found no differing lines — likely a trailing-newline or whitespace mismatch; diff the golden against the regenerated file.)";
        }

        var shown = Math.Min(diffs.Count, maxDiffsShown);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{diffs.Count} line(s) differ. Showing first {shown}:");
        foreach (var (lineNum, e, a) in diffs.Take(shown))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  line {lineNum}:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    expected: {Truncate(e)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    actual:   {Truncate(a)}");
        }
        if (diffs.Count > shown)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {diffs.Count - shown} more");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : $"{s[..max]}… (+{s.Length - max} chars)";

    private static string ResolveSourcePath(string filename, string callerFilePath)
    {
        // Walk up from the caller's source file until we find a .csproj; Golden/ sits beside it.
        // Keeps resolution stable even if a test is nested under Sink/, Fixtures/, etc.
        var dir = Path.GetDirectoryName(callerFilePath);
        while (dir is not null)
        {
            if (Directory.EnumerateFiles(dir, "*.csproj").Any())
            {
                return Path.Combine(dir, "Golden", filename);
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Could not resolve project directory from caller file path '{callerFilePath}'.");
    }

    private static string FormatCell(object? value) => value switch
    {
        null => "<null>",
        bool b => b ? "true" : "false",
        // Parquet.Net hands back DateTime with Kind=Unspecified for columns written with
        // isAdjustedToUtc=true, so dump the wall-clock value verbatim rather than reinterpret it.
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<null>",
    };
}
