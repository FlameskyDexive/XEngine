// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime.AssetBundles;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>
/// Phase 4: assemble the PackageManifest from the collection + write data + dependency data, write it
/// to disk (manifest .bytes + .version + .hash + optional .report), and populate
/// <see cref="BundleBuildResults"/>. This is the pipeline's terminal task.
/// </summary>
public sealed class WriteManifestTask : IBuildTask
{
    public int Version => 1;
    public string Name => "WriteManifest";

    public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
    {
        var parameters = context.Get<BundleBuildParameters>();
        var collection = context.Get<BundleBuildCollection>();
        var writeData = context.Get<BundleWriteData>();
        var content = context.Get<BundleBuildContent>();
        var db = content.AssetDatabase;

        var manifest = new XEngine.Runtime.AssetBundles.PackageManifest
        {
            PackageName = parameters.PackageName,
            PackageVersion = parameters.Version,
            BuildTarget = parameters.Platform,
            EnableAddressable = parameters.EnableAddressable,
            Compression = parameters.Compression,
            OutputNameStyle = ManifestFileNameStyle.HashName,
        };
        manifest.BundleList.AddRange(writeData.BundleInfos);

        long totalBytes = 0;
        foreach (var bi in writeData.BundleInfos) totalBytes += bi.FileSize;

        foreach (var kv in collection.AssetToBundle)
        {
            var guid = kv.Key;
            var bundleId = kv.Value;
            string address = collection.AssetAddress.TryGetValue(guid, out var a) ? a : guid.ToString();
            string assetPath = collection.AssetPath.TryGetValue(guid, out var p) ? p : guid.ToString();
            var tags = collection.AssetTags.TryGetValue(guid, out var t) ? t.ToArray() : System.Array.Empty<string>();
            manifest.AssetList.Add(new ManifestAsset
            {
                Address = address,
                AssetPath = assetPath,
                AssetGuid = guid,
                BundleId = bundleId,
                Tags = tags,
            });
        }
        manifest.BuildIndexes();

        string pkgDir = Path.Combine(parameters.OutputRoot, parameters.PackageName);
        Directory.CreateDirectory(pkgDir);

        string manifestPath = Path.Combine(pkgDir, PackageFileNames.ManifestFile(parameters.PackageName, parameters.Version));
        PackageManifestSerializer.SerializeToFile(manifest, manifestPath);
        File.WriteAllText(Path.Combine(pkgDir, PackageFileNames.VersionFile(parameters.PackageName)), parameters.Version);

        // .hash: aggregate of bundle content hashes (the runtime's version-check fingerprint).
        using (var ms = new System.IO.MemoryStream())
        {
            foreach (var bi in writeData.BundleInfos) { var hb = bi.FileHash.ToByteArray(); ms.Write(hb, 0, hb.Length); }
            File.WriteAllText(Path.Combine(pkgDir, PackageFileNames.HashFile(parameters.PackageName, parameters.Version)),
                BundleHash.Compute(ms.ToArray()).ToString());
        }

        var results = new BundleBuildResults
        {
            Manifest = manifest,
            Summary =
            {
                PackageName = parameters.PackageName,
                Version = parameters.Version,
                BundleCount = writeData.BundleInfos.Count,
                AssetCount = manifest.AssetList.Count,
                AssetGuids = new System.Collections.Generic.HashSet<Guid>(collection.AssetToBundle.Keys),
                ExplicitRootAssetGuids = new System.Collections.Generic.HashSet<Guid>(collection.ExplicitRootAssets),
                TotalBytes = totalBytes,
            },
        };
        if (context.TryGet<WriteBundlesTask.WriteBundlesWarnings>(out var w))
            results.Summary.Warnings.AddRange(w.Warnings);
        if (context.TryGet<CollectTask.CollectTaskWarnings>(out var cw))
            results.Summary.Warnings.AddRange(cw.Warnings);

        if (parameters.WriteReport) WriteReport(pkgDir, parameters, results);

        context.Set(results);
        progress?.Report(new BuildTaskProgress(Name, 1f));
        return BuildReturnCode.Success;
    }

    private static void WriteReport(string dir, BundleBuildParameters p, BundleBuildResults results)
    {
        try
        {
            using var w = new StreamWriter(Path.Combine(dir, PackageFileNames.ReportFile(p.PackageName, p.Version)));
            w.WriteLine($"Package: {p.PackageName}");
            w.WriteLine($"Version: {p.Version}");
            w.WriteLine($"Platform: {p.Platform}");
            w.WriteLine($"Compression: {p.Compression}");
            w.WriteLine($"Bundles: {results.Summary.BundleCount}");
            w.WriteLine($"Assets: {results.Summary.AssetCount}");
            w.WriteLine($"TotalBytes: {results.Summary.TotalBytes}");
            foreach (var warn in results.Summary.Warnings) w.WriteLine($"WARN: {warn}");
        }
        catch { /* report is best-effort */ }
    }
}
