using System;
using System.IO;

using XEngine.Editor.Core.BugReport;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Crash records are written on UnhandledException and surfaced once on the next editor start.
/// A crash storm used to leave a queue of <c>crash-*.txt</c> files; each launch prompted about
/// only the newest one, so the Bug Reporter kept appearing on every subsequent open.
/// </summary>
public sealed class CrashHandlerTests : IDisposable
{
    private readonly string _dirA;
    private readonly string _dirB;

    public CrashHandlerTests()
    {
        _dirA = Path.Combine(Path.GetTempPath(), "xengine-crash-a-" + Guid.NewGuid().ToString("N"));
        _dirB = Path.Combine(Path.GetTempPath(), "xengine-crash-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);
    }

    public void Dispose()
    {
        TryDelete(_dirA);
        TryDelete(_dirB);
    }

    [Fact]
    public void FindUnreported_SkipsReportedSuffixEvenWhenGlobMatches()
    {
        string pending = Path.Combine(_dirA, "crash-20260822-014213.txt");
        string reported = Path.Combine(_dirA, "crash-20260822-014213.reported.txt");
        File.WriteAllText(pending, "Time: 1\nTerminating: True\n");
        File.WriteAllText(reported, "Time: 1\nTerminating: True\n");

        string[] found = CrashHandler.FindUnreportedCrashFiles(_dirA);

        Assert.Equal(new[] { pending }, found);
    }

    [Fact]
    public void ConsumeUnreported_MarksEveryPendingFileAndReturnsNewestByFileName()
    {
        // Different directories so a full-path compare would pick the wrong file: dirB's path
        // sorts after dirA, but the older timestamp lives there.
        string older = Path.Combine(_dirB, "crash-20260821-213213.txt");
        string newer = Path.Combine(_dirA, "crash-20260822-005446.txt");
        File.WriteAllText(older, "Time:        2026-08-21 21:32:13 (local)\nTerminating: True\nException:   Old\n");
        File.WriteAllText(newer, "Time:        2026-08-22 00:54:46 (local)\nTerminating: True\nException:   New\n");
        File.WriteAllText(Path.Combine(_dirA, "crash-20260820-000000.reported.txt"), "already surfaced\n");

        bool found = CrashHandler.TryConsumeUnreportedCrashFiles(
            new[] { _dirA, _dirB }, out string? latestReported, out string? summary);

        Assert.True(found);
        Assert.Equal(CrashHandler.ReportedPath(newer), latestReported);
        Assert.Contains("Exception:   New", summary, StringComparison.Ordinal);
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(newer));
        Assert.True(File.Exists(CrashHandler.ReportedPath(older)));
        Assert.True(File.Exists(CrashHandler.ReportedPath(newer)));
        Assert.Empty(CrashHandler.FindUnreportedCrashFiles(_dirA, _dirB));
    }

    [Fact]
    public void ConsumeUnreported_ReturnsFalseWhenNothingPending()
    {
        File.WriteAllText(Path.Combine(_dirA, "crash-20260822-014213.reported.txt"), "done\n");

        bool found = CrashHandler.TryConsumeUnreportedCrashFiles(
            new[] { _dirA }, out string? latestReported, out string? summary);

        Assert.False(found);
        Assert.Null(latestReported);
        Assert.Null(summary);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}
