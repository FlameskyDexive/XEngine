namespace XEngine.Ai.Protocol;

/// <summary>★ Layer 1 (B2) — Result of spawning a build or headless run.</summary>
public sealed record ProcessRunResult(
    string Command,
    bool Succeeded,
    int ExitCode,
    long DurationMs,
    string? OutputPath,
    /// <summary>Truncated stdout/stderr tail (last ~4 KiB) for diagnostics.</summary>
    string OutputTail);

/// <summary>★ Result of run_tests: parsed xUnit counts + the raw output tail.</summary>
public sealed record TestRunResult(
    string Target,
    bool Succeeded,
    int ExitCode,
    long DurationMs,
    /// <summary>xUnit "Passed: N" count (parsed from output; -1 when unparseable).</summary>
    int Passed,
    /// <summary>xUnit "Failed: N" count.</summary>
    int Failed,
    /// <summary>xUnit "Skipped: N" count.</summary>
    int Skipped,
    /// <summary>xUnit "Total: N" count.</summary>
    int Total,
    /// <summary>Truncated output tail (last ~4 KiB).</summary>
    string OutputTail);
