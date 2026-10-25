using XEngine.Ai.Server;
using XEngine.Editor.Core;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineEditorLogTests", Guid.NewGuid().ToString("N"), "中文 工程");

    public EditorLogTests()
    {
        EditorLog.Shutdown();
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void MarkerFileStartup_WritesProjectLog_AndRotatesOnRestart()
    {
        string marker = Path.Combine(_root, "中文 工程.xengine");
        File.WriteAllText(marker, "{}");
        string log = Path.Combine(_root, "Logs", "Editor.log");

        EditorLog.Initialize(marker);
        Debug.Log("first-session-marker");
        Assert.Equal(log, EditorLog.LogPath);
        Assert.Contains("first-session-marker", File.ReadAllText(log));
        EditorLog.Shutdown();

        EditorLog.Initialize(marker);
        Debug.Log("second-session-marker");
        Assert.Contains("first-session-marker", File.ReadAllText(Path.Combine(_root, "Logs", "Editor-prev.log")));
        Assert.DoesNotContain("first-session-marker", File.ReadAllText(log));
        Assert.Contains("second-session-marker", File.ReadAllText(log));
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("warning")]
    [InlineData("WARN")]
    public void WarningFilter_ExcludesInfo_AndIncludesErrors(string level)
    {
        EditorLog.Initialize(_root);
        Debug.Log("filter-info-marker");
        Debug.LogWarning("filter-warning-marker");
        Debug.LogError("filter-error-marker");

        var (_, _, lines) = EditorLog.GetRecent(200, level);
        Assert.Contains(lines, line => line.Contains("filter-warning-marker"));
        Assert.Contains(lines, line => line.Contains("filter-error-marker"));
        Assert.DoesNotContain(lines, line => line.Contains("filter-info-marker"));
    }

    [Fact]
    public void RuntimeLogs_PreservesSeverityMessageAndEventTimestamp()
    {
        EditorLog.Initialize(_root);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        Debug.LogWarning("structured-event-marker");
        DateTimeOffset after = DateTimeOffset.UtcNow;

        var page = EditorRuntimeTools.GetLogs(new MainThreadDispatcher(), 200, "Warning").GetAwaiter().GetResult();
        var entry = Assert.Single(page.Logs, item => item.Message == "structured-event-marker");
        Assert.Equal("Warning", entry.Severity);
        Assert.InRange(entry.TimestampUtc, before, after);
    }

    [Theory]
    [InlineData("Exception")]
    [InlineData("exception")]
    public void ExceptionFilter_ExcludesErrors_AndPreservesStackContext(string level)
    {
        EditorLog.Initialize(_root);
        Debug.LogError("excluded-error-marker");
        try { throw new InvalidOperationException("included-exception-marker"); }
        catch (InvalidOperationException exception) { Debug.LogException(exception); }

        var page = EditorRuntimeTools.GetLogs(new MainThreadDispatcher(), 200, level).GetAwaiter().GetResult();

        var entry = Assert.Single(page.Logs);
        Assert.Equal("Exception", entry.Severity);
        Assert.Contains("included-exception-marker", entry.Message);
        Assert.Contains(nameof(ExceptionFilter_ExcludesErrors_AndPreservesStackContext), entry.StackTrace);
    }

    [Fact]
    public void FileAtLogsDirectory_DisablesFileSink_ButRetainsRuntimeLogs()
    {
        File.WriteAllText(Path.Combine(_root, "Logs"), "blocked-directory");
        EditorLog.Initialize(_root);
        Debug.LogWarning("ring-without-file-marker");

        Assert.Null(EditorLog.LogPath);
        var page = EditorRuntimeTools.GetLogs(new MainThreadDispatcher(), 200, "Warning").GetAwaiter().GetResult();
        var entry = Assert.Single(page.Logs);
        Assert.Equal("ring-without-file-marker", entry.Message);
        Assert.Equal("Warning", entry.Severity);
    }

    public void Dispose()
    {
        EditorLog.Shutdown();
        Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }
}
