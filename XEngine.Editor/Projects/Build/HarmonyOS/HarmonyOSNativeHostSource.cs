// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Materializes the native HarmonyOS host sources embedded in the Editor distribution.</summary>
internal static class HarmonyOSNativeHostSource
{
    private static readonly (string ResourceName, string FileName)[] s_sources =
    [
        ("XEngine.HarmonyOS.Native.CMakeLists.txt", "CMakeLists.txt"),
        ("XEngine.HarmonyOS.Native.xengine_host_napi.cpp", "xengine_host_napi.cpp"),
        ("XEngine.HarmonyOS.Native.xengine_player_abi.h", "xengine_player_abi.h"),
    ];

    internal static async Task MaterializeAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && HasEntries(root))
            throw new IOException($"HarmonyOS native host source directory must be empty: {root}");

        Assembly assembly = typeof(HarmonyOSNativeHostSource).Assembly;
        var availableResources = new HashSet<string>(
            assembly.GetManifestResourceNames(),
            StringComparer.Ordinal);
        foreach ((string resourceName, _) in s_sources)
        {
            if (!availableResources.Contains(resourceName))
                throw new InvalidDataException($"Embedded HarmonyOS native host source is missing: {resourceName}");
        }

        Directory.CreateDirectory(root);
        foreach ((string resourceName, string fileName) in s_sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using Stream input = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Embedded HarmonyOS native host source is missing: {resourceName}");
            await using var output = new FileStream(
                Path.Combine(root, fileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasEntries(string directory)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        return entries.MoveNext();
    }
}
