// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidNativeSymbolAuditEntry(
    string Path,
    string Abi,
    int UndefinedSymbolCount,
    int PackageResolvedSymbolCount,
    int SystemResolvedSymbolCount,
    int WeakUnresolvedSymbolCount,
    IReadOnlyList<string> SystemLibraries);

/// <summary>Writes the deterministic API-level audit for packaged native imports.</summary>
internal static class AndroidNativeSymbolAudit
{
    internal const string FileName = "android-native-symbol-audit.json";

    internal static void Write(
        string path,
        int minSdkVersion,
        IReadOnlyList<AndroidNativeSymbolAuditEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = new AndroidNativeSymbolAuditEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            ordered[i] = entries[i];
        Array.Sort(ordered, static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

        var payload = new List<object>(ordered.Length);
        for (int i = 0; i < ordered.Length; i++)
        {
            AndroidNativeSymbolAuditEntry entry = ordered[i];
            string[] libraries = new string[entry.SystemLibraries.Count];
            for (int libraryIndex = 0; libraryIndex < libraries.Length; libraryIndex++)
                libraries[libraryIndex] = entry.SystemLibraries[libraryIndex];
            Array.Sort(libraries, StringComparer.Ordinal);
            payload.Add(new
            {
                path = entry.Path,
                abi = entry.Abi,
                undefinedSymbolCount = entry.UndefinedSymbolCount,
                packageResolvedSymbolCount = entry.PackageResolvedSymbolCount,
                systemResolvedSymbolCount = entry.SystemResolvedSymbolCount,
                weakUnresolvedSymbolCount = entry.WeakUnresolvedSymbolCount,
                systemLibraries = libraries,
            });
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(new { schemaVersion = 1, minSdkVersion, entries = payload }, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n", new UTF8Encoding(false));
    }
}
