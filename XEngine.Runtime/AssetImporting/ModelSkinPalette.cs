using System;
using System.Collections.Generic;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.AssetImporting;

/// <summary>Import-time palette compaction. Skeleton Transforms and animation bindings are never removed.</summary>
internal static class ModelSkinPalette
{
    public static void StripUnused(ModelImportResult model)
    {
        if (model.RootGO == null) return;
        var renderers = new List<SkinnedMeshRenderer>(model.RootGO.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
        foreach (var mesh in model.Meshes)
        {
            var names = mesh.BoneNames; var binds = mesh.BindPoses;
            var indices = mesh.BoneIndices; var weights = mesh.BoneWeights;
            if (names == null || binds == null || indices == null || weights == null) continue;
            if (names.Length != binds.Length || indices.Length != weights.Length)
                throw new InvalidOperationException("Cannot strip an inconsistent skin palette.");
            var map = new int[names.Length];
            for (int v = 0; v < indices.Length; v++)
            {
                Mark(indices[v].X, weights[v].X, map); Mark(indices[v].Y, weights[v].Y, map);
                Mark(indices[v].Z, weights[v].Z, map); Mark(indices[v].W, weights[v].W, map);
            }
            int count = 0;
            for (int b = 0; b < map.Length; b++) if (map[b] != 0) map[b] = ++count;
            if (count == names.Length) continue; // off and already compact output remain byte-identical.
            var keptNames = new string[count]; var keptBinds = new Float4x4[count];
            for (int b = 0; b < map.Length; b++)
            {
                if (map[b] == 0) continue;
                keptNames[map[b] - 1] = names[b]; keptBinds[map[b] - 1] = binds[b];
            }
            // Capture live references before replacing mesh metadata: already resolved renderers
            // must not attempt to follow the new palette while it is only partially updated.
            var captured = new List<(SkinnedMeshRenderer renderer, Transform[] bones, Transform? root)>();
            foreach (var renderer in renderers)
            {
                if (!ReferenceEquals(renderer.SharedMesh.Res, mesh)) continue;
                var previous = renderer.Bones;
                if (previous == null || previous.Length != names.Length)
                    throw new InvalidOperationException("Cannot strip a renderer with a mismatched skin palette.");
                var keptBones = new Transform[count];
                for (int b = 0; b < map.Length; b++)
                {
                    if (map[b] == 0) continue;
                    keptBones[map[b] - 1] = previous[b]
                        ?? throw new InvalidOperationException("Cannot strip a renderer with an unresolved weighted bone.");
                }
                captured.Add((renderer, keptBones, renderer.RootBone));
            }
            for (int v = 0; v < indices.Length; v++)
            {
                var old = indices[v]; var w = weights[v];
                indices[v] = new Float4(Remap(old.X, w.X, map), Remap(old.Y, w.Y, map),
                    Remap(old.Z, w.Z, map), Remap(old.W, w.W, map));
            }
            mesh.BoneNames = keptNames; mesh.BindPoses = keptBinds;
            foreach (var binding in captured) binding.renderer.SetBones(binding.bones, binding.root);
        }
    }

    private static void Mark(float index, float weight, int[] map)
    {
        if (!(weight > 0)) return;
        int bone = (int)index - 1;
        if ((uint)bone >= (uint)map.Length || index != bone + 1)
            throw new InvalidOperationException("Cannot strip a skin with an invalid positive-weight bone index.");
        map[bone] = 1;
    }

    private static int Remap(float index, float weight, int[] map) => weight > 0 ? map[(int)index - 1] : 0;
}
