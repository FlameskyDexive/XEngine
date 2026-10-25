using System.Diagnostics;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B2) — Spawns engine processes for build and headless-run, capturing structured
/// results. For B2 these are synchronous (the Host's own operation store is a follow-up); a long build
/// blocks the tool call but returns a clean structured result. Build uses the editor's existing
/// <c>--buildmode</c> CLI; headless run uses the built player / a sample's headless entry.
///
/// <para>The editor exe is resolved from the Host's own directory neighbourhood (a published editor
/// build) or an explicit <c>--editor</c> path. When not found the tool reports a clear error rather
/// than a silent failure.</para>
/// </summary>
internal static class BuildRunner
{
    private const int TailBytes = 4 * 1024;

    /// <summary>Build a project via <c>XEngine.Editor --buildmode &lt;project&gt; -o &lt;output&gt;</c>.</summary>
    /// <param name="buildProfile">
    /// Optional <c>.buildprofile</c> asset to build (name, project relative path or asset GUID). When
    /// omitted the editor builds whichever profile the project has marked active.
    /// </param>
    /// <param name="platform">
    /// Optional pipeline override (<c>desktop</c>, <c>android</c>, <c>ios</c>, <c>harmonyos</c>, <c>web</c>). Only
    /// meaningful without <paramref name="buildProfile"/>, which already carries a platform.
    /// </param>
    public static async Task<ProcessRunResult> BuildAsync(
        string editorExe,
        string projectRoot,
        string outputDir,
        string? buildProfile = null,
        string? platform = null,
        CancellationToken cancellationToken = default)
    {
        string args = BuildArguments(projectRoot, outputDir, buildProfile, platform);
        return await RunAsync(editorExe, args, "build", outputDir, cancellationToken);
    }

    /// <summary>The <c>XEngine.Editor</c> command line a build tool call turns into.</summary>
    internal static string BuildArguments(string projectRoot, string outputDir, string? buildProfile, string? platform)
    {
        string args = $"--buildmode \"{projectRoot}\" -o \"{outputDir}\"";
        if (!string.IsNullOrWhiteSpace(buildProfile)) args += $" --build-profile \"{buildProfile}\"";
        if (!string.IsNullOrWhiteSpace(platform)) args += $" --platform \"{platform}\"";
        return args;
    }

    /// <summary>
    /// Run a built player headlessly. The player exe is expected at <paramref name="playerExe"/>; it
    /// honours the engine's headless flags. This is the offline equivalent of Game.RunHeadless.
    /// </summary>
    public static async Task<ProcessRunResult> RunHeadlessAsync(
        string playerExe,
        string? scene,
        int maxFrames,
        int maxSeconds,
        CancellationToken cancellationToken = default)
    {
        var argParts = new List<string> { "--headless" };
        if (scene is not null) argParts.Add($"--scene \"{scene}\"");
        if (maxFrames > 0) argParts.Add($"--max-frames {maxFrames}");
        if (maxSeconds > 0) argParts.Add($"--max-seconds {maxSeconds}");
        return await RunAsync(playerExe, string.Join(' ', argParts), "headless-run", null, cancellationToken);
    }

    private static async Task<ProcessRunResult> RunAsync(
        string exe, string args, string label, string? outputPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(exe))
        {
            // ★ P1-6: actionable hint. The most common cause is passing the project root instead of the
            // published editor exe; tell the AI exactly what shape of path to supply.
            throw new FileNotFoundException(
                $"Executable not found: {exe}. The --editor argument must be the absolute path to a " +
                "published XEngine.Editor executable (e.g. C:/xengine/bin/XEngine.Editor.exe), not the " +
                "project root. Publish with 'dotnet publish XEngine.Editor -c Release' first.", exe);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read both streams fully (prevents deadlock when the child fills its stdout/stderr pipe).
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        sw.Stop();

        string combined = stdout + "\n" + stderr;
        string tail = combined.Length > TailBytes ? combined[(combined.Length - TailBytes)..] : combined;

        return new ProcessRunResult(
            Command: $"{label}: {Path.GetFileName(exe)} {args}",
            Succeeded: process.ExitCode == 0,
            ExitCode: process.ExitCode,
            DurationMs: sw.ElapsedMilliseconds,
            OutputPath: outputPath,
            OutputTail: tail.Trim());
    }
}
