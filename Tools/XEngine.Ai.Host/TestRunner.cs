using System.Diagnostics;
using System.Text.RegularExpressions;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 — Runs xUnit tests via <c>dotnet test</c>. Wraps the .NET SDK test runner as a
/// structured result (passed/failed/skipped counts parsed from xUnit's summary line, plus the output
/// tail for diagnostics). Synchronous (xUnit tests are typically fast; unlike Unity's async job
/// model, <c>dotnet test</c> runs inline and returns).
///
/// <para>Test discovery: when <c>testProject</c> is a .csproj, runs that project; when it's a .sln,
/// runs the whole solution; when null, auto-discovers the solution at the project root (the same
/// XEngine.sln code_symbols uses).</para>
/// </summary>
internal static class TestRunner
{
    private const int TailBytes = 4 * 1024;
    private static readonly Regex CountLine = new(
        @"^\s*(Passed|Failed|Skipped|Total):\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    // xUnit also prints "Total tests: N" — a separate format when counts are zero (it omits the
    // "Failed: 0" / "Skipped: 0" lines). Catch both so we always surface a total.
    private static readonly Regex TotalTestsLine = new(
        @"^\s*Total tests:\s+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

    public static async Task<TestRunResult> RunAsync(
        string? testProject,
        string? solutionPath,
        string? filter,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // Resolve the target: explicit csproj > explicit sln > auto-discovered sln.
        string target;
        if (!string.IsNullOrEmpty(testProject) && File.Exists(testProject))
        {
            target = testProject;
        }
        else if (!string.IsNullOrEmpty(solutionPath) && File.Exists(solutionPath))
        {
            target = solutionPath;
        }
        else
        {
            throw new FileNotFoundException(
                "No test target found. Pass a .csproj or .sln path, or ensure XEngine.sln is at the project root.");
        }

        var argParts = new List<string> { "test", $"\"{target}\"", "--nologo" };
        if (!string.IsNullOrEmpty(filter))
        {
            argParts.Add($"--filter \"{filter}\"");
        }
        argParts.Add("--verbosity normal");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = string.Join(' ', argParts),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read both streams fully (prevents deadlock when the child fills its pipe).
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        string stdout, stderr;
        try
        {
            stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
            return new TestRunResult(target, false, -1, sw.ElapsedMilliseconds,
                -1, -1, -1, -1, $"Test run timed out after {timeoutSeconds}s and was killed.");
        }
        sw.Stop();

        string combined = stdout + "\n" + stderr;
        string tail = combined.Length > TailBytes ? combined[(combined.Length - TailBytes)..] : combined;

        // Parse the xUnit summary lines: "Passed: 10", "Failed: 0", "Skipped: 0", "Total: 10" (xUnit
        // omits zero-count lines, so derive missing values when we can).
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in CountLine.Matches(combined))
        {
            counts[m.Groups[1].Value] = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        // "Total tests: N" appears even when Failed/Skipped are 0.
        Match totalMatch = TotalTestsLine.Match(combined);
        if (totalMatch.Success && !counts.ContainsKey("Total"))
        {
            counts["Total"] = int.Parse(totalMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        // Derive: if we have Total + Passed and no Failed/Skipped, Failed = Total - Passed - Skipped.
        int passed = counts.GetValueOrDefault("Passed", -1);
        int total = counts.GetValueOrDefault("Total", -1);
        int skipped = counts.GetValueOrDefault("Skipped", -1);
        int failed = counts.GetValueOrDefault("Failed", -1);
        if (failed < 0 && total >= 0 && passed >= 0)
        {
            failed = total - passed - (skipped > 0 ? skipped : 0);
        }
        if (skipped < 0 && total >= 0 && passed >= 0 && failed >= 0)
        {
            skipped = total - passed - failed;
        }

        return new TestRunResult(
            Target: target,
            Succeeded: process.ExitCode == 0,
            ExitCode: process.ExitCode,
            DurationMs: sw.ElapsedMilliseconds,
            Passed: passed,
            Failed: failed < 0 ? 0 : failed,
            Skipped: skipped < 0 ? 0 : skipped,
            Total: total,
            OutputTail: tail.Trim());
    }
}
