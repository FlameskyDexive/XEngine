// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidNativeElfInventoryEntry(
    string Path,
    string Abi,
    string Soname,
    string GnuBuildId,
    IReadOnlyList<string> NeededLibraries);

/// <summary>Writes the deterministic native ELF identity inventory for a packaged Android player.</summary>
internal static class AndroidNativeElfInventory
{
    internal const string FileName = "android-native-elf-inventory.json";

    internal static void Write(string path, IReadOnlyList<AndroidNativeElfInventoryEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = new AndroidNativeElfInventoryEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            ordered[i] = entries[i];
        Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

        var payload = new List<object>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            AndroidNativeElfInventoryEntry entry = ordered[i];
            string[] needed = new string[entry.NeededLibraries.Count];
            for (int dependencyIndex = 0; dependencyIndex < needed.Length; dependencyIndex++)
                needed[dependencyIndex] = entry.NeededLibraries[dependencyIndex];
            Array.Sort(needed, StringComparer.Ordinal);
            payload.Add(new
            {
                path = entry.Path,
                abi = entry.Abi,
                soname = entry.Soname,
                gnuBuildId = entry.GnuBuildId,
                neededLibraries = needed,
            });
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(new { schemaVersion = 1, entries = payload }, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n", new UTF8Encoding(false));
    }
}
