using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;

namespace XHub.Services;

public enum SessionState
{
    Launching,
    Ready,
    LockConflict,
    Failed,
    /// <summary>Legacy editor: process alive past the grace window, but open success can never be confirmed.</summary>
    LaunchedUnconfirmed,
    Closed,
}

/// <summary>One parsed protocol event (§4.3 NDJSON envelope).</summary>
public sealed record SessionEvent(
    int ProtocolVersion,
    string SessionId,
    int Pid,
    string Type,
    string Code,
    JsonElement Data);

/// <summary>
/// Watches one editor session: tails the NDJSON events file and the process liveness, raising
/// transitions on a callback. Hub-aware sessions confirm success only via the editor's
/// <c>projectReady</c> event (§4.4 — process start is never success); legacy sessions get the
/// unconfirmed state after the grace window.
/// </summary>
public sealed class HubSessionWatcher : IDisposable
{
    private const int PollIntervalMs = 300;
    private const int LegacyGraceMs = 12_000;

    private readonly EditorSession _session;
    private readonly Action<SessionState, SessionEvent?, string?> _onTransition;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _fileOffset;
    private SessionState _state = SessionState.Launching;

    public HubSessionWatcher(EditorSession session, Action<SessionState, SessionEvent?, string?> onTransition)
    {
        _session = session;
        _onTransition = onTransition;
        _loop = Task.Run(() => RunLoop(_cts.Token));
    }

    public SessionState State => _state;
    public EditorSession Session => _session;

    private async Task RunLoop(CancellationToken ct)
    {
        Process? process = null;
        try { process = Process.GetProcessById(_session.ProcessId); } catch { }
        int? pid = process?.Id;

        while (!ct.IsCancellationRequested)
        {
            // 1. New events (hub-aware sessions only; legacy never writes the file).
            if (_session.HubAware && File.Exists(_session.EventsFilePath))
            {
                try
                {
                    using var stream = new FileStream(_session.EventsFilePath, FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(_fileOffset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        _fileOffset = stream.Position;
                        HandleLine(line.TrimEnd('\r', '\n'));
                        if (_state is SessionState.Ready or SessionState.LockConflict or SessionState.Failed)
                        {
                            // Terminal for open-feedback purposes; keep tailing until Closed.
                        }
                    }
                }
                catch (IOException)
                {
                    // writer lock contention — retry next tick
                }
            }

            // 2. Process liveness.
            if (process != null)
            {
                try
                {
                    bool alive = !process.HasExited;
                    // PID-reuse guard (§4.4): a recycled PID is not "our" editor.
                    if (alive && pid != null && TryGetStartTime(process) != _session.ProcessStartTime)
                        alive = false;
                    if (!alive)
                    {
                        Transition(SessionState.Closed, null, null);
                        return;
                    }
                }
                catch
                {
                    Transition(SessionState.Closed, null, null);
                    return;
                }
            }

            // 3. Legacy grace window: no protocol, no confirmation possible.
            if (!_session.HubAware && _clock.ElapsedMilliseconds > LegacyGraceMs && _state == SessionState.Launching)
                Transition(SessionState.LaunchedUnconfirmed, null, null);

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    private void HandleLine(string line)
    {
        if (line.Length == 0) return;
        SessionEvent? evt = ParseEvent(line);
        if (evt == null) return;
        if (evt.ProtocolVersion > HubProtocol.CurrentVersion)
            HubLog.Warning($"Session {_session.SessionId}: event from newer protocol {evt.ProtocolVersion}");

        switch (evt.Type)
        {
            case "projectReady":
                Transition(SessionState.Ready, evt, null);
                break;
            case "lockConflict":
                Transition(SessionState.LockConflict, evt, evt.Code);
                break;
            case "openFailed":
                if (_state != SessionState.LockConflict) // lockConflict already reported the cause
                    Transition(SessionState.Failed, evt, evt.Code);
                break;
            case "closing":
                // A ready session closing is normal teardown; a failed one stays reported.
                if (_state == SessionState.Ready || _state == SessionState.LaunchedUnconfirmed)
                    Transition(SessionState.Closed, evt, null);
                break;
        }
    }

    /// <summary>Pure NDJSON envelope parser (unit-tested).</summary>
    public static SessionEvent? ParseEvent(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeEl) || typeEl.ValueKind != JsonValueKind.String)
                return null;
            return new SessionEvent(
                root.TryGetProperty("protocolVersion", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0,
                root.TryGetProperty("sessionId", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "",
                root.TryGetProperty("pid", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                typeEl.GetString() ?? "",
                root.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "",
                root.TryGetProperty("data", out JsonElement d) ? d.Clone() : default);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Transition(SessionState state, SessionEvent? evt, string? code)
    {
        if (_state == state) return;
        _state = state;
        _onTransition(state, evt, code);
    }

    private static DateTime? TryGetStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(500); } catch { }
        _cts.Dispose();
    }
}
