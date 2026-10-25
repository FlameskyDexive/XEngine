// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidNativeSymbolArchiveNativeEntry(
    string PackagePath,
    string Abi,
    string ArchivePath,
    string SymbolSource,
    long SymbolBytes,
    string SymbolSha256,
    string GnuBuildId,
    bool SymbolHasStaticSymbolTable);

internal sealed record AndroidNativeSymbolArchiveManagedEntry(
    string ArchivePath,
    long Bytes,
    string Sha256);

internal sealed record AndroidNativeSymbolArchiveSnapshot(
    string ArchivePath,
    IReadOnlyList<AndroidNativeSymbolArchiveNativeEntry> NativeEntries,
    IReadOnlyList<AndroidNativeSymbolArchiveManagedEntry> ManagedEntries);

/// <summary>Archives unstripped native libraries and managed PDBs before Release native stripping.</summary>
internal static class AndroidNativeSymbolArchive
{
    internal const string ArchiveFileName = "android-native-symbols.zip";
    internal const string ManifestFileName = "android-native-symbol-archive.json";

    private static readonly DateTimeOffset s_zipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal static AndroidNativeSymbolArchiveSnapshot Create(
        AndroidPlayerProjectLayout layout,
        string unstrippedArtifactPath,
        string configuration)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(unstrippedArtifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        string symbolsDirectory = Path.Combine(layout.RootDirectory, "symbols");
        Directory.CreateDirectory(symbolsDirectory);
        string archivePath = Path.Combine(symbolsDirectory, ArchiveFileName);
        var nativeEntries = new List<AndroidNativeSymbolArchiveNativeEntry>();
        var managedEntries = new List<AndroidNativeSymbolArchiveManagedEntry>();
        using (ZipArchive package = ZipFile.OpenRead(unstrippedArtifactPath))
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            string prefix = layout.OutputMode == AndroidOutputMode.Aab ? "base/lib/" : "lib/";
            var packageEntries = new List<ZipArchiveEntry>();
            for (int index = 0; index < package.Entries.Count; index++)
            {
                ZipArchiveEntry entry = package.Entries[index];
                if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
                    entry.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                {
                    packageEntries.Add(entry);
                }
            }
            packageEntries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.FullName, right.FullName));
            for (int index = 0; index < packageEntries.Count; index++)
            {
                ZipArchiveEntry source = packageEntries[index];
                string packagePath = source.FullName.Replace('\\', '/');
                (string abi, string fileName) = GetAbiAndFileName(packagePath, prefix);
                byte[] contents = ReadEntryBytes(source);
                using var stream = new MemoryStream(contents, writable: false);
                AndroidElfDynamicMetadata metadata = AndroidElfInspector.ReadDynamicMetadata(stream, packagePath);
                AndroidElfInspector.RequireIdentity(metadata, packagePath);
                stream.Position = 0;
                bool hasStaticSymbolTable = AndroidElfInspector.HasStaticSymbolTable(stream, packagePath);
                string archiveEntryPath = "native/" + abi + "/" + fileName;
                WriteArchiveEntry(archive, archiveEntryPath, contents);
                nativeEntries.Add(new AndroidNativeSymbolArchiveNativeEntry(
                    packagePath,
                    abi,
                    archiveEntryPath,
                    "package-unstripped",
                    contents.LongLength,
                    Convert.ToHexString(SHA256.HashData(contents)),
                    metadata.GnuBuildId!,
                    hasStaticSymbolTable));
            }

            string managedDirectory = Path.Combine(
                layout.RootDirectory,
                "bin",
                configuration,
                "net10.0-android",
                layout.RuntimeIdentifiers[0]);
            if (Directory.Exists(managedDirectory))
            {
                string[] pdbs = Directory.GetFiles(managedDirectory, "*.pdb", SearchOption.AllDirectories);
                Array.Sort(pdbs, StringComparer.Ordinal);
                for (int index = 0; index < pdbs.Length; index++)
                {
                    string source = pdbs[index];
                    string relative = Path.GetRelativePath(managedDirectory, source).Replace('\\', '/');
                    if (relative.StartsWith("../", StringComparison.Ordinal))
                        throw new InvalidDataException($"Android managed symbol is outside the package output: {source}");
                    byte[] contents = File.ReadAllBytes(source);
                    string archiveEntryPath = "managed/" + relative;
                    WriteArchiveEntry(archive, archiveEntryPath, contents);
                    managedEntries.Add(new AndroidNativeSymbolArchiveManagedEntry(
                        archiveEntryPath,
                        contents.LongLength,
                        Convert.ToHexString(SHA256.HashData(contents))));
                }
            }
        }

        if (nativeEntries.Count == 0)
            throw new InvalidDataException("Android symbol archive found no native libraries.");
        if (managedEntries.Count == 0)
            throw new InvalidDataException("Android symbol archive found no managed PDBs.");
        return new AndroidNativeSymbolArchiveSnapshot(archivePath, nativeEntries, managedEntries);
    }

    internal static AndroidNativeSymbolArchiveSnapshot ReconcileWithStrippedArtifact(
        AndroidNativeSymbolArchiveSnapshot archive,
        AndroidPlayerProjectLayout layout,
        string configuration,
        string strippedArtifactPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(strippedArtifactPath);

        IReadOnlyDictionary<string, string> finalBuildIds = ReadFinalNativeBuildIds(layout, strippedArtifactPath);
        var reconciledEntries = new List<AndroidNativeSymbolArchiveNativeEntry>(archive.NativeEntries.Count);
        var replacements = new List<(string ArchivePath, byte[] Contents)>();
        for (int index = 0; index < archive.NativeEntries.Count; index++)
        {
            AndroidNativeSymbolArchiveNativeEntry source = archive.NativeEntries[index];
            if (!finalBuildIds.TryGetValue(source.PackagePath, out string? finalBuildId) ||
                string.Equals(source.GnuBuildId, finalBuildId, StringComparison.Ordinal))
            {
                reconciledEntries.Add(source);
                continue;
            }

            string debugCompanionPath = GetGeneratedDebugCompanionPath(layout, configuration, source);
            if (!File.Exists(debugCompanionPath))
            {
                throw new InvalidDataException(
                    $"Android native strip changed the GNU build-id for {source.PackagePath}, " +
                    $"but no matching generated debug companion was found at {debugCompanionPath}.");
            }
            byte[] contents = File.ReadAllBytes(debugCompanionPath);
            using var stream = new MemoryStream(contents, writable: false);
            AndroidElfInspector.RequireMachine(stream, debugCompanionPath, source.Abi);
            string debugBuildId = AndroidElfInspector.ReadBuildId(stream, debugCompanionPath);
            bool hasStaticSymbolTable = AndroidElfInspector.HasStaticSymbolTable(stream, debugCompanionPath);
            if (!hasStaticSymbolTable)
            {
                throw new InvalidDataException(
                    $"Android generated debug companion has no static symbol table: {debugCompanionPath}");
            }
            if (!string.Equals(debugBuildId, finalBuildId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Android generated debug companion does not match the final GNU build-id for {source.PackagePath}.");
            }

            reconciledEntries.Add(source with
            {
                SymbolSource = "generated-debug-companion",
                SymbolBytes = contents.LongLength,
                SymbolSha256 = Convert.ToHexString(SHA256.HashData(contents)),
                GnuBuildId = debugBuildId,
                SymbolHasStaticSymbolTable = true,
            });
            replacements.Add((source.ArchivePath, contents));
        }

        if (replacements.Count != 0)
        {
            using ZipArchive zip = ZipFile.Open(archive.ArchivePath, ZipArchiveMode.Update);
            for (int index = 0; index < replacements.Count; index++)
            {
                (string archivePath, byte[] contents) = replacements[index];
                ZipArchiveEntry? existing = zip.GetEntry(archivePath);
                if (existing == null)
                    throw new InvalidDataException($"Android native symbol archive entry is missing: {archivePath}");
                existing.Delete();
                WriteArchiveEntry(zip, archivePath, contents);
            }
        }
        return new AndroidNativeSymbolArchiveSnapshot(
            archive.ArchivePath,
            reconciledEntries,
            archive.ManagedEntries);
    }

    internal static void VerifyAndWrite(
        AndroidNativeSymbolArchiveSnapshot archive,
        AndroidPlayerProjectLayout layout,
        string strippedArtifactPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(strippedArtifactPath);
        if (!File.Exists(archive.ArchivePath))
            throw new FileNotFoundException("Android native symbol archive is missing.", archive.ArchivePath);

        var finalEntries = new Dictionary<string, FinalNativeEntry>(StringComparer.Ordinal);
        using (ZipArchive package = ZipFile.OpenRead(strippedArtifactPath))
        {
            string prefix = layout.OutputMode == AndroidOutputMode.Aab ? "base/lib/" : "lib/";
            for (int index = 0; index < package.Entries.Count; index++)
            {
                ZipArchiveEntry entry = package.Entries[index];
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                    !entry.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string packagePath = entry.FullName.Replace('\\', '/');
                _ = GetAbiAndFileName(packagePath, prefix);
                byte[] contents = ReadEntryBytes(entry);
                using var stream = new MemoryStream(contents, writable: false);
                AndroidElfDynamicMetadata metadata = AndroidElfInspector.ReadDynamicMetadata(stream, packagePath);
                AndroidElfInspector.RequireIdentity(metadata, packagePath);
                stream.Position = 0;
                bool hasStaticSymbolTable = AndroidElfInspector.HasStaticSymbolTable(stream, packagePath);
                if (!finalEntries.TryAdd(packagePath, new FinalNativeEntry(
                    contents.LongLength,
                    Convert.ToHexString(SHA256.HashData(contents)),
                    metadata.GnuBuildId!,
                    hasStaticSymbolTable)))
                {
                    throw new InvalidDataException($"Android stripped package contains duplicate native library: {packagePath}");
                }
            }
        }

        var entries = new List<object>(archive.NativeEntries.Count);
        for (int index = 0; index < archive.NativeEntries.Count; index++)
        {
            AndroidNativeSymbolArchiveNativeEntry source = archive.NativeEntries[index];
            if (!finalEntries.Remove(source.PackagePath, out FinalNativeEntry? final))
                throw new InvalidDataException($"Android stripped package is missing archived native library: {source.PackagePath}");
            if (!string.Equals(source.GnuBuildId, final.GnuBuildId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Android native strip changed the GNU build-id for {source.PackagePath}.");
            }
            if (IsUnexpectedStripSizeIncrease(source, final.Bytes))
            {
                throw new InvalidDataException(
                    $"Android native strip increased the library size for {source.PackagePath}.");
            }
            if (source.SymbolHasStaticSymbolTable && final.HasStaticSymbolTable)
            {
                throw new InvalidDataException(
                    $"Android native strip retained the static symbol table for {source.PackagePath}.");
            }
            entries.Add(new
            {
                path = source.PackagePath,
                abi = source.Abi,
                archivePath = source.ArchivePath,
                symbolSource = source.SymbolSource,
                symbolBytes = source.SymbolBytes,
                strippedBytes = final.Bytes,
                symbolSha256 = source.SymbolSha256,
                strippedSha256 = final.Sha256,
                gnuBuildId = source.GnuBuildId,
                symbolHasStaticSymbolTable = source.SymbolHasStaticSymbolTable,
                strippedHasStaticSymbolTable = final.HasStaticSymbolTable,
            });
        }
        if (finalEntries.Count != 0)
            throw new InvalidDataException("Android stripped package contains native libraries absent from the symbol archive.");

        string[] archivedEntries;
        using (ZipArchive zip = ZipFile.OpenRead(archive.ArchivePath))
        {
            archivedEntries = new string[zip.Entries.Count];
            for (int index = 0; index < zip.Entries.Count; index++)
                archivedEntries[index] = zip.Entries[index].FullName;
        }
        Array.Sort(archivedEntries, StringComparer.Ordinal);

        string manifestPath = Path.Combine(layout.RootDirectory, "verification", ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        string json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            archivePath = "symbols/" + ArchiveFileName,
            archiveBytes = new FileInfo(archive.ArchivePath).Length,
            archiveSha256 = HashFile(archive.ArchivePath),
            nativeEntries = entries,
            managedSymbols = archive.ManagedEntries,
            archiveEntries = archivedEntries,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(manifestPath, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n", new UTF8Encoding(false));
    }

    internal static bool IsUnexpectedStripSizeIncrease(
        AndroidNativeSymbolArchiveNativeEntry source,
        long strippedBytes) =>
        source.SymbolHasStaticSymbolTable &&
        strippedBytes > source.SymbolBytes &&
        string.Equals(source.SymbolSource, "package-unstripped", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, string> ReadFinalNativeBuildIds(
        AndroidPlayerProjectLayout layout,
        string strippedArtifactPath)
    {
        string prefix = layout.OutputMode == AndroidOutputMode.Aab ? "base/lib/" : "lib/";
        var buildIds = new Dictionary<string, string>(StringComparer.Ordinal);
        using ZipArchive package = ZipFile.OpenRead(strippedArtifactPath);
        for (int index = 0; index < package.Entries.Count; index++)
        {
            ZipArchiveEntry entry = package.Entries[index];
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) ||
                !entry.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string packagePath = entry.FullName.Replace('\\', '/');
            _ = GetAbiAndFileName(packagePath, prefix);
            byte[] contents = ReadEntryBytes(entry);
            using var stream = new MemoryStream(contents, writable: false);
            AndroidElfDynamicMetadata metadata = AndroidElfInspector.ReadDynamicMetadata(stream, packagePath);
            AndroidElfInspector.RequireIdentity(metadata, packagePath);
            if (!buildIds.TryAdd(packagePath, metadata.GnuBuildId!))
                throw new InvalidDataException($"Android stripped package contains duplicate native library: {packagePath}");
        }
        return buildIds;
    }

    private static string GetGeneratedDebugCompanionPath(
        AndroidPlayerProjectLayout layout,
        string configuration,
        AndroidNativeSymbolArchiveNativeEntry entry)
    {
        string fileName = Path.GetFileName(entry.ArchivePath);
        string debugFileName = Path.GetFileNameWithoutExtension(fileName) + ".dbg.so";
        return Path.Combine(
            layout.RootDirectory,
            "obj",
            configuration,
            "net10.0-android",
            layout.RuntimeIdentifiers[0],
            "app_shared_libraries",
            entry.Abi,
            debugFileName);
    }

    private static (string Abi, string FileName) GetAbiAndFileName(string packagePath, string prefix)
    {
        string remainder = packagePath[prefix.Length..];
        int separator = remainder.IndexOf('/');
        if (separator <= 0 || remainder.IndexOf('/', separator + 1) >= 0)
            throw new InvalidDataException($"Android native symbol archive has an invalid library path: {packagePath}");
        string abi = remainder[..separator];
        string fileName = remainder[(separator + 1)..];
        if (!fileName.EndsWith(".so", StringComparison.Ordinal) ||
            fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
        {
            throw new InvalidDataException($"Android native symbol archive has an invalid library name: {packagePath}");
        }
        return (abi, fileName);
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteArchiveEntry(ZipArchive archive, string path, byte[] contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = s_zipTimestamp;
        using Stream output = entry.Open();
        output.Write(contents, 0, contents.Length);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record FinalNativeEntry(
        long Bytes,
        string Sha256,
        string GnuBuildId,
        bool HasStaticSymbolTable);
}
