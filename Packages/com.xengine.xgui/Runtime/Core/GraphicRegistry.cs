using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Per-canvas graphic candidates for package input and diagnostics.</summary>
public static class GraphicRegistry
{
    private static readonly Dictionary<GameCanvas, List<Graphic>> s_graphics = new();
    private static readonly Dictionary<GameCanvas, List<Graphic>> s_raycastCandidates = new();
    private static readonly Dictionary<GameCanvas, List<UIBehaviour>> s_raycastHostCandidates = new();
    private static readonly Dictionary<Graphic, GameCanvas?> s_owners = new();
    private static readonly List<Graphic> s_rebindScratch = new();
    private static readonly HashSet<GameCanvas> s_raycastOrderDirty = new();
    private static readonly RaycastCandidateComparer s_raycastCandidateComparer = new();
    private static readonly RaycastHostCandidateComparer s_raycastHostCandidateComparer = new();
    private static readonly Provider s_provider = new();

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -880)]
    internal static void RegisterProvider()
        => UIRaycastProviderRegistration.Register(s_provider);

    [OnAssemblyLoad(Order = 100)]
    internal static void RebindAfterLoad()
    {
        RegisterProvider();
        Scene? scene = Scene.Current;
        if (scene != null) Rebind(scene);
    }

    [OnAssemblyUnload]
    internal static void ReleaseProvider()
    {
        UIRaycastProviderRegistration.Unregister(s_provider);
        Reset();
    }

    /// <summary>
    /// Re-registers live package Graphics after the collectible package context was unloaded.
    /// Host Canvas rebind must run first so each Graphic can resolve its owning Canvas.
    /// </summary>
    public static void Rebind(Scene scene)
    {
        RegisterProvider();
        if (scene == null || scene.IsDisposed) return;
        Graphic?[] graphics = scene.FindObjectsOfType<Graphic>();
        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic? graphic = graphics[i];
            if (graphic == null || graphic.IsDisposed || !graphic.EnabledInHierarchy) continue;
            RegisterGraphic(graphic);
        }
    }

    public static void RegisterGraphic(Graphic graphic)
    {
        GameCanvas? canvas = graphic.GetCanvas();
        if (s_owners.TryGetValue(graphic, out GameCanvas? previous))
        {
            if (ReferenceEquals(previous, canvas)) return;
            RemoveFromCanvas(graphic, previous);
        }
        s_owners[graphic] = canvas;
        if (canvas != null) AddToCanvas(graphic, canvas);
    }

    public static void UnregisterGraphic(Graphic graphic)
    {
        if (!s_owners.TryGetValue(graphic, out GameCanvas? canvas)) return;
        RemoveFromCanvas(graphic, canvas);
        s_owners.Remove(graphic);
    }

    public static IReadOnlyList<Graphic> GetGraphics(GameCanvas canvas)
    {
        RefreshCanvasOwnership();
        return s_graphics.TryGetValue(canvas, out List<Graphic>? list) ? list : Array.Empty<Graphic>();
    }

    /// <summary>
    /// Returns active package graphics from front to back. Ordering is refreshed only after a Canvas
    /// rebuild changes a renderer sort key; pointer input can then consume it without a hierarchy walk.
    /// </summary>
    public static IReadOnlyList<Graphic> GetRaycastCandidates(GameCanvas canvas)
    {
        RefreshCanvasOwnership();
        if (!s_raycastCandidates.TryGetValue(canvas, out List<Graphic>? list)) return Array.Empty<Graphic>();
        if (s_raycastOrderDirty.Remove(canvas)) list.Sort(s_raycastCandidateComparer);
        return list;
    }

    /// <summary>Called through the Runtime-neutral CanvasRenderer callback after host state changes.</summary>
    public static void NotifyRaycastStateChanged(Graphic graphic)
    {
        RegisterGraphic(graphic);
        if (s_owners.TryGetValue(graphic, out GameCanvas? canvas) && canvas != null)
            s_raycastOrderDirty.Add(canvas);
    }

    public static void Reset()
    {
        s_graphics.Clear();
        s_raycastCandidates.Clear();
        s_raycastHostCandidates.Clear();
        s_owners.Clear();
        s_rebindScratch.Clear();
        s_raycastOrderDirty.Clear();
    }

    private static void RefreshCanvasOwnership()
    {
        foreach (KeyValuePair<Graphic, GameCanvas?> entry in s_owners)
        {
            Graphic graphic = entry.Key;
            GameCanvas? current = graphic.IsDisposed ? null : graphic.GetCanvas();
            if (!ReferenceEquals(entry.Value, current)) s_rebindScratch.Add(graphic);
        }

        for (int i = 0; i < s_rebindScratch.Count; i++)
        {
            Graphic graphic = s_rebindScratch[i];
            if (!s_owners.TryGetValue(graphic, out GameCanvas? previous)) continue;

            RemoveFromCanvas(graphic, previous);
            if (graphic.IsDisposed)
            {
                s_owners.Remove(graphic);
                continue;
            }

            GameCanvas? current = graphic.GetCanvas();
            s_owners[graphic] = current;
            if (current != null) AddToCanvas(graphic, current);
        }
        s_rebindScratch.Clear();
    }

    private static void AddToCanvas(Graphic graphic, GameCanvas canvas)
    {
        if (!s_graphics.TryGetValue(canvas, out List<Graphic>? list))
            s_graphics.Add(canvas, list = new List<Graphic>(8));
        if (!list.Contains(graphic)) list.Add(graphic);

        if (!s_raycastCandidates.TryGetValue(canvas, out List<Graphic>? raycasts))
            s_raycastCandidates.Add(canvas, raycasts = new List<Graphic>(8));
        if (!raycasts.Contains(graphic))
        {
            raycasts.Add(graphic);
            s_raycastOrderDirty.Add(canvas);
        }

        if (!s_raycastHostCandidates.TryGetValue(canvas, out List<UIBehaviour>? hostRaycasts))
            s_raycastHostCandidates.Add(canvas, hostRaycasts = new List<UIBehaviour>(8));
        if (!hostRaycasts.Contains(graphic))
        {
            hostRaycasts.Add(graphic);
            s_raycastOrderDirty.Add(canvas);
        }
    }

    private static void RemoveFromCanvas(Graphic graphic, GameCanvas? canvas)
    {
        if (canvas == null || !s_graphics.TryGetValue(canvas, out List<Graphic>? list)) return;
        list.Remove(graphic);
        if (list.Count == 0) s_graphics.Remove(canvas);

        if (!s_raycastCandidates.TryGetValue(canvas, out List<Graphic>? raycasts)) return;
        raycasts.Remove(graphic);
        if (raycasts.Count == 0)
        {
            s_raycastCandidates.Remove(canvas);
            s_raycastHostCandidates.Remove(canvas);
            s_raycastOrderDirty.Remove(canvas);
            return;
        }
        s_raycastOrderDirty.Add(canvas);
    }

    private sealed class Provider : IUIRaycastProvider
    {
        public IReadOnlyList<UIBehaviour> GetRaycastCandidates(GameCanvas canvas)
        {
            RefreshCanvasOwnership();
            if (!s_raycastHostCandidates.TryGetValue(canvas, out List<UIBehaviour>? list))
                return Array.Empty<UIBehaviour>();
            if (s_raycastOrderDirty.Remove(canvas))
                list.Sort(s_raycastHostCandidateComparer);
            return list;
        }

        public bool TryGetRaycastState(UIBehaviour element, out long sortKey, out CanvasClipState clip,
            out bool blocksRaycasts, out Rect? raycastRect)
        {
            if (element is Graphic graphic && graphic.CanvasRenderer is { } renderer)
            {
                sortKey = renderer.RaycastSortKey;
                clip = renderer.ClipState;
                blocksRaycasts = renderer.BlocksRaycasts;
                raycastRect = renderer.RaycastRect;
                return true;
            }

            sortKey = long.MinValue;
            clip = CanvasClipState.None;
            blocksRaycasts = true;
            raycastRect = null;
            return false;
        }
    }

    private sealed class RaycastHostCandidateComparer : IComparer<UIBehaviour>
    {
        public int Compare(UIBehaviour? left, UIBehaviour? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is not Graphic leftGraphic) return -1;
            if (right is not Graphic rightGraphic) return 1;
            int sort = leftGraphic.RaycastSortKey.CompareTo(rightGraphic.RaycastSortKey);
            return sort != 0 ? sort : leftGraphic.InstanceID.CompareTo(rightGraphic.InstanceID);
        }
    }

    private sealed class RaycastCandidateComparer : IComparer<Graphic>
    {
        public int Compare(Graphic? left, Graphic? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;

            int sort = right.RaycastSortKey.CompareTo(left.RaycastSortKey);
            return sort != 0 ? sort : right.InstanceID.CompareTo(left.InstanceID);
        }
    }
}
