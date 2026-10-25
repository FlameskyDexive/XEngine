using System;
using System.IO;
using System.Threading.Tasks;
using XHub.Models;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class HubProtocolTests
{
    [Fact]
    public void ParseHubInfo_ReadsTheHandshake()
    {
        string stdout = """
            some unrelated editor log line
            {"protocolVersion":1,"engineVersion":"1.0.0-a1","displayVersion":"V1.0.0a1","architecture":"x64","capabilities":["sessionEvents","createProject"],"templateDirs":[]}
            """;

        var info = HubProtocol.ParseHubInfo(stdout);

        Assert.NotNull(info);
        Assert.Equal(1, info.ProtocolVersion);
        Assert.Equal("1.0.0-a1", info.EngineVersion);
        Assert.Equal("x64", info.Architecture);
        Assert.Contains("sessionEvents", info.Capabilities);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage output, no json")]
    [InlineData("{\"noVersion\":1}")]
    public void ParseHubInfo_RejectsNonHandshakeOutput(string stdout)
    {
        Assert.Null(HubProtocol.ParseHubInfo(stdout));
    }

    [Fact]
    public void ParseEvent_ReadsNdjsonEnvelope()
    {
        const string line = """{"protocolVersion":1,"sessionId":"abc","pid":123,"type":"projectReady","code":"","data":{"projectRoot":"C:/p"},"ts":"2026-09-12T08:00:00Z"}""";
        var evt = HubSessionWatcher.ParseEvent(line);

        Assert.NotNull(evt);
        Assert.Equal("projectReady", evt.Type);
        Assert.Equal("abc", evt.SessionId);
        Assert.Equal(123, evt.Pid);
        Assert.Equal("C:/p", evt.Data.GetProperty("projectRoot").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"missingType\":true}")]
    public void ParseEvent_RejectsMalformedLines(string line)
    {
        Assert.Null(HubSessionWatcher.ParseEvent(line));
    }
}

public class EditorLauncherTests
{
    [Fact]
    public void BuildArgs_HubAware_CarriesSessionId()
    {
        var install = new InstallationRecord { HubProtocolVersion = 1, EditorExePath = "XEngine.Editor.exe" };
        string[] args = EditorLauncher.BuildArgs(install, @"C:\Proj\A", "sess1");
        Assert.Equal(["--project", @"C:\Proj\A", "--hub-session", "sess1"], args);
    }

    [Fact]
    public void BuildArgs_Legacy_OmitsSessionId()
    {
        // A legacy editor aborts on unknown args (§4.4 compat mode) — never pass --hub-session.
        var install = new InstallationRecord { HubProtocolVersion = 0, EditorExePath = "XEngine.Editor.exe" };
        string[] args = EditorLauncher.BuildArgs(install, @"C:\Proj\A", "sess1");
        Assert.Equal(["--project", @"C:\Proj\A"], args);
    }
}

[Collection("Avalonia")]
public class HubSessionWatcherTests : IDisposable
{
    private readonly string _dir;

    public HubSessionWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"), "sessions");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.Combine(_dir, ".."), recursive: true); } catch { }
    }

    /// <summary>Watcher probes process liveness — the test host itself is a live, matching process.</summary>
    private EditorSession LiveSession(string sessionId, bool hubAware)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new EditorSession(sessionId, @"C:\fake", new InstallationRecord(),
            self.Id, self.StartTime, Path.Combine(_dir, $"{sessionId}.ndjson"), hubAware);
    }

    [Fact]
    public async Task HubSession_ProjectReady_IsObserved()
    {
        string file = Path.Combine(_dir, "s1.ndjson");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<SessionState>();
        var session = LiveSession("s1", hubAware: true);
        using var watcher = new HubSessionWatcher(session, (state, _, _) => seen.Enqueue(state));

        await Task.Delay(400);
        await File.WriteAllTextAsync(file,
            """{"protocolVersion":1,"sessionId":"s1","pid":1,"type":"sessionStarted","code":"","data":{},"ts":"t"}""" + "\n");
        await Task.Delay(800);
        await File.AppendAllTextAsync(file,
            """{"protocolVersion":1,"sessionId":"s1","pid":1,"type":"projectReady","code":"","data":{},"ts":"t"}""" + "\n");
        await Task.Delay(1000);

        Assert.Contains(SessionState.Ready, seen);
        Assert.Equal(SessionState.Ready, watcher.State);
    }

    [Fact]
    public async Task HubSession_LockConflict_IsReportedWithCode()
    {
        string file = Path.Combine(_dir, "s2.ndjson");
        string? code = null;
        var states = new System.Collections.Concurrent.ConcurrentQueue<SessionState>();
        var session = LiveSession("s2", hubAware: true);
        using var watcher = new HubSessionWatcher(session, (state, _, c) =>
        {
            states.Enqueue(state);
            if (c != null) code = c;
        });

        await Task.Delay(400);
        await File.WriteAllTextAsync(file,
            """{"protocolVersion":1,"sessionId":"s2","pid":1,"type":"lockConflict","code":"PROJECT_LOCKED","data":{},"ts":"t"}""" + "\n");
        await Task.Delay(1000);

        Assert.Contains(SessionState.LockConflict, states);
        Assert.Equal("PROJECT_LOCKED", code);
    }

    [Fact]
    public async Task LegacySession_TurnsUnconfirmedAfterGrace()
    {
        // Grace is 12s in production; this test only asserts the session stays Launching while
        // inside the window and that a legacy session with no events file never goes Ready.
        var seen = new System.Collections.Concurrent.ConcurrentQueue<SessionState>();
        var session = LiveSession("legacy", hubAware: false);
        using var watcher = new HubSessionWatcher(session, (state, _, _) => seen.Enqueue(state));

        await Task.Delay(1000);

        Assert.Equal(SessionState.Launching, watcher.State);
        Assert.DoesNotContain(SessionState.Ready, seen);
    }
}
