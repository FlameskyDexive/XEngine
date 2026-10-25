// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

using XEngine.Runtime;
using XEngine.Runtime.AssetBundles;

using AssetBundlePackageManifest = XEngine.Runtime.AssetBundles.PackageManifest;

namespace XEngine.Editor.Build.Android;

/// <summary>Physical asset entries found in an Android player payload.</summary>
internal sealed record AndroidPhysicalAssetEntry(
    long UncompressedBytes,
    long? CompressedBytes,
    string Container);

/// <summary>Result of comparing a generated Android payload with the collected asset closure.</summary>
internal sealed record AndroidPhysicalAssetPayload(
    bool IsExactClosure,
    string Verification,
    IReadOnlyDictionary<Guid, AndroidPhysicalAssetEntry> Assets,
    IReadOnlyList<string> Containers);

/// <summary>
/// Verifies that the physical Android payload is exactly the collected closure.
/// </summary>
internal static class AndroidAssetPayloadInspector
{
    internal static AndroidPhysicalAssetPayload Inspect(
        string payloadRoot,
        AssetPackagingMode packagingMode,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRoot);
        ArgumentNullException.ThrowIfNull(expectedAssets);

        // Built-in assets are embedded in XEngine.Runtime and AssetDatabase resolves them before
        // consulting the player backend. They remain part of the logical reachability closure, but
        // never belong in a LooseFiles/pak/bundle payload.
        BuiltInAssets.Initialize();
        var expectedPhysicalAssets = new HashSet<Guid>();
        foreach (Guid guid in expectedAssets)
        {
            if (!BuiltInAssets.IsBuiltIn(guid))
                expectedPhysicalAssets.Add(guid);
        }

        return packagingMode switch
        {
            AssetPackagingMode.LooseFiles => InspectLooseFiles(payloadRoot, expectedPhysicalAssets),
            AssetPackagingMode.XEnginePak => InspectPaks(payloadRoot, expectedPhysicalAssets),
            AssetPackagingMode.AssetBundle => InspectAssetBundles(payloadRoot, expectedPhysicalAssets),
            _ => throw new InvalidOperationException(
                $"Android physical asset inspection does not support {packagingMode}."),
        };
    }

    private static AndroidPhysicalAssetPayload InspectLooseFiles(
        string payloadRoot,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        string content = Path.Combine(payloadRoot, "Content");
        var assets = new Dictionary<Guid, AndroidPhysicalAssetEntry>();
        if (Directory.Exists(content))
        {
            string[] files = Directory.GetFiles(content, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                string relative = Path.GetRelativePath(content, files[i]).Replace('\\', '/');
                if (string.Equals(relative, "asset_manifest.bin", StringComparison.Ordinal))
                    continue;
                if (relative.Contains('/') || !relative.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Android LooseFiles payload contains an unexpected file: Content/{relative}");
                }
                Guid guid = ParseAssetFileName(Path.GetFileName(files[i]));
                if (!assets.TryAdd(guid, new AndroidPhysicalAssetEntry(
                        new FileInfo(files[i]).Length,
                        new FileInfo(files[i]).Length,
                        "Content/" + Path.GetFileName(files[i]))))
                {
                    throw new InvalidDataException($"Android LooseFiles payload has duplicate asset {guid:N}.");
                }
            }
        }

        RequireExactClosure(expectedAssets, assets, "LooseFiles");
        return new AndroidPhysicalAssetPayload(
            true,
            "exact-loose-files-closure",
            assets,
            BuildContainers(assets));
    }

    private static AndroidPhysicalAssetPayload InspectPaks(
        string payloadRoot,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        string content = Path.Combine(payloadRoot, "Content");
        var assets = new Dictionary<Guid, AndroidPhysicalAssetEntry>();
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
                if (relative.Contains('/') || !relative.EndsWith(".xenginepak", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Android XEnginePak payload contains an unexpected file: Content/{relative}");
                }

                string container = "Content/" + Path.GetFileName(contentFiles[fileIndex]);
                containers.Add(container);
                using ZipArchive pak = ZipFile.OpenRead(contentFiles[fileIndex]);
                for (int entryIndex = 0; entryIndex < pak.Entries.Count; entryIndex++)
                {
                    ZipArchiveEntry entry = pak.Entries[entryIndex];
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        throw new InvalidDataException($"Android XEnginePak contains a directory entry: {container}!/{entry.FullName}");
                    if (entry.FullName.Contains('/') || !entry.FullName.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Android XEnginePak contains an unexpected payload entry: {container}!/{entry.FullName}");
                    }

                    Guid guid = ParseAssetFileName(entry.FullName);
                    if (!assets.TryAdd(guid, new AndroidPhysicalAssetEntry(
                            entry.Length,
                            entry.CompressedLength,
                            container)))
                    {
                        throw new InvalidDataException($"Android XEnginePak payload has duplicate asset {guid:N}.");
                    }
                }
            }
        }

        RequireExactClosure(expectedAssets, assets, "XEnginePak");
        return new AndroidPhysicalAssetPayload(
            true,
            "exact-xenginepak-closure",
            assets,
            containers);
    }

    private static AndroidPhysicalAssetPayload InspectAssetBundles(
        string payloadRoot,
        IReadOnlyCollection<Guid> expectedAssets)
    {
        string content = Path.Combine(payloadRoot, "Content");
        var assets = new Dictionary<Guid, AndroidPhysicalAssetEntry>();
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
                    {
                        throw new InvalidDataException(
                            $"Android AssetBundle payload contains an unexpected file: Content/{Path.GetFileName(entry)}");
                    }
                    continue;
                }

                if (!Directory.Exists(entry))
                    throw new InvalidDataException($"Android AssetBundle payload contains an invalid entry: {entry}");

                InspectAssetBundlePackage(content, entry, assets, containers);
            }
        }

        RequireExactClosure(expectedAssets, assets, "AssetBundle");
        return new AndroidPhysicalAssetPayload(
            true,
            "exact-assetbundle-closure",
            assets,
            [.. containers]);
    }

    private static void InspectAssetBundlePackage(
        string contentDirectory,
        string packageDirectory,
        Dictionary<Guid, AndroidPhysicalAssetEntry> assets,
        SortedSet<string> containers)
    {
        string packageName = Path.GetFileName(packageDirectory);
        if (!IsSafeFileName(packageName))
            throw new InvalidDataException($"Android AssetBundle payload has an unsafe package directory: {packageName}");

        string[] nestedDirectories = Directory.GetDirectories(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        if (nestedDirectories.Length != 0)
        {
            throw new InvalidDataException(
                $"Android AssetBundle package '{packageName}' contains nested payload directories.");
        }

        string[] files = Directory.GetFiles(packageDirectory, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var manifestFiles = new List<string>();
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetExtension(files[i]).Equals(".bytes", StringComparison.OrdinalIgnoreCase))
                manifestFiles.Add(files[i]);
        }
        if (manifestFiles.Count != 1)
        {
            throw new InvalidDataException(
                $"Android AssetBundle package '{packageName}' must contain exactly one package manifest.");
        }

        AssetBundlePackageManifest? manifest = PackageManifestSerializer.DeserializeFromFile(manifestFiles[0]);
        if (manifest == null)
            throw new InvalidDataException($"Android AssetBundle package '{packageName}' has an unsupported manifest.");
        if (!string.Equals(manifest.PackageName, packageName, StringComparison.Ordinal) ||
            !IsSafeFileName(manifest.PackageVersion))
        {
            throw new InvalidDataException($"Android AssetBundle package '{packageName}' has invalid manifest identity.");
        }
        if (manifest.BuildTarget != AssetBundlePlatform.Android)
        {
            throw new InvalidDataException(
                $"Android AssetBundle package '{packageName}' targets {manifest.BuildTarget}, not Android.");
        }

        string manifestFileName = PackageFileNames.ManifestFile(packageName, manifest.PackageVersion);
        if (!string.Equals(Path.GetFileName(manifestFiles[0]), manifestFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Android AssetBundle package '{packageName}' manifest file does not match its identity.");
        }

        string versionFileName = PackageFileNames.VersionFile(packageName);
        string versionPath = Path.Combine(packageDirectory, versionFileName);
        if (!File.Exists(versionPath) ||
            !string.Equals(File.ReadAllText(versionPath).Trim(), manifest.PackageVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Android AssetBundle package '{packageName}' version file does not match its manifest.");
        }

        string hashFileName = PackageFileNames.HashFile(packageName, manifest.PackageVersion);
        string hashPath = Path.Combine(packageDirectory, hashFileName);
        if (!File.Exists(hashPath))
            throw new InvalidDataException($"Android AssetBundle package '{packageName}' is missing its hash file.");
        using (var hashInput = new MemoryStream(checked(manifest.BundleList.Count * 16)))
        {
            for (int i = 0; i < manifest.BundleList.Count; i++)
            {
                byte[] hash = manifest.BundleList[i].FileHash.ToByteArray();
                hashInput.Write(hash, 0, hash.Length);
            }
            string expectedHash = BundleHash.Compute(hashInput.ToArray()).ToString();
            if (!string.Equals(File.ReadAllText(hashPath).Trim(), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' hash file does not match its manifest.");
            }
        }

        var assetsByBundle = new List<HashSet<Guid>>(manifest.BundleList.Count);
        for (int i = 0; i < manifest.BundleList.Count; i++)
            assetsByBundle.Add(new HashSet<Guid>());
        var manifestAssets = new HashSet<Guid>();
        for (int i = 0; i < manifest.AssetList.Count; i++)
        {
            ManifestAsset asset = manifest.AssetList[i];
            if (asset.AssetGuid == Guid.Empty || asset.BundleId < 0 || asset.BundleId >= assetsByBundle.Count)
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' has an invalid asset manifest entry.");
            }
            if (!manifestAssets.Add(asset.AssetGuid))
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' manifest has duplicate asset {asset.AssetGuid:N}.");
            }
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
            if (!IsSafeFileName(bundleInfo.FileName) ||
                !bundleInfo.FileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ||
                !allowedFiles.Add(bundleInfo.FileName))
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' has an unsafe or duplicate bundle file name.");
            }

            string bundlePath = Path.Combine(packageDirectory, bundleInfo.FileName);
            if (!File.Exists(bundlePath))
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' is missing bundle '{bundleInfo.FileName}'.");
            }
            FileInfo file = new(bundlePath);
            if (file.Length != bundleInfo.FileSize || BundleHash.ComputeFile(bundlePath) != bundleInfo.FileHash ||
                Crc32Algorithm.ComputeFile(bundlePath) != bundleInfo.FileCrc)
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' bundle '{bundleInfo.FileName}' metadata does not match its file.");
            }

            string container = Path.GetRelativePath(contentDirectory, bundlePath).Replace('\\', '/');
            using var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle.Platform != AssetBundlePlatform.Android || bundle.BundleHash != bundleInfo.BundleGuid)
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' bundle '{bundleInfo.FileName}' header does not match its manifest.");
            }

            var actualAssets = new HashSet<Guid>();
            foreach (Guid guid in bundle.GetAllAssetGuids())
            {
                if (guid == Guid.Empty || !actualAssets.Add(guid) || !bundle.TryGetAssetPayloadSize(guid, out long byteLength))
                {
                    throw new InvalidDataException(
                        $"Android AssetBundle package '{packageName}' bundle '{bundleInfo.FileName}' has an invalid asset index.");
                }
            }
            RequireExactAssetBundleIndex(packageName, bundleInfo.FileName, assetsByBundle[bundleIndex], actualAssets);

            foreach (Guid guid in actualAssets)
            {
                if (!bundle.TryGetAssetPayloadSize(guid, out long byteLength) ||
                    !assets.TryAdd(guid, new AndroidPhysicalAssetEntry(byteLength, null, "Content/" + container)))
                {
                    throw new InvalidDataException($"Android AssetBundle payload has duplicate asset {guid:N}.");
                }
            }
            containers.Add("Content/" + container);
        }

        for (int i = 0; i < files.Length; i++)
        {
            string fileName = Path.GetFileName(files[i]);
            if (!allowedFiles.Contains(fileName))
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' contains an unexpected file: {fileName}");
            }
        }
    }

    private static void ValidateAssetBundleDependencies(string packageName, BundleInfo bundle, int bundleCount)
    {
        var dependencies = new HashSet<int>();
        for (int i = 0; i < bundle.DependentBundleIds.Length; i++)
        {
            int dependency = bundle.DependentBundleIds[i];
            if (dependency < 0 || dependency >= bundleCount || !dependencies.Add(dependency))
            {
                throw new InvalidDataException(
                    $"Android AssetBundle package '{packageName}' has an invalid bundle dependency.");
            }
        }
    }

    private static void RequireExactAssetBundleIndex(
        string packageName,
        string fileName,
        IReadOnlySet<Guid> expectedAssets,
        IReadOnlySet<Guid> actualAssets)
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
            $"Android AssetBundle package '{packageName}' bundle '{fileName}' does not match manifest index. " +
            $"Missing: {FormatGuids(missing)}. Unexpected: {FormatGuids(unexpected)}.");
    }

    private static bool IsSafeFileName(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value != "." && value != ".." &&
            !value.Contains('/') && !value.Contains('\\') &&
            string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
    }

    private static Guid ParseAssetFileName(string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (!Guid.TryParse(name, out Guid guid))
            throw new InvalidDataException($"Android asset payload has an invalid asset name: {fileName}");
        return guid;
    }

    private static void RequireExactClosure(
        IReadOnlyCollection<Guid> expectedAssets,
        IReadOnlyDictionary<Guid, AndroidPhysicalAssetEntry> actualAssets,
        string mode)
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
            $"Android {mode} payload does not match the collected asset closure. " +
            $"Missing: {FormatGuids(missing)}. Unexpected: {FormatGuids(unexpected)}.");
    }

    private static IReadOnlyList<string> BuildContainers(
        IReadOnlyDictionary<Guid, AndroidPhysicalAssetEntry> assets)
    {
        var containers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (AndroidPhysicalAssetEntry entry in assets.Values)
            containers.Add(entry.Container);
        return [.. containers];
    }

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
