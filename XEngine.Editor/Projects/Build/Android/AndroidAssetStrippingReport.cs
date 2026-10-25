// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

using XEngine.Editor.Build;

namespace XEngine.Editor.Build.Android;

/// <summary>Writes the deterministic, auditable Android asset reachability report.</summary>
internal static class AndroidAssetStrippingReport
{
    internal const string FileName = "android-asset-stripping.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void Write(
        string path,
        string assetExportMode,
        XEngine.Runtime.AssetPackagingMode packagingMode,
        AssetCollector.CollectionResult collection,
        AndroidPhysicalAssetPayload physicalPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetExportMode);
        ArgumentNullException.ThrowIfNull(physicalPayload);

        var candidates = CollectCandidates();
        var reportAssets = new List<object>(candidates.Count);
        int retained = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            AssetCandidate candidate = candidates[i];
            bool isRetained = collection.AllAssets.Contains(candidate.Guid);
            if (isRetained)
                retained++;
            collection.RetentionReasons.TryGetValue(candidate.Guid, out AssetCollector.AssetRetentionReason? reason);
            physicalPayload.Assets.TryGetValue(candidate.Guid, out AndroidPhysicalAssetEntry? physicalEntry);
            reportAssets.Add(new
            {
                guid = candidate.Guid.ToString("N"),
                assetPath = candidate.Path,
                importer = candidate.Importer,
                isSubAsset = candidate.IsSubAsset,
                retained = isRetained,
                retainedBy = isRetained ? reason?.RootKind ?? "collector-closure" : "unreachable",
                rootGuid = reason?.RootGuid.ToString("N"),
                reasonChain = CreateReasonChain(reason),
                serializedBytes = physicalEntry?.UncompressedBytes ?? 0,
                // AssetBundle payloads share compressed blocks, so the inspector deliberately
                // leaves per-asset compressed ownership unknown. Preserve null instead of
                // reporting a misleading zero-byte compressed payload.
                packagedBytes = physicalEntry?.CompressedBytes,
                packageContainer = physicalEntry?.Container,
            });
        }

        var document = new
        {
            schemaVersion = 1,
            assetExportMode,
            packagingMode = packagingMode.ToString(),
            knownAssetCount = candidates.Count,
            retainedAssetCount = retained,
            strippedAssetCount = candidates.Count - retained,
            physicalPayloadVerified = physicalPayload.IsExactClosure,
            physicalPayloadVerification = physicalPayload.Verification,
            physicalAssetCount = physicalPayload.Assets.Count,
            physicalContainers = physicalPayload.Containers,
            assets = reportAssets,
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
            Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(document, s_jsonOptions);
        File.WriteAllText(path, json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n");
    }

    private static List<object> CreateReasonChain(AssetCollector.AssetRetentionReason? reason)
    {
        if (reason == null || reason.Chain.Count == 0)
            return [];
        var chain = new List<object>(reason.Chain.Count);
        for (int i = 0; i < reason.Chain.Count; i++)
        {
            AssetCollector.AssetRetentionEdge edge = reason.Chain[i];
            chain.Add(new
            {
                from = edge.From.ToString("N"),
                to = edge.To.ToString("N"),
                role = edge.Role,
            });
        }
        return chain;
    }

    private static List<AssetCandidate> CollectCandidates()
    {
        var result = new List<AssetCandidate>();
        EditorAssetBackend? database = EditorAssetBackend.Instance;
        if (database == null)
            return result;

        var known = new HashSet<Guid>();
        foreach (AssetEntry entry in database.GetAllEntries())
        {
            if (known.Add(entry.Guid))
                result.Add(new AssetCandidate(entry.Guid, entry.Path, entry.ImporterType, false));
            if (entry.SubAssets == null)
                continue;
            for (int subIndex = 0; subIndex < entry.SubAssets.Length; subIndex++)
            {
                SubAssetEntry subAsset = entry.SubAssets[subIndex];
                if (known.Add(subAsset.Guid))
                {
                    result.Add(new AssetCandidate(
                        subAsset.Guid,
                        entry.Path + "#" + subAsset.Name,
                        subAsset.TypeName,
                        true));
                }
            }
        }

        result.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Guid.ToString("N"), right.Guid.ToString("N")));
        return result;
    }

    private sealed record AssetCandidate(Guid Guid, string Path, string Importer, bool IsSubAsset);
}
