// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using PropertyGridUtils = XEngine.Editor.GUI.PropertyGridUtils;

namespace XEngine.Editor.Inspector;

/// <summary>
/// Shared material inspector body (shader slot + property rows) with debounced auto-save
/// for disk-backed materials. Runtime-only instances stay in memory.
/// </summary>
public static class MaterialEditorGUI
{
    public const float AutoSaveDelaySeconds = 0.5f;

    private struct PendingSave
    {
        public Material Material;
        public float LastChangeTime;
    }

    private static readonly Dictionary<Guid, PendingSave> s_pending = new();
    private static readonly List<Guid> s_flushScratch = new();

    public static bool HasPendingSaves => s_pending.Count > 0;

    public static bool IsSaveDue(float lastChangeTime, float now)
        => now - lastChangeTime >= AutoSaveDelaySeconds;

    public static void Draw(Paper paper, string id, Material material)
    {
        FlushDueSaves();
        if (material == null) return;

        PropertyGridUtils.DrawField(paper, $"{id}_shader", "Shader", typeof(AssetRef<Shader>), material.ShaderRef,
            newVal =>
            {
                material.ShaderRef = (AssetRef<Shader>)newVal!;
                MarkDirty(material);
            }, 0);

        var shader = material.Shader;
        if (shader == null) return;

        Origami.Header(paper, $"{id}_h_props", "Properties").Underline().Show();
        foreach (var prop in shader.Properties)
        {
            MaterialPropertyDrawer.DrawPropertyRow(paper, $"{id}_p_{prop.Name}", material, prop,
                onChanged: () => MarkDirty(material));
        }
    }

    public static void MarkDirty(Material material)
    {
        if (material == null || material.AssetID == Guid.Empty) return;
        s_pending[material.AssetID] = new PendingSave
        {
            Material = material,
            LastChangeTime = Time.UnscaledTotalTime,
        };
    }

    public static void Tick()
    {
        if (s_pending.Count == 0) return;
        FlushDueSaves();
    }

    public static void FlushDueSaves()
    {
        if (s_pending.Count == 0) return;

        float now = Time.UnscaledTotalTime;
        s_flushScratch.Clear();
        foreach (var kv in s_pending)
        {
            if (IsSaveDue(kv.Value.LastChangeTime, now))
                s_flushScratch.Add(kv.Key);
        }

        for (int i = 0; i < s_flushScratch.Count; i++)
        {
            Guid id = s_flushScratch[i];
            if (!s_pending.Remove(id, out var pending)) continue;
            TrySave(pending.Material);
        }
    }

    public static bool TrySave(Material material)
    {
        if (material == null || material.AssetID == Guid.Empty) return false;
        if (Project.Current == null) return false;

        var entry = EditorAssetBackend.Instance?.GetEntry(material.AssetID);
        if (entry == null || string.IsNullOrEmpty(entry.Path)) return false;

        string absolutePath = Path.Combine(Project.Current.AssetsPath, entry.Path);
        try
        {
            var savedId = material.AssetID;
            material.AssetID = Guid.Empty;
            var echo = Serializer.Serialize(typeof(object), material);
            material.AssetID = savedId;

            if (echo == null) return false;
            File.WriteAllText(absolutePath, echo.WriteToString());
            EditorAssetBackend.Instance?.Reimport(entry.Guid);
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to save material: {ex.Message}");
            return false;
        }
    }
}
