using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Ai.Protocol;
using XEngine.Clay;
using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P2.1 — Offline mesh budget + AABB for a model file. Replays the same Clay import as
/// <see cref="SubAssetResolver"/> so triangle counts and bounds match what the editor will bake.
/// Used by the playable-prop loop: budget gate (remesh when over cap) and auto BoxCollider extents.
/// </summary>
internal static class MeshStatsResolver
{
    public const float MinColliderExtent = 0.01f;

    public static MeshStatsResult Resolve(string absolutePath, string relativePath, int? maxTriangles = null)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Asset file not found: {absolutePath}");
        }

        string extension = Path.GetExtension(absolutePath);
        if (!ModelImportReplay.ModelExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                $"asset_mesh_stats reads model files only (.gltf/.glb/.obj/.fbx). '{relativePath}' is a {extension}.");
        }

        string? parentGuidText = null;
        Guid parentGuid = Guid.Empty;
        float unitScale = 1.0f;
        bool flipUVs = true;
        var warnings = new List<string>();

        string metaPath = absolutePath + ".meta";
        if (File.Exists(metaPath))
        {
            EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
            parentGuidText = meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String ? g.StringValue : null;
            if (!string.IsNullOrEmpty(parentGuidText))
            {
                Guid.TryParse(parentGuidText, out parentGuid);
            }

            EchoObject? settings = meta.TryGet("settings", out EchoObject? s) && s!.TagType == EchoType.Compound ? s : null;
            if (settings != null)
            {
                if (settings.TryGet("unitScale", out EchoObject? us))
                {
                    unitScale = us!.FloatValue;
                }

                if (settings.TryGet("flipUVs", out EchoObject? fu))
                {
                    flipUVs = fu!.BoolValue;
                }
            }
        }
        else
        {
            warnings.Add("No .meta sidecar — mesh GUIDs are omitted (import first to mint a parent GUID).");
        }

        Model model = ModelImportReplay.Load(absolutePath, extension, unitScale, flipUVs);
        var meshes = new List<MeshStatEntry>(model.Meshes.Count);
        var combined = Bounds.Empty;
        int totalVerts = 0;
        int totalTris = 0;

        for (int i = 0; i < model.Meshes.Count; i++)
        {
            Mesh mesh = model.Meshes[i];
            string name = mesh.Name ?? $"Mesh_{i}";
            int triangles = mesh.Indices.Length / 3;
            totalVerts += mesh.VertexCount;
            totalTris += triangles;
            if (!mesh.Bounds.IsEmpty)
            {
                combined.Encapsulate(mesh.Bounds);
            }

            string? guid = parentGuid != Guid.Empty
                ? SubAssetResolver.DeriveSubAssetGuid(parentGuid, name).ToString()
                : null;

            Float3 size = mesh.Bounds.IsEmpty ? default : mesh.Bounds.Size;
            Float3 center = mesh.Bounds.IsEmpty ? default : mesh.Bounds.Center;
            meshes.Add(new MeshStatEntry(
                name, mesh.VertexCount, triangles,
                mesh.Bounds.Min.X, mesh.Bounds.Min.Y, mesh.Bounds.Min.Z,
                mesh.Bounds.Max.X, mesh.Bounds.Max.Y, mesh.Bounds.Max.Z,
                size.X, size.Y, size.Z,
                center.X, center.Y, center.Z,
                guid));
        }

        bool empty = combined.IsEmpty;
        Float3 cSize = empty ? default : combined.Size;
        Float3 cCenter = empty ? default : combined.Center;
        float boxX = empty ? MinColliderExtent : Math.Max(cSize.X, MinColliderExtent);
        float boxY = empty ? MinColliderExtent : Math.Max(cSize.Y, MinColliderExtent);
        float boxZ = empty ? MinColliderExtent : Math.Max(cSize.Z, MinColliderExtent);
        bool over = maxTriangles is int cap && totalTris > cap;

        if (empty)
        {
            warnings.Add("Model has no vertices — BoxCollider falls back to the 0.01 minimum extent.");
        }

        return new MeshStatsResult(
            relativePath, parentGuidText, meshes.Count, totalVerts, totalTris,
            empty ? 0 : combined.Min.X, empty ? 0 : combined.Min.Y, empty ? 0 : combined.Min.Z,
            empty ? 0 : combined.Max.X, empty ? 0 : combined.Max.Y, empty ? 0 : combined.Max.Z,
            cSize.X, cSize.Y, cSize.Z,
            cCenter.X, cCenter.Y, cCenter.Z,
            boxX, boxY, boxZ,
            cCenter.X, cCenter.Y, cCenter.Z,
            maxTriangles, over, meshes, warnings);
    }
}
