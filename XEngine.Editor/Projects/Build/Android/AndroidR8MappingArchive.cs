// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidR8MappingSnapshot(
    string SourcePath,
    string ArchivePath,
    long Bytes,
    string Sha256);

/// <summary>
/// Archives the final R8 mapping emitted by a Release Android package build. The mapping is kept
/// outside the distributable APK and, for AABs, must also match bundletool's metadata entry.
/// </summary>
internal static class AndroidR8MappingArchive
{
    internal const string ArchiveFileName = "android-r8-mapping.txt";
    internal const string ManifestFileName = "android-r8-mapping.json";

    private const string BundleMetadataPath =
        "BUNDLE-METADATA/com.android.tools.build.obfuscation/proguard.map";

    internal static AndroidR8MappingSnapshot Create(
        AndroidPlayerProjectLayout layout,
        string configuration)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        if (!IsRelease(configuration))
            throw new ArgumentException("R8 mappings are only created for Release Android builds.", nameof(configuration));

        string sourcePath = Path.Combine(
            layout.RootDirectory,
            "bin",
            configuration,
            "net10.0-android",
            layout.RuntimeIdentifiers[0],
            "mapping.txt");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "Android Release build did not produce the required R8 mapping.txt.",
                sourcePath);
        }

        byte[] contents = File.ReadAllBytes(sourcePath);
        if (!HasContent(contents))
            throw new InvalidDataException("Android R8 mapping.txt is empty.");

        string symbolsDirectory = Path.Combine(layout.RootDirectory, "symbols");
        Directory.CreateDirectory(symbolsDirectory);
        string archivePath = Path.Combine(symbolsDirectory, ArchiveFileName);
        File.WriteAllBytes(archivePath, contents);
        return new AndroidR8MappingSnapshot(
            sourcePath,
            archivePath,
            contents.LongLength,
            Convert.ToHexString(SHA256.HashData(contents)));
    }

    internal static void VerifyAndWrite(
        AndroidR8MappingSnapshot snapshot,
        AndroidPlayerProjectLayout layout,
        string artifactPath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        if (!File.Exists(artifactPath))
            throw new FileNotFoundException("Android package artifact is missing.", artifactPath);

        byte[] archived = File.ReadAllBytes(snapshot.ArchivePath);
        if (archived.LongLength != snapshot.Bytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(archived)), snapshot.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Android archived R8 mapping no longer matches the final build output.");
        }

        if (layout.OutputMode == AndroidOutputMode.Aab)
            VerifyBundleMapping(artifactPath, archived);

        string reportPath = Path.Combine(layout.RootDirectory, "verification", ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var payload = new
        {
            schemaVersion = 1,
            archivePath = "symbols/" + ArchiveFileName,
            bytes = snapshot.Bytes,
            sha256 = snapshot.Sha256,
            bundledInAab = layout.OutputMode == AndroidOutputMode.Aab,
            bundleMetadataPath = layout.OutputMode == AndroidOutputMode.Aab ? BundleMetadataPath : null,
        };
        File.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .TrimEnd() + "\n",
            new UTF8Encoding(false));
    }

    private static void VerifyBundleMapping(string artifactPath, byte[] archived)
    {
        using ZipArchive bundle = ZipFile.OpenRead(artifactPath);
        ZipArchiveEntry? entry = bundle.GetEntry(BundleMetadataPath);
        if (entry == null)
            throw new InvalidDataException("Android App Bundle is missing the R8 mapping metadata entry.");
        if (entry.Length != archived.LongLength)
            throw new InvalidDataException("Android App Bundle R8 mapping byte count does not match the archived mapping.");

        using Stream input = entry.Open();
        byte[] packaged = ReadAllBytes(input, checked((int)entry.Length));
        if (!packaged.AsSpan().SequenceEqual(archived))
            throw new InvalidDataException("Android App Bundle R8 mapping does not match the archived mapping.");
    }

    private static byte[] ReadAllBytes(Stream input, int capacity)
    {
        using var output = new MemoryStream(capacity);
        input.CopyTo(output);
        return output.ToArray();
    }

    private static bool HasContent(ReadOnlySpan<byte> contents)
    {
        for (int index = 0; index < contents.Length; index++)
        {
            if (!char.IsWhiteSpace((char)contents[index]))
                return true;
        }
        return false;
    }

    private static bool IsRelease(string configuration) =>
        string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase);
}
