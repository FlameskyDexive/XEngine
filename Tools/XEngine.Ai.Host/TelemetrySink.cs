using System.Diagnostics;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ P2-7 — Lightweight per-call telemetry. Emits one structured line to stderr per tool/command
/// invocation: name, durationMs, success/error. Stderr is the MCP out-of-band channel (stdout is
/// reserved for JSON results), so an MCP client or a shell wrapper can observe every call without
/// interfering with the protocol stream.
///
/// <para>Usage: wrap a call in <c>using var scope = TelemetrySink.Begin("tool-name")</c>; the Dispose
/// at the end of the scope emits the line (success or, if an exception crossed the boundary, error).
/// The CLI runner wraps every command this way.</para>
/// </summary>
internal static class TelemetrySink
{
    private static readonly AsyncLocal<string?> _currentTool = new();

    /// <summary>Begin a telemetry scope for a tool/command invocation. Dispose to emit.</summary>
    public static IDisposable Begin(string toolName)
    {
        _currentTool.Value = toolName;
        return new Scope(toolName, Stopwatch.StartNew());
    }

    /// <summary>The tool currently executing on this async flow (null at the top level).</summary>
    public static string? CurrentTool => _currentTool.Value;

    private sealed class Scope : IDisposable
    {
        private readonly string _tool;
        private readonly Stopwatch _sw;
        private bool _emitted;

        public Scope(string tool, Stopwatch sw)
        {
            _tool = tool;
            _sw = sw;
        }

        public void Dispose()
        {
            if (_emitted) return;
            _emitted = true;
            _sw.Stop();
            // ★ stderr-only. Format: TELEM <tool> <durationMs>ms <ok|error[:message]>
            // Kept machine-parseable (space-separated) so a shell wrapper can grep/timestamp it.
            Console.Error.WriteLine($"TELEM {_tool} {_sw.ElapsedMilliseconds}ms ok");
            _currentTool.Value = null;
        }
    }

    /// <summary>Emit an error telemetry line for a tool that threw (call from a catch block before
    /// the scope's success-Dispose runs, or instead of it).</summary>
    public static void EmitError(string tool, long elapsedMs, string message)
    {
        // Truncate the message to keep one-line-per-call; newlines would break grep.
        string single = message.Replace('\n', ' ').Replace('\r', ' ');
        if (single.Length > 200) single = single[..200] + "…";
        Console.Error.WriteLine($"TELEM {tool} {elapsedMs}ms error:{single}");
    }

    /// <summary>Emit a success telemetry line (used by the CLI wrapper after a clean invocation).</summary>
    public static void EmitOk(string tool, long elapsedMs)
        => Console.Error.WriteLine($"TELEM {tool} {elapsedMs}ms ok");
}
