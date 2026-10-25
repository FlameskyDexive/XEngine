// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using XEngine.Runtime;
using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using AssetBundlePackageManifest = XEngine.Runtime.AssetBundles.PackageManifest;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Physical asset entries found in a HarmonyOS player payload.</summary>
internal sealed record HarmonyOSPhysicalAssetEntry(
    long UncompressedBytes,
    long? CompressedBytes,
    string Container);

/// <summary>Result of comparing a generated HarmonyOS payload with the collected asset closure.</summary>
internal sealed record HarmonyOSPhysicalAssetPayload(
    bool IsExactClosure,
    string Verification,
    IReadOnlyDictionary<Guid, HarmonyOSPhysicalAssetEntry> Assets,
    IReadOnlyList<string> Containers);

/// <summary>Verifies that the physical HarmonyOS payload exactly matches the collected closure.</summary>
internal static class HarmonyOSAssetPayloadInspector
{
    internal const string HashManifestFileName = "xengine-harmonyos-payload.json";

    internal static HarmonyOSPhysicalAssetPayload Inspect(
        string payloadRoot,
        AssetPackagingMode packagingMode,
        IReadOnlyCollection<Guid> expectedAssets) =>
        Inspect(payloadRoot, packagingMode, expectedAssets, []);

    /// <summary>
    /// Verifies the assets plus the offline Vulkan artifact closure placed in the HarmonyOS rawfile
    /// staging root. Shader artifact names are relative to the <c>Shaders</c> directory.
    /// </summary>
    internal static HarmonyOSPhysicalAssetPayload Inspect(
        string payloadRoot,
        AssetPackagingMode packagingMode,
        IReadOnlyCollection<Guid> expectedAssets,
        IReadOnlyCollection<string> expectedShaderArtifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(expectedAssets);
        ArgumentNullException.ThrowIfNull(expectedShaderArtifacts);
        payloadRoot = Path.GetFullPath(payloadRoot);
        ValidateHashManifest(payloadRoot);

        BuiltInAssets.Initialize();
        var expectedPhysicalAssets = new HashSet<Guid>();
        foreach (Guid guid in expectedAssets)
        {
            if (!BuiltInAssets.IsBuiltIn(guid))
                expectedPhysicalAssets.Add(guid);
        }

        HarmonyOSPhysicalAssetPayload payload = packagingMode switch
        {
            AssetPackagingMode.LooseFiles => InspectLooseFiles(payloadRoot, expectedPhysicalAssets),
            AssetPackagingMode.XEnginePak => InspectPaks(payloadRoot, expectedPhysicalAssets),
            AssetPackagingMode.AssetBundle => InspectAssetBundles(payloadRoot, expectedPhysicalAssets),
            _ => throw new InvalidOperationException(
                $"HarmonyOS physical asset inspection does not support {packagingMode}."),
        };
        ValidateShaderArtifacts(payloadRoot, expectedShaderArtifacts);
        RejectNativeBinaries(payloadRoot);
        return payload;
    }

    private static void ValidateHashManifest(string payloadRoot)
    {
        string manifestPath = Path.Combine(payloadRoot, HashManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"HarmonyOS payload is missing {HashManifestFileName}.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number || schemaVersion.GetInt32() != 1 ||
            !root.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HarmonyOS payload hash manifest must declare schemaVersion 1 and files.");
        }

        var manifestFiles = new List<PayloadManifestFile>();
        string? previousPath = null;
        foreach (JsonElement entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("path", out JsonElement pathProperty) || pathProperty.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("sha256", out JsonElement hashProperty) || hashProperty.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("bytes", out JsonElement bytesProperty) || bytesProperty.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidDataException("HarmonyOS payload hash manifest contains an invalid file entry.");
            }

            string relativePath = NormalizePayloadPath(pathProperty.GetString() ?? string.Empty);
            string sha256 = RequireSha256(hashProperty.GetString() ?? string.Empty);
            long bytes = bytesProperty.GetInt64();
            if (bytes < 0)
                throw new InvalidDataException($"HarmonyOS payload hash manifest has a negative length: {relativePath}");
            if (previousPath != null && StringComparer.Ordinal.Compare(previousPath, relativePath) >= 0)
                throw new InvalidDataException("HarmonyOS payload hash manifest files must be ordinally sorted and unique.");
            previousPath = relativePath;
            manifestFiles.Add(new PayloadManifestFile(relativePath, sha256, bytes));
        }

        string[] actualFiles = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories);
        var actualPaths = new List<string>(actualFiles.Length);
        for (int i = 0; i < actualFiles.Length; i++)
        {
            string relativePath = Path.GetRelativePath(payloadRoot, actualFiles[i]).Replace('\\', '/');
            if (!string.Equals(relativePath, HashManifestFileName, StringComparison.Ordinal))
                actualPaths.Add(NormalizePayloadPath(relativePath));
        }
        actualPaths.Sort(StringComparer.Ordinal);
        if (manifestFiles.Count != actualPaths.Count)
            throw new InvalidDataException("HarmonyOS payload hash manifest does not cover the physical payload closure.");

        for (int i = 0; i < manifestFiles.Count; i++)
        {
            PayloadManifestFile expected = manifestFiles[i];
            if (!string.Equals(expected.Path, actualPaths[i], StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"HarmonyOS payload hash manifest does not match the physical payload closure at '{expected.Path}'.");
            }

            string actualPath = GetContainedPath(payloadRoot, expected.Path);
            FileInfo actual = new(actualPath);
            if (actual.Length != expected.Bytes || !string.Equals(ComputeSha256(actualPath), expected.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"HarmonyOS payload hash manifest entry does not match '{expected.Path}'.");
        }
    }

    private static void ValidateShaderArtifacts(string payloadRoot, IReadOnlyCollection<string> expectedArtifacts)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (string artifact in expectedArtifacts)
            expected.Add(NormalizeShaderArtifactPath(artifact));

        string shaderDirectory = Path.Combine(payloadRoot, "Shaders");
        if (!Directory.Exists(shaderDirectory))
        {
            if (expected.Count != 0)
                throw new InvalidDataException("HarmonyOS payload is missing required offline shader artifacts.");
            return;
        }

        string[] files = Directory.GetFiles(shaderDirectory, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var xshaderPaths = new List<string>();
        string? catalogPath = null;
        for (int i = 0; i < files.Length; i++)
        {
            string relativePath = Path.GetRelativePath(shaderDirectory, files[i]).Replace('\\', '/');
            string extension = Path.GetExtension(relativePath);
            if (IsShaderSourceExtension(extension))
                throw new InvalidDataException($"HarmonyOS payload contains a source-only shader: Shaders/{relativePath}");
            if (string.Equals(relativePath, ShaderArtifactBuildPackager.CatalogFileName, StringComparison.Ordinal))
            {
                catalogPath = files[i];
                continue;
            }
            if (string.Equals(extension, ".xshader", StringComparison.OrdinalIgnoreCase))
            {
                xshaderPaths.Add(files[i]);
                actual.Add(relativePath);
                continue;
            }
            if (string.Equals(extension, ".spv", StringComparison.OrdinalIgnoreCase))
            {
                ValidateSpirV(files[i], relativePath);
                actual.Add(relativePath);
                continue;
            }
            throw new InvalidDataException($"HarmonyOS payload contains an unexpected shader file: Shaders/{relativePath}");
        }

        if (xshaderPaths.Count != 0)
        {
            if (catalogPath == null)
                throw new InvalidDataException("HarmonyOS payload shader artifacts require catalog.xshadercatalog.");
            ValidateXshaderCatalog(catalogPath, shaderDirectory, xshaderPaths);
        }
        else if (catalogPath != null)
        {
            throw new InvalidDataException("HarmonyOS payload shader catalog has no offline shader artifacts.");
        }

        RequireExactStringClosure(expected, actual, "offline shader artifacts");
    }

    private static void ValidateXshaderCatalog(string catalogPath, string shaderDirectory, IReadOnlyList<string> xshaderPaths)
    {
        ShaderArtifactCatalog catalog;
        try
        {
            using var stream = File.OpenRead(catalogPath);
            catalog = ShaderArtifactCatalog.Read(stream);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException("HarmonyOS payload shader catalog is invalid.", exception);
        }

        var catalogFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<Guid, IReadOnlyList<ShaderArtifactKey>> entry in catalog.Entries)
        {
            IReadOnlyList<ShaderArtifactKey> keys = entry.Value;
            for (int i = 0; i < keys.Count; i++)
            {
                ShaderArtifactKey key = keys[i];
                string value = key.Value ?? throw new InvalidDataException("HarmonyOS shader catalog contains an empty artifact key.");
                string relativePath = value + ".xshader";
                string path = GetContainedPath(shaderDirectory, relativePath);
                if (!File.Exists(path) || !catalogFiles.Add(relativePath))
                    throw new InvalidDataException($"HarmonyOS shader catalog does not match artifact '{relativePath}'.");
                try
                {
                    using var stream = File.OpenRead(path);
                    ShaderArtifact artifact = ShaderArtifactCodec.Read(stream, key);
                    if (artifact.Backend != GraphicsBackend.Vulkan || artifact.Bytecode.Format != ShaderBytecodeFormat.SpirV)
                        throw new InvalidDataException($"HarmonyOS shader artifact is not Vulkan SPIR-V: {relativePath}");
                }
                catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
                {
                    throw new InvalidDataException($"HarmonyOS shader artifact is invalid: {relativePath}", exception);
                }
            }
        }

        var physicalFiles = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < xshaderPaths.Count; i++)
            physicalFiles.Add(Path.GetRelativePath(shaderDirectory, xshaderPaths[i]).Replace('\\', '/'));
        RequireExactStringClosure(catalogFiles, physicalFiles, "shader catalog");
    }

    private static void ValidateSpirV(string path, string relativePath)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[sizeof(uint)];
        if (stream.Read(header) != header.Length || BitConverter.ToUInt32(header) != 0x07230203)
            throw new InvalidDataException($"HarmonyOS shader artifact is not SPIR-V: Shaders/{relativePath}");
    }

    private static void RejectNativeBinaries(string payloadRoot)
    {
        string[] files = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories);
        for (int i = 0; i < files.Length; i++)
        {
            string extension = Path.GetExtension(files[i]);
            if (extension.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = Path.GetRelativePath(payloadRoot, files[i]).Replace('\\', '/');
                throw new InvalidDataException($"HarmonyOS payload contains an unowned native binary: {relativePath}");
            }
        }
    }

    /// <summary>
    /// True for video streaming payload entries (<c>Content/payload/{guid}{ext}</c>), which the
    /// build copies beside the cache assets exactly like the desktop pipeline's N6 distribution
    /// layout. Their bytes are covered by the payload hash manifest, so the closure here only
    /// validates the naming scheme, not the content.
    /// </summary>
    private static bool IsVideoPayloadEntry(string relative)
    {
        if (!relative.StartsWith("payload/", StringComparison.Ordinal))
            return false;
        string extension = Path.GetExtension(relative);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    }

    private static HarmonyOSPhysicalAssetPayload InspectLooseFiles(
        string payloadRoot,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        string content = Path.Combine(payloadRoot, "Content");
        var assets = new Dictionary<Guid, HarmonyOSPhysicalAssetEntry>();
        if (Directory.Exists(content))
        {
            string[] files = Directory.GetFiles(content, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string relative = Path.GetRelativePath(content, files[i]).Replace('\\', '/');
                if (string.Equals(relative, "asset_manifest.bin", StringComparison.Ordinal))
                    continue;
                if (IsVideoPayloadEntry(relative))
                    continue;
                if (relative.Contains('/') || !relative.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"HarmonyOS LooseFiles payload contains an unexpected file: Content/{relative}");
                }

                Guid guid = ParseAssetFileName(Path.GetFileName(files[i]));
                FileInfo file = new(files[i]);
                if (!assets.TryAdd(guid, new HarmonyOSPhysicalAssetEntry(file.Length, file.Length, "Content/" + Path.GetFileName(files[i]))))
                    throw new InvalidDataException($"HarmonyOS LooseFiles payload has duplicate asset {guid:N}.");
            }
        }

        RequireExactClosure(expectedAssets, assets, "LooseFiles");
        return new HarmonyOSPhysicalAssetPayload(true, "exact-loose-files-closure", assets, BuildContainers(assets));
    }

    private static HarmonyOSPhysicalAssetPayload InspectPaks(
        string payloadRoot,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        string content = Path.Combine(payloadRoot, "Content");
        var assets = new Dictionary<Guid, HarmonyOSPhysicalAssetEntry>();
        var containers = new List<string>();
        if (Directory.Exists(content))
        {
            string[] contentFiles = Directory.GetFiles(content, "*", SearchOption.AllDirectories);
            Array.Sort(contentFiles, StringComparer.Ordinal);
            for (int fileIndex = 0; fileIndex < contentFiles.Length; fileIndex++)
            {
                string relative = Path.GetRelativePath(content, contentFiles[fileIndex]).Replace('\\', '/');
                if (string.Equals(relative, "asset_manifest.bin", StringComparison.Ordinal))
                    continue;
                if (IsVideoPayloadEntry(relative))
                    continue;
                if (relative.Contains('/') || !relative.EndsWith(".xenginepak", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"HarmonyOS XEnginePak payload contains an unexpected file: Content/{relative}");
                }

                string container = "Content/" + Path.GetFileName(contentFiles[fileIndex]);
                containers.Add(container);
                using ZipArchive pak = ZipFile.OpenRead(contentFiles[fileIndex]);
                for (int entryIndex = 0; entryIndex < pak.Entries.Count; entryIndex++)
                {
                    ZipArchiveEntry entry = pak.Entries[entryIndex];
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        throw new InvalidDataException($"HarmonyOS XEnginePak contains a directory entry: {container}!/{entry.FullName}");
                    if (entry.FullName.Contains('/') || !entry.FullName.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"HarmonyOS XEnginePak contains an unexpected payload entry: {container}!/{entry.FullName}");
                    }

                    Guid guid = ParseAssetFileName(entry.FullName);
                    if (!assets.TryAdd(guid, new HarmonyOSPhysicalAssetEntry(entry.Length, entry.CompressedLength, container)))
                        throw new InvalidDataException($"HarmonyOS XEnginePak payload has duplicate asset {guid:N}.");
                }
            }
        }

        RequireExactClosure(expectedAssets, assets, "XEnginePak");
        return new HarmonyOSPhysicalAssetPayload(true, "exact-xenginepak-closure", assets, containers);
    }

    private static HarmonyOSPhysicalAssetPayload InspectAssetBundles(
        string payloadRoot,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        string content = Path.Combine(payloadRoot, "Content");
        var assets = new Dictionary<Guid, HarmonyOSPhysicalAssetEntry>();
        var containers = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(content))
        {
            string[] entries = Directory.GetFileSystemEntries(content, "*", SearchOption.TopDirectoryOnly);
            Array.Sort(entries, StringComparer.Ordinal);
            for (int i = 0; i < entries.Length; i++)
            {
                string entry = entries[i];
                if (File.Exists(entry))
                {
                    if (!string.Equals(Path.GetFileName(entry), "asset_manifest.bin", StringComparison.Ordinal))
                        throw new InvalidDataException($"HarmonyOS AssetBundle payload contains an unexpected file: Content/{Path.GetFileName(entry)}");
                    continue;
                }
                if (!Directory.Exists(entry))
                    throw new InvalidDataException($"HarmonyOS AssetBundle payload contains an invalid entry: {entry}");
                if (string.Equals(Path.GetFileName(entry), "payload", StringComparison.Ordinal))
                    continue; // video streaming payloads, hash-manifest covered (see IsVideoPayloadEntry)
                InspectAssetBundlePackage(content, entry, assets, containers);
            }
        }

        RequireExactClosure(expectedAssets, assets, "AssetBundle");
        return new HarmonyOSPhysicalAssetPayload(true, "exact-assetbundle-closure", assets, [.. containers]);
    }

    private static void InspectAssetBundlePackage(
        string contentDirectory,
        string packageDirectory,
        Dictionary<Guid, HarmonyOSPhysicalAssetEntry> assets,
        SortedSet<string> containers)
    {
        string packageName = Path.GetFileName(packageDirectory);
        if (!IsSafeFileName(packageName))
            throw new InvalidDataException($"HarmonyOS AssetBundle payload has an unsafe package directory: {packageName}");

        string[] nestedDirectories = Directory.GetDirectories(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        if (nestedDirectories.Length != 0)
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' contains nested payload directories.");

        string[] files = Directory.GetFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var manifestFiles = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetExtension(files[i]).Equals(".bytes", StringComparison.OrdinalIgnoreCase))
                manifestFiles.Add(files[i]);
        }
        if (manifestFiles.Count != 1)
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' must contain exactly one package manifest.");

        AssetBundlePackageManifest? manifest = PackageManifestSerializer.DeserializeFromFile(manifestFiles[0]);
        if (manifest == null)
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' has an unsupported manifest.");
        if (!string.Equals(manifest.PackageName, packageName, StringComparison.Ordinal) || !IsSafeFileName(manifest.PackageVersion))
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' has invalid manifest identity.");
        if (manifest.BuildTarget != AssetBundlePlatform.HarmonyOS)
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' targets {manifest.BuildTarget}, not HarmonyOS.");

        string manifestFileName = PackageFileNames.ManifestFile(packageName, manifest.PackageVersion);
        if (!string.Equals(Path.GetFileName(manifestFiles[0]), manifestFileName, StringComparison.Ordinal))
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' manifest file does not match its identity.");

        string versionFileName = PackageFileNames.VersionFile(packageName);
        string versionPath = Path.Combine(packageDirectory, versionFileName);
        if (!File.Exists(versionPath) || !string.Equals(File.ReadAllText(versionPath).Trim(), manifest.PackageVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' version file does not match its manifest.");

        string hashFileName = PackageFileNames.HashFile(packageName, manifest.PackageVersion);
        string hashPath = Path.Combine(packageDirectory, hashFileName);
        if (!File.Exists(hashPath))
            throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' is missing its hash file.");
        using (var hashInput = new MemoryStream(checked(manifest.BundleList.Count * 16)))
        {
            for (int i = 0; i < manifest.BundleList.Count; i++)
            {
                byte[] hash = manifest.BundleList[i].FileHash.ToByteArray();
                hashInput.Write(hash, 0, hash.Length);
            }
            string expectedHash = BundleHash.Compute(hashInput.ToArray()).ToString();
            if (!string.Equals(File.ReadAllText(hashPath).Trim(), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' hash file does not match its manifest.");
        }

        var assetsByBundle = new List<HashSet<Guid>>(manifest.BundleList.Count);
        for (int i = 0; i < manifest.BundleList.Count; i++)
            assetsByBundle.Add(new HashSet<Guid>());
        var manifestAssets = new HashSet<Guid>();
        for (int i = 0; i < manifest.AssetList.Count; i++)
        {
            ManifestAsset asset = manifest.AssetList[i];
            if (asset.AssetGuid == Guid.Empty || asset.BundleId < 0 || asset.BundleId >= assetsByBundle.Count)
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' has an invalid asset manifest entry.");
            if (!manifestAssets.Add(asset.AssetGuid))
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' manifest has duplicate asset {asset.AssetGuid:N}.");
            assetsByBundle[asset.BundleId].Add(asset.AssetGuid);
        }

        var allowedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            manifestFileName,
            versionFileName,
            hashFileName,
            PackageFileNames.ReportFile(packageName, manifest.PackageVersion),
        };
        for (int bundleIndex = 0; bundleIndex < manifest.BundleList.Count; bundleIndex++)
        {
            BundleInfo bundleInfo = manifest.BundleList[bundleIndex];
            ValidateAssetBundleDependencies(packageName, bundleInfo, manifest.BundleList.Count);
            if (!IsSafeFileName(bundleInfo.FileName) || !bundleInfo.FileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || !allowedFiles.Add(bundleInfo.FileName))
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' has an unsafe or duplicate bundle file name.");

            string bundlePath = Path.Combine(packageDirectory, bundleInfo.FileName);
            if (!File.Exists(bundlePath))
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' is missing bundle '{bundleInfo.FileName}'.");
            FileInfo file = new(bundlePath);
            if (file.Length != bundleInfo.FileSize || BundleHash.ComputeFile(bundlePath) != bundleInfo.FileHash || Crc32Algorithm.ComputeFile(bundlePath) != bundleInfo.FileCrc)
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' bundle '{bundleInfo.FileName}' metadata does not match its file.");

            string container = Path.GetRelativePath(contentDirectory, bundlePath).Replace('\\', '/');
            using var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle.Platform != AssetBundlePlatform.HarmonyOS || bundle.BundleHash != bundleInfo.BundleGuid)
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' bundle '{bundleInfo.FileName}' header does not match its manifest.");

            var actualAssets = new HashSet<Guid>();
            foreach (Guid guid in bundle.GetAllAssetGuids())
            {
                if (guid == Guid.Empty || !actualAssets.Add(guid) || !bundle.TryGetAssetPayloadSize(guid, out _))
                    throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' bundle '{bundleInfo.FileName}' has an invalid asset index.");
            }
            RequireExactAssetBundleIndex(packageName, bundleInfo.FileName, assetsByBundle[bundleIndex], actualAssets);

            foreach (Guid guid in actualAssets)
            {
                if (!bundle.TryGetAssetPayloadSize(guid, out long byteLength) || !assets.TryAdd(guid, new HarmonyOSPhysicalAssetEntry(byteLength, null, "Content/" + container)))
                    throw new InvalidDataException($"HarmonyOS AssetBundle payload has duplicate asset {guid:N}.");
            }
            containers.Add("Content/" + container);
        }

        for (int i = 0; i < files.Length; i++)
        {
            string fileName = Path.GetFileName(files[i]);
            if (!allowedFiles.Contains(fileName))
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' contains an unexpected file: {fileName}");
        }
    }

    private static void ValidateAssetBundleDependencies(string packageName, BundleInfo bundle, int bundleCount)
    {
        var dependencies = new HashSet<int>();
        for (int i = 0; i < bundle.DependentBundleIds.Length; i++)
        {
            int dependency = bundle.DependentBundleIds[i];
            if (dependency < 0 || dependency >= bundleCount || !dependencies.Add(dependency))
                throw new InvalidDataException($"HarmonyOS AssetBundle package '{packageName}' has an invalid bundle dependency.");
        }
    }

    private static void RequireExactAssetBundleIndex(string packageName, string fileName, IReadOnlySet<Guid> expectedAssets, IReadOnlySet<Guid> actualAssets)
    {
        var missing = new List<Guid>();
        foreach (Guid guid in expectedAssets)
        {
            if (!actualAssets.Contains(guid))
                missing.Add(guid);
        }
        var unexpected = new List<Guid>();
        foreach (Guid guid in actualAssets)
        {
            if (!expectedAssets.Contains(guid))
                unexpected.Add(guid);
        }
        if (missing.Count == 0 && unexpected.Count == 0)
            return;
        missing.Sort(CompareGuid);
        unexpected.Sort(CompareGuid);
        throw new InvalidDataException(
            $"HarmonyOS AssetBundle package '{packageName}' bundle '{fileName}' does not match manifest index. " +
            $"Missing: {FormatGuids(missing)}. Unexpected: {FormatGuids(unexpected)}.");
    }

    private static bool IsSafeFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "." && value != ".." &&
        !value.Contains('/') && !value.Contains('\\') &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);

    private static Guid ParseAssetFileName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (!Guid.TryParse(name, out Guid guid))
            throw new InvalidDataException($"HarmonyOS asset payload has an invalid asset name: {fileName}");
        return guid;
    }

    private static void RequireExactClosure(IReadOnlyCollection<Guid> expectedAssets, IReadOnlyDictionary<Guid, HarmonyOSPhysicalAssetEntry> actualAssets, string mode)
    {
        var expected = new HashSet<Guid>(expectedAssets);
        var missing = new List<Guid>();
        foreach (Guid guid in expected)
        {
            if (!actualAssets.ContainsKey(guid))
                missing.Add(guid);
        }
        var unexpected = new List<Guid>();
        foreach (Guid guid in actualAssets.Keys)
        {
            if (!expected.Contains(guid))
                unexpected.Add(guid);
        }
        if (missing.Count == 0 && unexpected.Count == 0)
            return;
        missing.Sort(CompareGuid);
        unexpected.Sort(CompareGuid);
        throw new InvalidDataException(
            $"HarmonyOS {mode} payload does not match the collected asset closure. " +
            $"Missing: {FormatGuids(missing)}. Unexpected: {FormatGuids(unexpected)}.");
    }

    private static IReadOnlyList<string> BuildContainers(IReadOnlyDictionary<Guid, HarmonyOSPhysicalAssetEntry> assets)
    {
        var containers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (HarmonyOSPhysicalAssetEntry entry in assets.Values)
            containers.Add(entry.Container);
        return [.. containers];
    }

    private static void RequireExactStringClosure(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual,
        string description)
    {
        var missing = new List<string>();
        foreach (string value in expected)
        {
            if (!actual.Contains(value))
                missing.Add(value);
        }
        var unexpected = new List<string>();
        foreach (string value in actual)
        {
            if (!expected.Contains(value))
                unexpected.Add(value);
        }
        if (missing.Count == 0 && unexpected.Count == 0)
            return;

        missing.Sort(StringComparer.Ordinal);
        unexpected.Sort(StringComparer.Ordinal);
        throw new InvalidDataException(
            $"HarmonyOS {description} closure mismatch. Missing: {FormatStrings(missing)}. Unexpected: {FormatStrings(unexpected)}.");
    }

    private static string NormalizePayloadPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] == '/' || value.Contains('\\') || Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"HarmonyOS payload path is invalid: {value}");
        string[] segments = value.Split('/', StringSplitOptions.None);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0 || segments[i] is "." or "..")
                throw new InvalidDataException($"HarmonyOS payload path is invalid: {value}");
        }
        return value;
    }

    private static string NormalizeShaderArtifactPath(string value)
    {
        string path = NormalizePayloadPath(value);
        if (path.Contains('/', StringComparison.Ordinal))
            throw new InvalidDataException($"HarmonyOS shader artifact must be directly under Shaders: {value}");
        if (!path.EndsWith(".xshader", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".spv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"HarmonyOS shader artifact must be .xshader or .spv: {value}");
        }
        return path;
    }

    private static string RequireSha256(string value)
    {
        if (value.Length != 64)
            throw new InvalidDataException("HarmonyOS payload hashes must be 64-character SHA-256 values.");
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                throw new InvalidDataException("HarmonyOS payload hashes must use lowercase hexadecimal.");
        }
        return value;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        string normalized = NormalizePayloadPath(relativePath);
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"HarmonyOS payload path escapes its root: {relativePath}");
        return path;
    }

    private static bool IsShaderSourceExtension(string extension) =>
        extension.Equals(".shader", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hlsl", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hlsli", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".glsl", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".slang", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cginc", StringComparison.OrdinalIgnoreCase);

    private static string FormatStrings(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    private sealed record PayloadManifestFile(string Path, string Sha256, long Bytes);

    private static string FormatGuids(IReadOnlyList<Guid> guids)
    {
        if (guids.Count == 0)
            return "none";
        var values = new string[guids.Count];
        for (int i = 0; i < guids.Count; i++)
            values[i] = guids[i].ToString("N");
        return string.Join(',', values);
    }

    private static int CompareGuid(Guid left, Guid right) =>
        StringComparer.Ordinal.Compare(left.ToString("N"), right.ToString("N"));
}
