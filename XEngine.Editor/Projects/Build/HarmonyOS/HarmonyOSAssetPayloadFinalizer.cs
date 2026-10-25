// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects;
using XEngine.Runtime;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>A hash-closed asset payload ready to be copied into the generated Stage project.</summary>
internal sealed record HarmonyOSFinalizedAssetPayload(
    string RootDirectory,
    string ManifestPath,
    IReadOnlyList<string> ShaderArtifacts,
    HarmonyOSPhysicalAssetPayload Inspection);

/// <summary>Copies offline shaders, writes the complete payload manifest, and verifies its closure.</summary>
internal static class HarmonyOSAssetPayloadFinalizer
{
    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static async Task<HarmonyOSFinalizedAssetPayload> FinalizeAsync(
        string payloadRoot,
        AssetPackagingMode packagingMode,
        IReadOnlyCollection<Guid> expectedAssets,
        string? shaderPackageDirectory = null,
        CancellationToken cancellationToken = default,
        string? projectRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(expectedAssets);
        string root = Path.GetFullPath(payloadRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"HarmonyOS asset payload directory is missing: {root}");

        IReadOnlyList<string> shaderArtifacts = CopyShaderPackage(root, shaderPackageDirectory);
        string manifestPath = Path.Combine(root, HarmonyOSAssetPayloadInspector.HashManifestFileName);
        await WriteManifestAsync(root, manifestPath, cancellationToken).ConfigureAwait(false);
        HarmonyOSPhysicalAssetPayload inspection = HarmonyOSAssetPayloadInspector.Inspect(
            root,
            packagingMode,
            FilterPackableAssets(expectedAssets, projectRoot),
            shaderArtifacts);
        return new HarmonyOSFinalizedAssetPayload(root, manifestPath, shaderArtifacts, inspection);
    }

    /// <summary>
    /// Drops folder '.meta' guids from the closure expectation. A swept package Resources folder
    /// records the folder entry alongside its children; the payload ships the children only, so
    /// the folder entry would otherwise fail the exact-closure comparison.
    /// </summary>
    private static List<Guid> FilterPackableAssets(IReadOnlyCollection<Guid> expectedAssets, string? projectRoot)
    {
        EditorAssetBackend? backend = EditorAssetBackend.Instance;
        var result = new List<Guid>(expectedAssets.Count);
        if (backend == null || string.IsNullOrEmpty(projectRoot))
        {
            result.AddRange(expectedAssets);
            return result;
        }

        // Resolve folder entries by scanning the same entry list the collector swept; package
        // paths must resolve through the package mount table, not the project root.
        var folderGuids = new HashSet<Guid>();
        foreach (AssetEntry entry in backend.GetAllEntries())
        {
            if (!string.IsNullOrEmpty(entry.Path) &&
                Directory.Exists(backend.GetEntryAbsolutePath(entry)))
            {
                folderGuids.Add(entry.Guid);
            }
        }

        foreach (Guid guid in expectedAssets)
        {
            if (!folderGuids.Contains(guid))
                result.Add(guid);
        }
        return result;
    }

    private static IReadOnlyList<string> CopyShaderPackage(string payloadRoot, string? sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return Array.Empty<string>();

        string source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"HarmonyOS offline shader package is missing: {source}");
        string destination = Path.Combine(payloadRoot, "Shaders");
        if (PathsOverlap(source, destination))
            throw new InvalidDataException("HarmonyOS offline shader source cannot overlap its payload destination.");

        ShaderArtifactBuildPackager.CopyPreparedPackage(source, destination);
        string[] files = Directory.GetFiles(destination, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var artifacts = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            string extension = Path.GetExtension(files[i]);
            if (extension.Equals(".xshader", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".spv", StringComparison.OrdinalIgnoreCase))
            {
                artifacts.Add(Path.GetFileName(files[i]));
            }
        }
        return artifacts.AsReadOnly();
    }

    private static async Task WriteManifestAsync(
        string payloadRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        string[] discovered = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories);
        var files = new List<string>(discovered.Length);
        for (int i = 0; i < discovered.Length; i++)
        {
            if (!string.Equals(discovered[i], manifestPath, PathComparison))
                files.Add(discovered[i]);
        }
        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(
            left.Replace('\\', '/'),
            right.Replace('\\', '/')));

        var entries = new List<object>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = Path.GetRelativePath(payloadRoot, files[i]).Replace('\\', '/');
            FileInfo file = new(files[i]);
            entries.Add(new
            {
                path = relativePath,
                sha256 = await ComputeSha256Async(files[i], cancellationToken).ConfigureAwait(false),
                bytes = file.Length,
            });
        }

        string json = JsonSerializer.Serialize(new { schemaVersion = 1, files = entries }, s_jsonOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd() + "\n";
        string temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool PathsOverlap(string left, string right)
    {
        string leftPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string rightPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return string.Equals(leftPath, rightPath, PathComparison) ||
            leftPath.StartsWith(rightPath + Path.DirectorySeparatorChar, PathComparison) ||
            rightPath.StartsWith(leftPath + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
