// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidToolchainProcessResult(
    int ExitCode,
    long DurationMilliseconds,
    string StandardOutput,
    string StandardError)
{
    internal string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

internal interface IAndroidToolchainProcessRunner
{
    Task<AndroidToolchainProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class AndroidToolchainProcessRunner : IAndroidToolchainProcessRunner
{
    public async Task<AndroidToolchainProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Android toolchain executable is missing.", executablePath);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Android toolchain probe working directory is missing: {workingDirectory}");

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        ProcessStartInfo startInfo = CreateStartInfo(executablePath, arguments, workingDirectory);
        foreach (KeyValuePair<string, string> variable in environment)
            startInfo.Environment[variable.Key] = variable.Value;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start Android toolchain probe: {executablePath}");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCancellation.Token);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(linkedCancellation.Token);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Android toolchain probe timed out after {timeout}: {executablePath}");
        }
        finally
        {
            stopwatch.Stop();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }

        return new AndroidToolchainProcessResult(
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        bool isBatch = OperatingSystem.IsWindows() &&
                       (executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                        executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        var result = new ProcessStartInfo
        {
            FileName = isBatch ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isBatch)
        {
            // ArgumentList escapes embedded quotes as \" for native argv parsing. cmd.exe parses
            // /C itself and treats those backslashes literally, so provide its command tail raw.
            result.Arguments = "/d /c \"" + BuildBatchCommand(executablePath, arguments) + "\"";
        }
        else
        {
            for (int i = 0; i < arguments.Count; i++)
                result.ArgumentList.Add(arguments[i]);
        }
        return result;
    }

    private static string BuildBatchCommand(string executablePath, IReadOnlyList<string> arguments)
    {
        var result = new StringBuilder();
        AppendBatchArgument(result, executablePath);
        for (int i = 0; i < arguments.Count; i++)
        {
            result.Append(' ');
            AppendBatchArgument(result, arguments[i]);
        }
        return result.ToString();
    }

    private static void AppendBatchArgument(StringBuilder result, string value)
    {
        if (value.IndexOfAny(['&', '|', '<', '>', '^', '\r', '\n']) >= 0)
            throw new ArgumentException("Android toolchain batch argument contains unsupported shell metacharacters.", nameof(value));
        result.Append('"');
        result.Append(value.Replace("%", "%%", StringComparison.Ordinal).Replace("\"", "\"\"", StringComparison.Ordinal));
        result.Append('"');
    }
}
