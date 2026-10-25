// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>One external HarmonyOS-tool invocation expressed without a shell command string.</summary>
internal sealed class HarmonyOSProcessCommand
{
    public string ExecutablePath { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public int OutputLimitCharacters { get; init; } = 1024 * 1024;
    public IReadOnlyList<string> Redactions { get; init; } = [];
    public string? CompletionOutputMarker { get; init; }
}

/// <summary>Redacted result of one HarmonyOS external-tool invocation.</summary>
internal sealed record HarmonyOSProcessResult(
    int ExitCode,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError,
    string DisplayCommand,
    bool TimedOut,
    bool OutputWasTruncated,
    bool CompletionMarkerFound = false)
{
    internal string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>Abstraction for externally observable HarmonyOS tool invocations.</summary>
internal interface IHarmonyOSProcessRunner
{
    Task<HarmonyOSProcessResult> RunAsync(HarmonyOSProcessCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts a tool without shell interpolation, drains bounded output, redacts configured secrets, and
/// terminates the complete process tree when its timeout or cancellation token fires.
/// </summary>
internal sealed class HarmonyOSProcessRunner : IHarmonyOSProcessRunner
{
    private const string TruncationMarker = "\n[output truncated]\n";

    public async Task<HarmonyOSProcessResult> RunAsync(
        HarmonyOSProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        cancellationToken.ThrowIfCancellationRequested();

        using var timeoutCancellation = new CancellationTokenSource(command.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using var process = new Process
        {
            StartInfo = CreateStartInfo(command),
        };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start HarmonyOS tool: {command.ExecutablePath}");

        TaskCompletionSource<bool>? markerCompletion = command.CompletionOutputMarker == null
            ? null
            : new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CapturedOutput> standardOutputTask = ReadOutputAsync(
            process.StandardOutput,
            command.OutputLimitCharacters,
            command.CompletionOutputMarker,
            markerCompletion);
        Task<CapturedOutput> standardErrorTask = ReadOutputAsync(
            process.StandardError,
            command.OutputLimitCharacters,
            command.CompletionOutputMarker,
            markerCompletion);
        var stopwatch = Stopwatch.StartNew();
        bool timedOut = false;
        bool cancelledByCaller = false;
        bool completionMarkerFound = false;
        try
        {
            Task processExit = process.WaitForExitAsync(linkedCancellation.Token);
            if (markerCompletion == null)
            {
                await processExit.ConfigureAwait(false);
            }
            else
            {
                Task completed = await Task.WhenAny(processExit, markerCompletion.Task).ConfigureAwait(false);
                if (completed == markerCompletion.Task && await markerCompletion.Task.ConfigureAwait(false))
                {
                    completionMarkerFound = true;
                    TerminateProcessTree(process);
                }
                else
                {
                    await processExit.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (OperationCanceledException)
        {
            cancelledByCaller = true;
        }
        finally
        {
            if (!process.HasExited)
                TerminateProcessTree(process);
            stopwatch.Stop();
        }

        if (!process.HasExited)
            await process.WaitForExitAsync().ConfigureAwait(false);
        CapturedOutput standardOutput = await standardOutputTask.ConfigureAwait(false);
        CapturedOutput standardError = await standardErrorTask.ConfigureAwait(false);
        if (cancelledByCaller)
            throw new OperationCanceledException(cancellationToken);

        return new HarmonyOSProcessResult(
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            Redact(standardOutput.Text, command.Redactions),
            Redact(standardError.Text, command.Redactions),
            Redact(CreateDisplayCommand(command), command.Redactions),
            timedOut,
            standardOutput.WasTruncated || standardError.WasTruncated,
            completionMarkerFound);
    }

    private static void ValidateCommand(HarmonyOSProcessCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkingDirectory);
        if (!Directory.Exists(command.WorkingDirectory))
            throw new DirectoryNotFoundException($"HarmonyOS tool working directory is missing: {command.WorkingDirectory}");
        if (command.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(command.Timeout), "HarmonyOS tool timeout must be positive.");
        if (command.OutputLimitCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.OutputLimitCharacters), "HarmonyOS tool output limit must be positive.");
        if (command.CompletionOutputMarker is { } marker &&
            (string.IsNullOrWhiteSpace(marker) || marker.Contains('\r') || marker.Contains('\n')))
        {
            throw new ArgumentException(
                "HarmonyOS tool completion output marker must be non-empty and single-line.",
                nameof(command.CompletionOutputMarker));
        }
        for (int i = 0; i < command.Arguments.Count; i++)
        {
            if (command.Arguments[i] == null)
                throw new ArgumentException("HarmonyOS tool arguments cannot contain null.", nameof(command.Arguments));
        }
    }

    private static ProcessStartInfo CreateStartInfo(HarmonyOSProcessCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (int i = 0; i < command.Arguments.Count; i++)
            startInfo.ArgumentList.Add(command.Arguments[i]);
        foreach (KeyValuePair<string, string> variable in command.Environment)
            startInfo.Environment[variable.Key] = variable.Value;
        return startInfo;
    }

    private static async Task<CapturedOutput> ReadOutputAsync(
        StreamReader reader,
        int outputLimitCharacters,
        string? completionOutputMarker,
        TaskCompletionSource<bool>? markerCompletion)
    {
        char[] buffer = new char[4096];
        var output = new StringBuilder(Math.Min(outputLimitCharacters, buffer.Length));
        bool truncated = false;
        string markerTail = string.Empty;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
                break;
            if (completionOutputMarker != null && markerCompletion != null && !markerCompletion.Task.IsCompleted)
            {
                string markerWindow = markerTail + new string(buffer, 0, read);
                if (markerWindow.Contains(completionOutputMarker, StringComparison.Ordinal))
                {
                    markerCompletion.TrySetResult(true);
                }
                else
                {
                    int tailLength = Math.Min(completionOutputMarker.Length - 1, markerWindow.Length);
                    markerTail = tailLength == 0 ? string.Empty : markerWindow[^tailLength..];
                }
            }
            int remaining = outputLimitCharacters - output.Length;
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }
        if (truncated)
            output.Append(TruncationMarker);
        return new CapturedOutput(output.ToString(), truncated);
    }

    private static void TerminateProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The operating system could not terminate an already-exited process.
        }
    }

    private static string CreateDisplayCommand(HarmonyOSProcessCommand command)
    {
        var result = new StringBuilder();
        AppendDisplayArgument(result, command.ExecutablePath);
        for (int i = 0; i < command.Arguments.Count; i++)
        {
            result.Append(' ');
            AppendDisplayArgument(result, command.Arguments[i]);
        }
        return result.ToString();
    }

    private static void AppendDisplayArgument(StringBuilder result, string value)
    {
        result.Append('"');
        result.Append(value.Replace("\"", "\\\"", StringComparison.Ordinal));
        result.Append('"');
    }

    private static string Redact(string value, IReadOnlyList<string> redactions)
    {
        var ordered = new List<string>();
        for (int i = 0; i < redactions.Count; i++)
        {
            string redaction = redactions[i];
            if (string.IsNullOrEmpty(redaction))
                continue;
            bool duplicate = false;
            for (int j = 0; j < ordered.Count; j++)
            {
                if (string.Equals(ordered[j], redaction, StringComparison.Ordinal))
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                ordered.Add(redaction);
        }
        ordered.Sort(static (left, right) =>
        {
            int length = right.Length.CompareTo(left.Length);
            return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
        });
        for (int i = 0; i < ordered.Count; i++)
            value = value.Replace(ordered[i], "[REDACTED]", StringComparison.Ordinal);
        return value;
    }

    private sealed record CapturedOutput(string Text, bool WasTruncated);
}
