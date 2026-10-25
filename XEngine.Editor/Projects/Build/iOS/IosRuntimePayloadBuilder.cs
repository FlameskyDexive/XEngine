// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Ios;

/// <summary>Publishes an iOS-conditioned Runtime and its RID-matched managed dependency closure.</summary>
internal static class IosRuntimePayloadBuilder
{
    internal static async Task<string> BuildAsync(
        string searchRoot,
        string outputDirectory,
        string runtimeIdentifier,
        CancellationToken cancellationToken = default,
        string dotnetExecutable = "dotnet")
    {
        if (runtimeIdentifier is not ("ios-arm64" or "iossimulator-arm64"))
            throw new ArgumentOutOfRangeException(nameof(runtimeIdentifier), runtimeIdentifier, "Unsupported iOS RID.");

        string projectPath = FindRuntimeProject(searchRoot);
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && HasEntries(output))
            throw new IOException($"iOS Runtime output directory must be empty: {output}");
        Directory.CreateDirectory(output);

        string intermediateRoot = Path.Combine(output, ".obj") + Path.DirectorySeparatorChar;
        var start = new ProcessStartInfo
        {
            FileName = dotnetExecutable,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Reusable MSBuild workers inherit the redirected streams on macOS. Even
        // after publish exits, their open write ends prevent ReadToEndAsync reaching
        // EOF. Scope server shutdown to this child rather than the Editor process.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        string[] arguments =
        [
            "publish",
            projectPath,
            "--configuration", "Release",
            "--runtime", runtimeIdentifier,
            "--property:IsIosTarget=true",
            "--property:IosIntermediateRoot=" + intermediateRoot,
            "--property:CopyLocalLockFileAssemblies=true",
            "--property:SelfContained=false",
            "--output", output,
            "--disable-build-servers",
            "--nologo",
            "--verbosity:minimal",
        ];
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to start dotnet while publishing the iOS Runtime.", ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string outputText = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
            (await stderr.ConfigureAwait(false));
        if (process.ExitCode != 0)
        {
            const int maxOutputCharacters = 8192;
            if (outputText.Length > maxOutputCharacters)
                outputText = outputText[^maxOutputCharacters..];
            throw new InvalidOperationException("iOS-target XEngine.Runtime publish failed:" +
                Environment.NewLine + outputText.Trim());
        }

        if (!File.Exists(Path.Combine(output, "XEngine.Runtime.dll")) ||
            !File.Exists(Path.Combine(output, "XEngine.Runtime.deps.json")))
        {
            throw new InvalidDataException("iOS Runtime publish completed without its managed payload.");
        }

        TryDeleteDirectory(Path.Combine(output, ".obj"));
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
            "XEngine.Runtime.csproj was not found. The Editor distribution must provide a prebuilt iOS Runtime payload.");
    }

    private static bool HasEntries(string path)
    {
        using System.Collections.Generic.IEnumerator<string> entries =
            Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A completed payload is independent of intermediate files; leave an undeletable cache
            // for diagnostics rather than converting a successful publish into a failed build.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }
}
