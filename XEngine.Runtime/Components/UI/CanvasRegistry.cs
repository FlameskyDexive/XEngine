// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Scene-owned registry of active Canvas hosts. Render collection uses this registry
/// instead of scanning every active GameObject each frame.
/// </summary>
public static class CanvasRegistry
{
    private static readonly Dictionary<Scene, List<GameCanvas>> s_canvases = new();
    private static readonly List<GameCanvas> s_empty = new(0);
    private static readonly HashSet<Scene> s_dirtyScenes = new();
    private static Float2? s_screenSizeOverride;

    /// <summary>Active render target size supplied by the pipeline during Canvas collection.</summary>
    public static Float2? ScreenSizeOverride
    {
        get => s_screenSizeOverride;
        set => s_screenSizeOverride = value;
    }

    internal static void Register(GameCanvas canvas)
    {
        Scene? scene = canvas.Scene;
        if (scene == null) return;
        if (!s_canvases.TryGetValue(scene, out List<GameCanvas>? list))
        {
            list = new List<GameCanvas>(4);
            s_canvases.Add(scene, list);
        }

        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], canvas)) return;
        list.Add(canvas);
        s_dirtyScenes.Add(scene);
    }

    internal static void Unregister(GameCanvas canvas)
    {
        Scene? scene = canvas.Scene;
        if (scene != null && s_canvases.TryGetValue(scene, out List<GameCanvas>? list))
        {
            list.Remove(canvas);
            s_dirtyScenes.Add(scene);
            if (list.Count == 0)
            {
                s_canvases.Remove(scene);
                s_dirtyScenes.Remove(scene);
            }
            return;
        }

        // A scene may clear the GameObject back-reference before the lifecycle callback.
        Scene? foundScene = null;
        foreach (KeyValuePair<Scene, List<GameCanvas>> entry in s_canvases)
        {
            if (!entry.Value.Contains(canvas)) continue;
            entry.Value.Remove(canvas);
            foundScene = entry.Key;
            s_dirtyScenes.Add(entry.Key);
            break;
        }
        if (foundScene != null && s_canvases[foundScene].Count == 0)
        {
            s_canvases.Remove(foundScene);
            s_dirtyScenes.Remove(foundScene);
        }
    }

    internal static void NotifyDirty(GameCanvas canvas)
    {
        Scene? scene = canvas.Scene;
        if (scene != null) s_dirtyScenes.Add(scene);
    }

    private static void EnsureSorted(Scene scene)
    {
        if (!s_dirtyScenes.Remove(scene) || !s_canvases.TryGetValue(scene, out List<GameCanvas>? list)) return;
        list.Sort(static (a, b) =>
        {
            int order = a.SortOrder.CompareTo(b.SortOrder);
            return order != 0 ? order : a.InstanceID.CompareTo(b.InstanceID);
        });
    }

    /// <summary>Returns the registered canvases for diagnostics without exposing mutable storage.</summary>
    public static IReadOnlyList<GameCanvas> GetCanvases(Scene scene)
    {
        if (scene == null) return s_empty;
        EnsureSorted(scene);
        return s_canvases.TryGetValue(scene, out List<GameCanvas>? list) ? list : s_empty;
    }

    /// <summary>Collects attached Canvas nodes for one render surface in stable canvas order.</summary>
    public static void CollectRenderNodes(Scene scene, UISurface surface, List<CanvasRenderNode> destination)
    {
        if (scene == null || destination == null) return;
        if (!s_canvases.TryGetValue(scene, out List<GameCanvas>? canvases)) return;
        EnsureSorted(scene);

        for (int i = 0; i < canvases.Count; i++)
        {
            GameCanvas canvas = canvases[i];
            canvas.CollectRenderNodes(surface, destination);
        }
    }

    /// <summary>Marks every registered Canvas dirty so a window resize or inset change relayouts.</summary>
    internal static void NotifyAllDirty(UIDirtyFlags flags)
    {
        foreach (KeyValuePair<Scene, List<GameCanvas>> entry in s_canvases)
        {
            List<GameCanvas> list = entry.Value;
            for (int i = 0; i < list.Count; i++)
            {
                GameCanvas canvas = list[i];
                if (canvas.IsDisposed || !canvas.EnabledInHierarchy) continue;
                canvas.MarkDirty(flags);
            }
        }
    }

    /// <summary>Drops baked UI GPU meshes after a device loss so the next rebuild uploads again.</summary>
    internal static void DiscardAllGpuMeshes()
    {
        foreach (KeyValuePair<Scene, List<GameCanvas>> entry in s_canvases)
        {
            List<GameCanvas> list = entry.Value;
            for (int i = 0; i < list.Count; i++)
            {
                GameCanvas canvas = list[i];
                if (canvas.IsDisposed) continue;
                canvas.DiscardGpuMeshes();
            }
        }
    }

    /// <summary>Clears global registration state during scene/domain teardown or hot reload.</summary>
    public static void Reset()
    {
        s_canvases.Clear();
        s_dirtyScenes.Clear();
        s_screenSizeOverride = null;
        UIRaycastRegistry.Reset();
    }

    /// <summary>Hot-reload hook: clears static canvas registrations that may hold user-script UIBehaviour types.</summary>
    [OnAssemblyUnload]
    internal static void ClearForReload() => Reset();

    /// <summary>Compatibility view used by legacy renderable collection callers.</summary>
    internal static void CollectRenderables(Scene scene, UISurface surface, List<IRenderable> destination)
    {
        if (scene == null || destination == null) return;
        if (!s_canvases.TryGetValue(scene, out List<GameCanvas>? canvases)) return;
        EnsureSorted(scene);

        for (int i = 0; i < canvases.Count; i++)
        {
            GameCanvas canvas = canvases[i];
            if (!canvas.EnabledInHierarchy || UIRenderTree.ToSurface(canvas.RenderMode) != surface) continue;
            canvas.RebuildIfDirty();
            canvas.Tree.RefreshTransforms();
            canvas.Tree.AppendNodes(destination);
            canvas.AppendRegisteredRendererNodes(surface, destination);
        }
    }

    internal static void Clear()
    {
        s_canvases.Clear();
        s_dirtyScenes.Clear();
        UIRaycastRegistry.Reset();
    }
}
