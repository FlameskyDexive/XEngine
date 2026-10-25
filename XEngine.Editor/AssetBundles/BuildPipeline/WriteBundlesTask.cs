// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Projects;
using XEngine.Runtime.AssetBundles;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>
/// Phase 3: write each bundle's assets into an XEBN container file under {OutputRoot}/{PackageName}/.
/// Payloads are read from the existing Library/cache/{guid}.asset Echo blobs (no re-import). Output:
/// <see cref="BundleWriteData"/> (per-bundle BundleInfo with hash/CRC/size) in the context.
/// </summary>
public sealed class WriteBundlesTask : IBuildTask
{
    public int Version => 1;
    public string Name => "WriteBundles";

    public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
    {
        var parameters = context.Get<BundleBuildParameters>();
        var collection = context.Get<BundleBuildCollection>();
        var deps = context.Get<BundleDependencyData>();
        var content = context.Get<BundleBuildContent>();
        var project = Project.Current;
        if (project == null) { Runtime.Debug.LogError("[WriteBundlesTask] No project open."); return BuildReturnCode.Error; }

        if (string.IsNullOrEmpty(parameters.OutputRoot)) return BuildReturnCode.Error;

        // Fail before creating or partially writing the package. A selected asset without its
        // importer cache would otherwise be omitted from the physical bundle while still being
        // advertised by the manifest and build summary. Built-in engine assets are never roots in
        // this collection and therefore remain legitimately outside this cache check.
        var missingCaches = new List<Guid>();
        for (int b = 0; b < collection.BundleAssets.Count; b++)
        {
            foreach (Guid guid in collection.BundleAssets[b])
            {
                string cachePath = Path.Combine(project.CachePath, $"{guid}.asset");
                if (!File.Exists(cachePath)) missingCaches.Add(guid);
            }
        }

        if (missingCaches.Count > 0)
        {
            string preview = string.Join(", ", missingCaches.GetRange(0, Math.Min(8, missingCaches.Count)));
            if (missingCaches.Count > 8) preview += $", … ({missingCaches.Count} total)";
            Runtime.Debug.LogError($"[WriteBundlesTask] Missing cache for selected asset(s): {preview}.");
            return BuildReturnCode.Error;
        }

        string pkgDir = Path.Combine(parameters.OutputRoot, parameters.PackageName);
        Directory.CreateDirectory(pkgDir);

        var writeData = new BundleWriteData();
        var warnings = new List<string>();

        for (int b = 0; b < collection.BundleNames.Count; b++)
        {
            var entries = new List<KeyValuePair<AssetBundleEntry, byte[]>>();
            foreach (var guid in collection.BundleAssets[b])
            {
                string cachePath = Path.Combine(project.CachePath, $"{guid}.asset");
                // The preflight above makes this a defensive race check. Do not emit a manifest
                // entry for an asset that disappeared between preflight and the read.
                if (!File.Exists(cachePath))
                {
                    Runtime.Debug.LogError($"[WriteBundlesTask] Cache disappeared for selected asset {guid}.");
                    return BuildReturnCode.Error;
                }
                byte[] payload = File.ReadAllBytes(cachePath);
                if (parameters.PayloadTransform != null)
                    payload = parameters.PayloadTransform(guid, payload);
                string address = collection.AssetAddress.TryGetValue(guid, out var a) ? a : guid.ToString();
                entries.Add(new KeyValuePair<AssetBundleEntry, byte[]>(
                    new AssetBundleEntry(guid, address, typeName: "", 0, payload.Length), payload));
            }

            string bundleFile = collection.BundleNames[b] + ".bundle";
            string bundlePath = Path.Combine(pkgDir, bundleFile);
            AssetBundleWriter.Write(bundlePath, entries, new XEngine.Runtime.AssetBundles.AssetBundleBuildOptions
            {
                Platform = parameters.Platform,
                Compression = parameters.Compression,
                Lz4BlockSize = parameters.Lz4BlockSize,
            });

            var headerHash = ReadBundleHeaderHash(bundlePath);
            // BundleGuid = the header hash (the cache key stores read from each file's header, so they
            // match). FileHash = the WHOLE-FILE content hash, which is what SandboxBundleStore verifies
            // after a download. Keeping these distinct matches YooAsset/Unity (FileHash = full-file CRC
            // or hash; the bundle's identity is its content GUID).
            var fileHash = BundleHash.ComputeFile(bundlePath);
            var info = new BundleInfo
            {
                BundleName = collection.BundleNames[b],
                FileName = bundleFile,
                BundleGuid = headerHash,
                FileHash = fileHash,
                FileCrc = Crc32Algorithm.ComputeFile(bundlePath),
                FileSize = new FileInfo(bundlePath).Length,
                DependentBundleIds = SortedIntArray(deps.DirectDependencies[b]),
                IsEncrypted = false,
            };
            writeData.BundleInfos.Add(info);

            progress?.Report(new BuildTaskProgress(Name, (b + 1f) / collection.BundleNames.Count, bundleFile));
        }

        // Stash warnings into the results summary (created by WriteManifestTask, but we cache for it).
        context.Set(new WriteBundlesWarnings(warnings));
        context.Set(writeData);
        return BuildReturnCode.Success;
    }

    /// <summary>Carrier so WriteManifest can surface bundle-write warnings without rebuilding data.</summary>
    public sealed class WriteBundlesWarnings : IBuildContextObject
    {
        public List<string> Warnings;
        public WriteBundlesWarnings(List<string> warnings) { Warnings = warnings; }
    }

    private static int[] SortedIntArray(System.Collections.Generic.HashSet<int> set)
    {
        var arr = new int[set.Count];
        set.CopyTo(arr);
        Array.Sort(arr);
        return arr;
    }

    private static BundleHash ReadBundleHeaderHash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs);
        br.ReadUInt32(); br.ReadUInt16(); br.ReadByte(); br.ReadByte();
        br.ReadInt32(); br.ReadUInt32(); br.ReadUInt32();
        br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64();
        return BundleHash.ReadBigEndian(br.ReadBytes(16));
    }
}
