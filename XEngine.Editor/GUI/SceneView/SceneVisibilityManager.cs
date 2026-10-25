// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Editor-only Scene Visibility / Pickability. Hidden objects stay enabled for Game View
/// and play mode; they are skipped only for the Scene View camera and Scene View picking.
/// State persists under Library/SceneVisibility/ and is never written into .scene files.
/// </summary>
public static class SceneVisibilityManager
{
    private static readonly HashSet<Guid> s_hidden = new();
    private static readonly HashSet<Guid> s_unpickable = new();
    private static string? s_boundKey;
    private static bool s_installed;
    private static int s_version;

    public static int Version => s_version;

    public static void Install()
    {
        if (s_installed) return;
        s_installed = true;
        Scene.RenderCollectFilter = AllowRenderCollect;
    }

    [OnAssemblyUnload]
    internal static void Unload()
    {
        if (ReferenceEquals(Scene.RenderCollectFilter, (Func<GameObject, Camera, bool>)AllowRenderCollect))
            Scene.RenderCollectFilter = null;
        s_installed = false;
        Reset();
    }

    public static void Reset()
    {
        s_hidden.Clear();
        s_unpickable.Clear();
        s_boundKey = null;
        s_version++;
    }

    public static void BindScene(string? scenePath)
    {
        Install();
        if (!string.IsNullOrEmpty(s_boundKey))
            SaveBound();
        s_hidden.Clear();
        s_unpickable.Clear();
        s_boundKey = string.IsNullOrEmpty(scenePath) ? null : scenePath;
        if (s_boundKey != null)
            LoadBound();
        NotifyChanged();
    }

    public static bool IsHidden(GameObject? go)
        => go != null && s_hidden.Contains(go.Identifier);

    public static bool IsPickingEnabled(GameObject? go)
        => go == null || !s_unpickable.Contains(go.Identifier);

    public static bool IsVisibleInHierarchy(GameObject? go)
    {
        while (go != null)
        {
            if (s_hidden.Contains(go.Identifier))
                return false;
            go = go.Parent;
        }
        return true;
    }

    public static bool IsPickingEnabledInHierarchy(GameObject? go)
    {
        while (go != null)
        {
            if (s_unpickable.Contains(go.Identifier) || s_hidden.Contains(go.Identifier))
                return false;
            go = go.Parent;
        }
        return true;
    }

    public static void SetHidden(GameObject go, bool hidden)
    {
        if (go == null) return;
        bool changed = hidden ? s_hidden.Add(go.Identifier) : s_hidden.Remove(go.Identifier);
        if (!changed) return;
        SaveBound();
        NotifyChanged();
    }

    public static void SetPickingEnabled(GameObject go, bool enabled)
    {
        if (go == null) return;
        bool changed = enabled ? s_unpickable.Remove(go.Identifier) : s_unpickable.Add(go.Identifier);
        if (!changed) return;
        SaveBound();
        NotifyChanged();
    }

    public static void ToggleHidden(GameObject go)
        => SetHidden(go, !IsHidden(go));

    public static void TogglePicking(GameObject go)
        => SetPickingEnabled(go, !IsPickingEnabled(go));

    /// <summary>Scene View camera only. Game View, preview, and other cameras always pass.</summary>
    public static bool AllowRenderCollect(GameObject go, Camera camera)
    {
        if (go == null || camera == null) return true;
        var sceneCam = SceneViewPanel.ActiveCamera;
        if (sceneCam == null || !ReferenceEquals(camera, sceneCam.Camera))
            return true;
        return IsVisibleInHierarchy(go);
    }

    public static string SerializeState()
    {
        var dto = new SceneVisibilityFile
        {
            Hidden = ToIdList(s_hidden),
            Unpickable = ToIdList(s_unpickable),
        };
        return JsonSerializer.Serialize(dto);
    }

    public static void DeserializeState(string json)
    {
        s_hidden.Clear();
        s_unpickable.Clear();
        if (string.IsNullOrWhiteSpace(json)) return;
        var dto = JsonSerializer.Deserialize<SceneVisibilityFile>(json);
        if (dto == null) return;
        AddIds(dto.Hidden, s_hidden);
        AddIds(dto.Unpickable, s_unpickable);
        s_version++;
    }

    private static void NotifyChanged()
    {
        s_version++;
        SceneViewPanel.ActiveInstance?.InvalidateRender();
        EditorApplication.Instance?.InvalidateSurface(EditorSurfaceDirtyReason.ExplicitRepaint);
    }

    private static void SaveBound()
    {
        if (s_boundKey == null || Project.Current == null) return;
        try
        {
            string path = FilePath(s_boundKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, SerializeState());
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[SceneVisibility] Save failed: {ex.Message}");
        }
    }

    private static void LoadBound()
    {
        if (s_boundKey == null || Project.Current == null) return;
        string path = FilePath(s_boundKey);
        if (!File.Exists(path)) return;
        try
        {
            DeserializeState(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[SceneVisibility] Load failed: {ex.Message}");
        }
    }

    private static string FilePath(string scenePath)
    {
        string safe = scenePath.Replace('\\', '_').Replace('/', '_');
        return Path.Combine(Project.Current!.LibraryPath, "SceneVisibility", safe + ".json");
    }

    private static List<string> ToIdList(HashSet<Guid> set)
    {
        var list = new List<string>(set.Count);
        foreach (Guid id in set)
            list.Add(id.ToString("N"));
        return list;
    }

    private static void AddIds(List<string>? ids, HashSet<Guid> dest)
    {
        if (ids == null) return;
        for (int i = 0; i < ids.Count; i++)
        {
            if (Guid.TryParse(ids[i], out Guid id))
                dest.Add(id);
        }
    }

    private sealed class SceneVisibilityFile
    {
        public List<string> Hidden { get; set; } = new();
        public List<string> Unpickable { get; set; } = new();
    }
}
