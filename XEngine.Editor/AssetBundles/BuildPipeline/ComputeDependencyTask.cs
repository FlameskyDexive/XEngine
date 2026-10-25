// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Linq;

using EditorAssetBackend = XEngine.Editor.EditorAssetBackend;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

/// <summary>
/// Phase 2: compute the direct bundle-dependency graph from the collection + the editor asset
/// database's per-asset dependency edges. For each bundle, any outgoing asset dependency that lands
/// in a different bundle becomes a direct bundle dependency. Output:
/// <see cref="BundleDependencyData"/> in the context.
/// </summary>
public sealed class ComputeDependencyTask : IBuildTask
{
    public int Version => 1;
    public string Name => "ComputeDependency";

    public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
    {
        var content = context.Get<BundleBuildContent>();
        var collection = context.Get<BundleBuildCollection>();
        var db = content.AssetDatabase;
        if (db == null) return BuildReturnCode.Error;

        var deps = new BundleDependencyData();
        for (int i = 0; i < collection.BundleNames.Count; i++) deps.DirectDependencies.Add(new System.Collections.Generic.HashSet<int>());

        for (int b = 0; b < collection.BundleAssets.Count; b++)
        {
            foreach (var guid in collection.BundleAssets[b])
            {
                Guid[]? dependenciesForAsset = db.GetEntry(guid)?.Dependencies;
                if (dependenciesForAsset == null && db.TryGetParentGuid(guid, out Guid parentGuid))
                {
                    var parentEntry = db.GetEntry(parentGuid);
                    if (parentEntry?.SubAssets != null)
                        for (int subIndex = 0; subIndex < parentEntry.SubAssets.Length; subIndex++)
                            if (parentEntry.SubAssets[subIndex].Guid == guid)
                            {
                                dependenciesForAsset = parentEntry.SubAssets[subIndex].Dependencies;
                                break;
                            }
                }

                if (dependenciesForAsset == null) continue;
                foreach (var depGuid in dependenciesForAsset)
                {
                    if (collection.AssetToBundle.TryGetValue(depGuid, out int depBundle) && depBundle != b)
                        deps.DirectDependencies[b].Add(depBundle);
                }
            }
        }

        context.Set(deps);
        progress?.Report(new BuildTaskProgress(Name, 1f));
        return BuildReturnCode.Success;
    }
}
