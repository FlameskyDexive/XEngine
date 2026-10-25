// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.Android;

internal static class AndroidRuntimePayloadBuilder
{
    internal static async Task<string> BuildAsync(
        string searchRoot,
        string outputDirectory,
        string runtimeIdentifier,
        AndroidToolchainSnapshot toolchain,
        CancellationToken cancellationToken = default)
    {
        string projectPath = FindRuntimeProject(searchRoot);
        Directory.CreateDirectory(outputDirectory);
        var runner = new AndroidToolchainProcessRunner();
        string[] arguments =
        [
            "build",
            projectPath,
            "-c", "Release",
            "-r", runtimeIdentifier,
            "-p:IsAndroidTarget=true",
            "-p:AndroidIntermediateRoot=" + Path.Combine(outputDirectory, ".obj") + Path.DirectorySeparatorChar,
            "-p:CopyLocalLockFileAssemblies=true",
            "-o", outputDirectory,
            "--nologo",
            "-v:minimal",
        ];
        AndroidToolchainProcessResult result = await runner.RunAsync(
            toolchain.DotNetExecutable,
            arguments,
            Path.GetDirectoryName(projectPath)!,
            CreateEnvironment(toolchain),
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string output = result.CombinedOutput;
            if (output.Length > 8192)
                output = output[^8192..];
            throw new InvalidOperationException(
                "Android-target XEngine.Runtime build failed:" + Environment.NewLine + output);
        }
        if (!File.Exists(Path.Combine(outputDirectory, "XEngine.Runtime.dll")) ||
            !File.Exists(Path.Combine(outputDirectory, "XEngine.Runtime.deps.json")))
        {
            throw new InvalidDataException("Android Runtime build completed without its managed payload.");
        }
        return outputDirectory;
    }

    internal static string FindRuntimeProject(string searchRoot)
    {
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
            "XEngine.Runtime.csproj was not found. The Editor distribution must provide a prebuilt Android Runtime payload.");
    }

    private static IReadOnlyDictionary<string, string> CreateEnvironment(AndroidToolchainSnapshot toolchain)
    {
        string dotnetRoot = Path.GetDirectoryName(toolchain.DotNetExecutable)!;
        string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_ROOT"] = dotnetRoot,
            ["JAVA_HOME"] = toolchain.JavaSdkRoot,
            ["ANDROID_HOME"] = toolchain.AndroidSdkRoot,
            ["ANDROID_SDK_ROOT"] = toolchain.AndroidSdkRoot,
            ["ANDROID_NDK_ROOT"] = toolchain.AndroidNdkRoot,
            ["PATH"] = dotnetRoot + Path.PathSeparator + inheritedPath,
        };
    }
}
