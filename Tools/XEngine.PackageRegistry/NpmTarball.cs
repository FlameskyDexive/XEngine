// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XEngine.PackageRegistry;

/// <summary>
/// Packs and extracts npm package tarballs: a gzipped tar archive whose entries all sit under a
/// single <c>package/</c> root (the layout npm CLI produces and the registry expects).
///
/// <para><see cref="Pack"/> applies the wire-name transform — the tarball's package.json carries the
/// npm name (<c>@inxep/spine</c>) and npm-named dependency keys while the on-disk source keeps
/// engine names (<c>com.xengine.spine</c>). <see cref="Extract"/> reverses the layout into a plain
/// directory, refusing path-traversal entries and non-file entry types.</para>
/// </summary>
public static class NpmTarball
{
    public const string RootPrefix = "package/";

    /// <summary>
    /// Pack <paramref name="packageDir"/> (a package root containing package.json) into an npm
    /// tarball at <paramref name="outputFile"/>. Entries are written under <c>package/</c> in
    /// deterministic ordinal path order. Returns the tarball filename npm would derive
    /// (e.g. <c>inxep-spine-0.2.0.tgz</c>) and the applied name transform.
    /// </summary>
    public static (string FileName, string NpmName, string Version) Pack(string packageDir, string outputFile)
    {
        string manifestPath = Path.Combine(packageDir, "package.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"package.json not found under '{packageDir}'.");

        JsonObject manifest = ParseManifest(manifestPath);
        string engineName = (manifest["name"]?.GetValue<string>()) ?? "";
        if (string.IsNullOrWhiteSpace(engineName))
            throw new InvalidDataException($"package.json at '{manifestPath}' has no name.");
        string version = (manifest["version"]?.GetValue<string>()) ?? "";
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException($"package.json at '{manifestPath}' has no version.");

        // Wire transform: engine name -> scoped npm name, dependency keys likewise.
        string npmName = NpmPackageName.ToNpmName(engineName);
        manifest["name"] = npmName;
        if (manifest["dependencies"] is JsonObject dependencies)
        {
            var rewritten = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> dependency in dependencies)
            {
                string key = NpmPackageName.ToNpmName(dependency.Key);
                rewritten[key] = dependency.Value?.DeepClone();
            }
            manifest["dependencies"] = rewritten;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        using FileStream output = File.Create(outputFile);
        using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);

        // package.json first (rewritten wire copy), then everything else in ordinal order —
        // deterministic byte-for-byte reruns for unchanged sources.
        WriteEntryFromBytes(writer, RootPrefix + "package.json",
            JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }),
            File.GetLastWriteTimeUtc(manifestPath));

        foreach (string file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            if (string.Equals(file, manifestPath, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
            // Build intermediates never belong in a published tarball: NuGet writes
            // *.nuget.dgspec.json next to the project file regardless of BaseIntermediateOutputPath,
            // so Tests~/obj content reappears even when the csproj redirects its obj. (bit
            // @inxep/gpu-animation 0.1.0 and @inxep/hudmesh before this rule.)
            if (IsExcludedBuildArtifact(relative)) continue;
            string entryName = RootPrefix + relative;
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(File.ReadAllBytes(file)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                ModificationTime = File.GetLastWriteTimeUtc(file),
            };
            writer.WriteEntry(entry);
        }

        return (NpmPackageName.TarballFileName(npmName, version), npmName, version);
    }

    /// <summary>True for paths under obj/ or bin/ at any depth (MSBuild/Roslyn/NuGet outputs).</summary>
    private static bool IsExcludedBuildArtifact(string forwardSlashRelativePath)
    {
        int start = 0;
        while (start <= forwardSlashRelativePath.Length)
        {
            int slash = forwardSlashRelativePath.IndexOf('/', start);
            string segment = slash < 0
                ? forwardSlashRelativePath[start..]
                : forwardSlashRelativePath[start..slash];
            if (segment is "obj" or "bin")
                return true;
            if (slash < 0) break;
            start = slash + 1;
        }
        return false;
    }

    /// <summary>
    /// Extract an npm tarball into <paramref name="destinationDir"/> (created if missing),
    /// stripping the <c>package/</c> root. Throws <see cref="InvalidDataException"/> on entries
    /// that escape the destination or use non-regular entry types (symlinks/hardlinks).
    /// </summary>
    public static void Extract(string tarballFile, string destinationDir)
    {
        using FileStream input = File.OpenRead(tarballFile);
        Extract(input, destinationDir);
    }

    public static void Extract(Stream tarballStream, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        string destinationRoot = Path.GetFullPath(destinationDir);
        using var gzip = new GZipStream(tarballStream, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            string name = entry.Name.Replace('\\', '/');
            if (!name.StartsWith(RootPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"npm tarball entry '{entry.Name}' is not under the 'package/' root.");

            string relative = name[RootPrefix.Length..];
            if (relative.Length == 0) continue;

            string destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!destinationPath.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"npm tarball entry '{entry.Name}' escapes the extraction directory.");

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destinationPath);
                    break;
                case TarEntryType.RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    using (FileStream file = File.Create(destinationPath))
                    {
                        entry.DataStream?.CopyTo(file);
                    }
                    break;
                default:
                    throw new InvalidDataException(
                        $"npm tarball entry '{entry.Name}' has unsupported type '{entry.EntryType}'.");
            }
        }
    }

    private static JsonObject ParseManifest(string path)
    {
        JsonObject? manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (manifest is null)
            throw new InvalidDataException($"package.json at '{path}' is not a JSON object.");
        return manifest;
    }

    private static void WriteEntryFromBytes(TarWriter writer, string entryName, byte[] content, DateTime modificationTimeUtc)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = new MemoryStream(content),
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            ModificationTime = new DateTimeOffset(modificationTimeUtc, TimeSpan.Zero),
        };
        writer.WriteEntry(entry);
    }
}
