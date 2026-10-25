using System;
using System.Collections.Generic;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.Vector;

namespace XEngine.Editor.Core;

public enum DockDropZone
{
    None,
    Tab,
    Left,
    Right,
    Top,
    Bottom,
    Float,
}

public readonly record struct DockDropCandidate(
    int HostId,
    DockDropZone Zone,
    Rect PreviewBounds,
    bool IsValid,
    DockNode? TargetLeaf = null)
{
    public static DockDropCandidate None => default;
}

/// <summary>Registry and ownership boundary for editor surfaces.</summary>
public sealed class EditorWorkspace
{
    private const float FloatingWidth = 480f;
    private const float FloatingHeight = 320f;
    private readonly List<EditorSurfaceHost> _hosts = new();
    private readonly HashSet<DockPanel> _ownedPanels = new();

    public IReadOnlyList<EditorSurfaceHost> Hosts => _hosts;
    /// <summary>Optional native-host creation transaction. Returning false rejects the logical host.</summary>
    public Func<EditorSurfaceHost, bool>? TryCreateHost { get; set; }
    /// <summary>Releases native resources after a host has been removed from the workspace.</summary>
    public Action<EditorSurfaceHost>? ReleaseHost { get; set; }

    public bool RegisterHost(EditorSurfaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (FindHost(host.Id) != null) return false;

        List<DockPanel> panels = new();
        CollectPanels(host.DockSpace.Root, panels);
        for (int i = 0; i < host.DockSpace.FloatingWindows.Count; i++)
            CollectPanels(host.DockSpace.FloatingWindows[i].Node, panels);
        for (int i = 0; i < panels.Count; i++)
            if (_ownedPanels.Contains(panels[i]))
                return false;

        if (TryCreateHost != null && !TryCreateHost(host))
            return false;

        for (int i = 0; i < panels.Count; i++) _ownedPanels.Add(panels[i]);
        _hosts.Add(host);
        return true;
    }

    public bool UnregisterHost(int id)
    {
        for (int i = 0; i < _hosts.Count; i++)
        {
            if (_hosts[i].Id != id) continue;
            EditorSurfaceHost host = _hosts[i];
            _hosts.RemoveAt(i);
            RebuildOwnership();
            ReleaseHost?.Invoke(host);
            return true;
        }
        return false;
    }

    /// <summary>Atomically replaces a host's dock tree and refreshes panel ownership.</summary>
    public bool TryReplaceDockSpace(int id, DockSpace dockSpace)
    {
        ArgumentNullException.ThrowIfNull(dockSpace);
        EditorSurfaceHost? host = FindHost(id);
        if (host == null) return false;

        List<DockPanel> panels = new();
        CollectPanels(dockSpace.Root, panels);
        for (int i = 0; i < dockSpace.FloatingWindows.Count; i++)
            CollectPanels(dockSpace.FloatingWindows[i].Node, panels);
        for (int i = 0; i < panels.Count; i++)
        {
            EditorSurfaceHost? owner = FindOwner(panels[i]);
            if (owner != null && !ReferenceEquals(owner, host))
                return false;
        }

        host.ReplaceDockSpace(dockSpace);
        RebuildOwnership();
        return true;
    }

    /// <summary>
    /// Closes a non-main host and transfers its panels into a fallback host.
    /// Salvage path only (native create / event-pump failure). User caption-X uses
    /// <see cref="TryDestroyHost"/> so panels are destroyed, not redocked.
    /// </summary>
    public bool TryCloseHost(int id, int fallbackHostId, out int transferredPanelCount)
    {
        transferredPanelCount = 0;
        EditorSurfaceHost? host = FindHost(id);
        EditorSurfaceHost? fallback = FindHost(fallbackHostId);
        if (host == null || fallback == null || ReferenceEquals(host, fallback)
            || (_hosts.Count > 0 && ReferenceEquals(host, _hosts[0])))
            return false;

        List<DockPanel> panels = new();
        CollectPanels(host.DockSpace.Root, panels);
        for (int i = 0; i < host.DockSpace.FloatingWindows.Count; i++)
            CollectPanels(host.DockSpace.FloatingWindows[i].Node, panels);
        DockNode? target = FindFirstLeaf(fallback.DockSpace.Root);
        if (target == null)
        {
            target = DockNode.Leaf();
            fallback.DockSpace.Root = target;
        }
        for (int i = 0; i < panels.Count; i++)
        {
            target.InsertTab(panels[i]);
            transferredPanelCount++;
        }

        _hosts.Remove(host);
        RebuildOwnership();
        ReleaseHost?.Invoke(host);
        BringToFront(fallback.Id);
        return true;
    }

    /// <summary>
    /// Destroys a non-main floating host and all of its panels (Unity caption-X).
    /// Does not redock into the main layout; panels must be reopened or dragged back.
    /// </summary>
    public bool TryDestroyHost(int id, out int destroyedPanelCount)
    {
        destroyedPanelCount = 0;
        EditorSurfaceHost? host = FindHost(id);
        if (host == null || (_hosts.Count > 0 && ReferenceEquals(host, _hosts[0])) || host.Id == 0)
            return false;

        List<DockPanel> panels = new();
        CollectPanels(host.DockSpace.Root, panels);
        for (int i = 0; i < host.DockSpace.FloatingWindows.Count; i++)
            CollectPanels(host.DockSpace.FloatingWindows[i].Node, panels);
        for (int i = 0; i < panels.Count; i++)
        {
            panels[i].OnClosed();
            destroyedPanelCount++;
        }

        _hosts.Remove(host);
        RebuildOwnership();
        ReleaseHost?.Invoke(host);
        return true;
    }

    public EditorSurfaceHost? FindHost(int id)
    {
        for (int i = 0; i < _hosts.Count; i++)
            if (_hosts[i].Id == id) return _hosts[i];
        return null;
    }

    public EditorSurfaceHost? FindOwner(DockPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        for (int i = _hosts.Count - 1; i >= 0; i--)
            if (_hosts[i].OwnsPanel(panel)) return _hosts[i];
        return null;
    }

    /// <summary>Returns the topmost visible host candidate at a screen-space point.</summary>
    public DockDropCandidate FindDropCandidate(Float2 screenPosition)
    {
        for (int i = _hosts.Count - 1; i >= 0; i--)
        {
            EditorSurfaceHost host = _hosts[i];
            Rect hostBounds = host.DockingScreenBounds;
            if (!host.IsVisible || !Contains(hostBounds, screenPosition)) continue;
            float scale = Math.Max(0.01f, host.DpiScale * EditorTheme.UserScale);
            var localPosition = new Float2(
                (screenPosition.X - hostBounds.Min.X) / scale,
                (screenPosition.Y - hostBounds.Min.Y) / scale);
            if (host.DockSpace.TryGetLeafAt(localPosition, out DockNode? drawnLeaf, out Rect drawnBounds))
            {
                Rect screenBounds = new(
                    new Float2(hostBounds.Min.X + drawnBounds.Min.X * scale,
                        hostBounds.Min.Y + drawnBounds.Min.Y * scale),
                    new Float2(hostBounds.Min.X + drawnBounds.Max.X * scale,
                        hostBounds.Min.Y + drawnBounds.Max.Y * scale));
                return CreateCandidate(host, screenPosition, drawnLeaf!, screenBounds);
            }
            if (TryFindLeaf(host.DockSpace.Root, hostBounds, screenPosition,
                    out DockNode? leaf, out Rect leafBounds))
                return CreateCandidate(host, screenPosition, leaf, leafBounds);
        }
        return DockDropCandidate.None;
    }

    /// <summary>Finds the leaf that currently owns a panel, including nested split trees.</summary>
    public DockNode? FindPanelLeaf(DockPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        for (int i = _hosts.Count - 1; i >= 0; i--)
        {
            DockNode? leaf = FindPanelLeaf(_hosts[i].DockSpace.Root, panel);
            if (leaf != null) return leaf;
            for (int j = 0; j < _hosts[i].DockSpace.FloatingWindows.Count; j++)
            {
                leaf = FindPanelLeaf(_hosts[i].DockSpace.FloatingWindows[j].Node, panel);
                if (leaf != null) return leaf;
            }
        }
        return null;
    }

    public void BringToFront(int id)
    {
        EditorSurfaceHost? host = FindHost(id);
        if (host == null || _hosts.Count == 0 || ReferenceEquals(_hosts[^1], host)) return;
        _hosts.Remove(host);
        _hosts.Add(host);
    }

    /// <summary>Creates a native-backed top-level host for a panel that is not yet owned by a workspace.</summary>
    public bool TryCreateDetachedHost(DockPanel panel, Rect screenBounds, out EditorSurfaceHost? host)
    {
        ArgumentNullException.ThrowIfNull(panel);
        host = null;
        if (FindOwner(panel) != null) return false;

        var candidate = new EditorSurfaceHost(NextHostId(), new DockSpace(DockNode.Leaf(panel)), screenBounds);
        if (!RegisterHost(candidate)) return false;
        host = candidate;
        BringToFront(candidate.Id);
        return true;
    }

    /// <summary>Moves legacy in-client floating windows into independent workspace hosts.</summary>
    public int MigrateLegacyFloatingWindows(int sourceHostId)
    {
        EditorSurfaceHost? sourceHost = FindHost(sourceHostId);
        if (sourceHost == null || sourceHost.DockSpace.FloatingWindows.Count == 0) return 0;

        var legacyWindows = new List<FloatingWindow>(sourceHost.DockSpace.FloatingWindows);
        sourceHost.DockSpace.FloatingWindows.Clear();
        RebuildOwnership();

        int migrated = 0;
        for (int i = 0; i < legacyWindows.Count; i++)
        {
            FloatingWindow legacy = legacyWindows[i];
            Float2 position = legacy.Position;
            Float2 size = legacy.Size;
            Rect bounds = new(
                sourceHost.ScreenBounds.Min.X + position.X,
                sourceHost.ScreenBounds.Min.Y + position.Y,
                sourceHost.ScreenBounds.Min.X + position.X + size.X,
                sourceHost.ScreenBounds.Min.Y + position.Y + size.Y);
            var candidate = new EditorSurfaceHost(NextHostId(), new DockSpace(legacy.Node), bounds);
            if (RegisterHost(candidate))
            {
                BringToFront(candidate.Id);
                migrated++;
            }
            else
            {
                // Keep a legacy window visible when the native-host transaction is unavailable.
                sourceHost.DockSpace.FloatingWindows.Add(legacy);
                RebuildOwnership();
            }
        }

        return migrated;
    }

    /// <summary>Adds a newly created panel to an existing host when native detachment is unavailable.</summary>
    public bool TryAddPanelToHost(DockPanel panel, int hostId)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (FindOwner(panel) != null) return false;
        EditorSurfaceHost? host = FindHost(hostId);
        if (host == null) return false;

        DockNode? target = FindFirstLeaf(host.DockSpace.Root);
        if (target == null)
        {
            target = DockNode.Leaf();
            host.DockSpace.Root = target;
        }
        target.InsertTab(panel);
        RebuildOwnership();
        return true;
    }

    /// <summary>Moves a panel into a new top-level logical host at the requested screen point.</summary>
    public bool TryFloatPanel(DockPanel panel, Float2 screenPosition, out EditorSurfaceHost? host)
    {
        ArgumentNullException.ThrowIfNull(panel);
        host = null;
        EditorSurfaceHost? sourceHost = FindOwner(panel);
        if (sourceHost == null)
            return false;
        if (!RemovePanel(sourceHost.DockSpace, panel))
            return false;

        Rect bounds = new(
            screenPosition.X - FloatingWidth * 0.5f,
            screenPosition.Y - FloatingHeight * 0.5f,
            screenPosition.X + FloatingWidth * 0.5f,
            screenPosition.Y + FloatingHeight * 0.5f);
        EditorSurfaceHost candidate = new(NextHostId(), new DockSpace(DockNode.Leaf(panel)), bounds);
        RebuildOwnership();
        if (!RegisterHost(candidate))
        {
            DockNode? restoreTarget = FindFirstLeaf(sourceHost.DockSpace.Root);
            if (restoreTarget == null)
            {
                restoreTarget = DockNode.Leaf();
                sourceHost.DockSpace.Root = restoreTarget;
            }
            restoreTarget.InsertTab(panel);
            RebuildOwnership();
            return false;
        }
        host = candidate;
        BringToFront(candidate.Id);
        return true;
    }

    public bool TryDockPanel(DockPanel panel, DockDropCandidate candidate)
    {
        if (!candidate.IsValid || candidate.Zone == DockDropZone.Float) return false;
        EditorSurfaceHost? sourceHost = FindOwner(panel);
        EditorSurfaceHost? targetHost = FindHost(candidate.HostId);
        if (sourceHost == null || targetHost == null) return false;
        if (!RemovePanel(sourceHost.DockSpace, panel)) return false;

        DockNode target = candidate.TargetLeaf != null
            && ContainsNode(targetHost.DockSpace.Root, candidate.TargetLeaf)
            ? candidate.TargetLeaf
            : FindFirstLeaf(targetHost.DockSpace.Root) ?? DockNode.Leaf();
        if (candidate.Zone == DockDropZone.Tab)
        {
            target.InsertTab(panel);
        }
        else
        {
            DockNode newLeaf = DockNode.Leaf(panel);
            SplitDirection direction = candidate.Zone is DockDropZone.Left or DockDropZone.Right
                ? SplitDirection.Horizontal : SplitDirection.Vertical;
            bool insertFirst = candidate.Zone is DockDropZone.Left or DockDropZone.Top;
            DockNode split = DockNode.Split(direction, insertFirst ? 0.25f : 0.75f,
                insertFirst ? newLeaf : target,
                insertFirst ? target : newLeaf);
            targetHost.DockSpace.Root = ReplaceNode(targetHost.DockSpace.Root, target, split);
        }

        RebuildOwnership();
        return true;
    }

    private void RebuildOwnership()
    {
        _ownedPanels.Clear();
        for (int i = 0; i < _hosts.Count; i++)
        {
            List<DockPanel> panels = new();
            CollectPanels(_hosts[i].DockSpace.Root, panels);
            for (int j = 0; j < _hosts[i].DockSpace.FloatingWindows.Count; j++)
                CollectPanels(_hosts[i].DockSpace.FloatingWindows[j].Node, panels);
            for (int j = 0; j < panels.Count; j++) _ownedPanels.Add(panels[j]);
        }
    }

    private int NextHostId()
    {
        int id = 0;
        for (int i = 0; i < _hosts.Count; i++)
            id = Math.Max(id, _hosts[i].Id + 1);
        return id;
    }

    private static void CollectPanels(DockNode? node, List<DockPanel> panels)
    {
        if (node == null) return;
        if (node.IsLeaf)
        {
            if (node.Tabs == null) return;
            for (int i = 0; i < node.Tabs.Count; i++) panels.Add(node.Tabs[i]);
            return;
        }
        CollectPanels(node.ChildA, panels);
        CollectPanels(node.ChildB, panels);
    }

    private static bool RemovePanel(DockSpace dockSpace, DockPanel panel)
    {
        if (RemovePanel(dockSpace.Root, panel)) return true;
        for (int i = 0; i < dockSpace.FloatingWindows.Count; i++)
            if (RemovePanel(dockSpace.FloatingWindows[i].Node, panel)) return true;
        return false;
    }

    private static bool RemovePanel(DockNode? node, DockPanel panel)
    {
        if (node == null) return false;
        if (node.IsLeaf)
        {
            if (node.Tabs == null) return false;
            for (int i = 0; i < node.Tabs.Count; i++)
            {
                if (!ReferenceEquals(node.Tabs[i], panel)) continue;
                node.RemoveTab(i);
                return true;
            }
            return false;
        }
        return RemovePanel(node.ChildA, panel) || RemovePanel(node.ChildB, panel);
    }

    private static DockNode? FindFirstLeaf(DockNode? node)
    {
        if (node == null) return null;
        if (node.IsLeaf) return node;
        return FindFirstLeaf(node.ChildA) ?? FindFirstLeaf(node.ChildB);
    }

    private static DockNode ReplaceNode(DockNode root, DockNode target, DockNode replacement)
    {
        if (ReferenceEquals(root, target)) return replacement;
        if (root.ChildA != null)
        {
            if (ReferenceEquals(root.ChildA, target)) { root.ChildA = replacement; return root; }
            if (ContainsNode(root.ChildA, target)) { root.ChildA = ReplaceNode(root.ChildA, target, replacement); return root; }
        }
        if (root.ChildB != null)
        {
            if (ReferenceEquals(root.ChildB, target)) { root.ChildB = replacement; return root; }
            if (ContainsNode(root.ChildB, target)) { root.ChildB = ReplaceNode(root.ChildB, target, replacement); return root; }
        }
        return root;
    }

    private static bool ContainsNode(DockNode? root, DockNode target)
    {
        if (root == null) return false;
        if (ReferenceEquals(root, target)) return true;
        return ContainsNode(root.ChildA, target) || ContainsNode(root.ChildB, target);
    }

    private static DockDropCandidate CreateCandidate(EditorSurfaceHost host, Float2 point,
        DockNode targetLeaf, Rect bounds)
    {
        float width = bounds.Max.X - bounds.Min.X;
        float height = bounds.Max.Y - bounds.Min.Y;
        float edge = Math.Clamp(MathF.Min(width, height) * 0.25f, 48f, 120f);
        float left = point.X - bounds.Min.X;
        float right = bounds.Max.X - point.X;
        float top = point.Y - bounds.Min.Y;
        float bottom = bounds.Max.Y - point.Y;
        float nearest = MathF.Min(MathF.Min(left, right), MathF.Min(top, bottom));

        DockDropZone zone = nearest > edge
            ? DockDropZone.Tab
            : nearest == left ? DockDropZone.Left
            : nearest == right ? DockDropZone.Right
            : nearest == top ? DockDropZone.Top
            : DockDropZone.Bottom;

        Rect preview = zone switch
        {
            DockDropZone.Left => new Rect(bounds.Min, new Float2(bounds.Min.X + width * 0.5f, bounds.Max.Y)),
            DockDropZone.Right => new Rect(new Float2(bounds.Min.X + width * 0.5f, bounds.Min.Y), bounds.Max),
            DockDropZone.Top => new Rect(bounds.Min, new Float2(bounds.Max.X, bounds.Min.Y + height * 0.5f)),
            DockDropZone.Bottom => new Rect(new Float2(bounds.Min.X, bounds.Min.Y + height * 0.5f), bounds.Max),
            _ => bounds,
        };
        return new DockDropCandidate(host.Id, zone, preview, true, targetLeaf);
    }

    // The draw cache is intentionally private to Origami. Recompute the split geometry here so a
    // cross-native drag can target the same leaf regions even before the next host repaint.
    private static bool TryFindLeaf(DockNode? node, Rect bounds, Float2 point,
        out DockNode? leaf, out Rect leafBounds)
    {
        leaf = null;
        leafBounds = default;
        if (node == null || !Contains(bounds, point)) return false;
        if (node.IsLeaf)
        {
            leaf = node;
            leafBounds = bounds;
            return true;
        }

        const float splitter = 4f;
        float width = MathF.Max(0f, bounds.Max.X - bounds.Min.X);
        float height = MathF.Max(0f, bounds.Max.Y - bounds.Min.Y);
        if (node.Direction == SplitDirection.Horizontal)
        {
            float aWidth = MathF.Max(0f, (width - splitter) * node.SplitRatio);
            Rect a = new(bounds.Min, new Float2(bounds.Min.X + aWidth, bounds.Max.Y));
            Rect b = new(new Float2(bounds.Min.X + aWidth + splitter, bounds.Min.Y), bounds.Max);
            if (TryFindLeaf(node.ChildA, a, point, out leaf, out leafBounds)
                || TryFindLeaf(node.ChildB, b, point, out leaf, out leafBounds)) return true;
            return TryFindLeaf(point.X <= a.Max.X ? node.ChildA : node.ChildB,
                point.X <= a.Max.X ? a : b, point, out leaf, out leafBounds);
        }

        float aHeight = MathF.Max(0f, (height - splitter) * node.SplitRatio);
        Rect top = new(bounds.Min, new Float2(bounds.Max.X, bounds.Min.Y + aHeight));
        Rect bottom = new(new Float2(bounds.Min.X, bounds.Min.Y + aHeight + splitter), bounds.Max);
        if (TryFindLeaf(node.ChildA, top, point, out leaf, out leafBounds)
            || TryFindLeaf(node.ChildB, bottom, point, out leaf, out leafBounds)) return true;
        return TryFindLeaf(point.Y <= top.Max.Y ? node.ChildA : node.ChildB,
            point.Y <= top.Max.Y ? top : bottom, point, out leaf, out leafBounds);
    }

    private static DockNode? FindPanelLeaf(DockNode? node, DockPanel panel)
    {
        if (node == null) return null;
        if (node.IsLeaf)
        {
            if (node.Tabs == null) return null;
            for (int i = 0; i < node.Tabs.Count; i++)
                if (ReferenceEquals(node.Tabs[i], panel)) return node;
            return null;
        }
        return FindPanelLeaf(node.ChildA, panel) ?? FindPanelLeaf(node.ChildB, panel);
    }

    private static bool Contains(Rect bounds, Float2 point) =>
        point.X >= bounds.Min.X && point.X <= bounds.Max.X
        && point.Y >= bounds.Min.Y && point.Y <= bounds.Max.Y;
}
