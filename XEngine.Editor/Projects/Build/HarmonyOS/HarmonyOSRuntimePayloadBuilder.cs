// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Builds the HarmonyOS-conditioned XEngine Runtime managed payload.</summary>
internal sealed class HarmonyOSRuntimePayloadBuilder(IHarmonyOSProcessRunner? processRunner = null)
{
    private readonly IHarmonyOSProcessRunner _processRunner = processRunner ?? new HarmonyOSProcessRunner();

    internal async Task<string> BuildAsync(
        string searchRoot,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        string projectPath = FindRuntimeProject(searchRoot);
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && HasEntries(output))
            throw new IOException($"HarmonyOS Runtime output directory must be empty: {output}");

        string intermediate = Path.Combine(output, ".obj") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(output);
        HarmonyOSProcessResult result = await _processRunner.RunAsync(new HarmonyOSProcessCommand
        {
            ExecutablePath = "dotnet",
            Arguments =
            [
                "build",
                projectPath,
                "--configuration", "Release",
                "--property:OpenHarmonyTarget=true",
                "--property:IsHarmonyOSTarget=true",
                "--property:HarmonyOSIntermediateRoot=" + intermediate,
                "--property:CopyLocalLockFileAssemblies=true",
                "--output", output,
                "--nologo",
                "--verbosity:minimal",
            ],
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            Timeout = TimeSpan.FromMinutes(5),
            Redactions = [Path.GetFullPath(searchRoot)],
        }, cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
            throw new TimeoutException("HarmonyOS-target XEngine Runtime build timed out.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException("HarmonyOS-target XEngine Runtime build failed: " + result.CombinedOutput);
        if (!File.Exists(Path.Combine(output, "XEngine.Runtime.dll")) ||
            !File.Exists(Path.Combine(output, "XEngine.Runtime.deps.json")))
        {
            throw new InvalidDataException("HarmonyOS Runtime build completed without its managed payload.");
        }
        return output;
    }

    internal static string FindRuntimeProject(string searchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchRoot);
        string? directory = Path.GetFullPath(searchRoot);
        if (File.Exists(directory))
            directory = Path.GetDirectoryName(directory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory, "XEngine.Runtime", "XEngine.Runtime.csproj");
            if (File.Exists(candidate))
                return candidate;
            directory = Path.GetDirectoryName(directory);
        }
        throw new FileNotFoundException(
            "XEngine.Runtime.csproj was not found. The Editor distribution must provide a prebuilt HarmonyOS Runtime payload.");
    }

    private static bool HasEntries(string path)
    {
        using System.Collections.Generic.IEnumerator<string> entries =
            Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }
}
