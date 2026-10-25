using System;
using System.Collections.Generic;

using XEngine.Clay;
using XEngine.Clay.Importer;

using ClayModelImporter = XEngine.Clay.Importer.ModelImporter;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P2.1 — Shared Clay replay of the editor's model-import post-process flags
/// (<c>ClayBackedImporter.MapSettings</c>: GameQuality minus normal/tangent steps, FlipUVs only
/// for the glTF family). <see cref="SubAssetResolver"/> and <see cref="MeshStatsResolver"/> must
/// see the same mesh set the editor import will produce.
/// </summary>
internal static class ModelImportReplay
{
    internal static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".obj", ".fbx",
    };

    public static Model Load(string absolutePath, string extension, float unitScale, bool flipUVs)
    {
        PostProcessFlags flags = PostProcessPresets.GameQuality;
        flags &= ~PostProcessFlags.CalcTangentSpace;
        flags &= ~PostProcessFlags.GenerateSmoothNormals;
        flags &= ~PostProcessFlags.GenerateNormals;

        bool sourceNeedsFlip = extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".glb", StringComparison.OrdinalIgnoreCase);
        if (flipUVs && sourceNeedsFlip)
        {
            flags |= PostProcessFlags.FlipUVs;
        }
        else
        {
            flags &= ~PostProcessFlags.FlipUVs;
        }

        var settings = new ModelImporterSettings
        {
            PostProcess = flags,
            GlobalScale = unitScale,
            BoneWeightLimit = 4,
        };

        return ClayModelImporter.Load(absolutePath, settings);
    }
}
