using System;
using System.Collections.Generic;

using Silk.NET.Windowing;

using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime.GUI;
using XEngine.Runtime.RHI;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>One editor presentation host and its optional native presentation binding.</summary>
public sealed class EditorSurfaceHost
{
    private IWindow? _nativeWindow;
    private IWindowSurface? _windowSurface;
    private PaperRenderer? _paperRenderer;
    private Paper? _paper;
    private Rect? _dockingScreenBounds;

    public EditorSurfaceHost(int id, DockSpace dockSpace, Rect screenBounds)
    {
        if (id < 0) throw new ArgumentOutOfRangeException(nameof(id));
        DockSpace = dockSpace ?? throw new ArgumentNullException(nameof(dockSpace));
        Id = id;
        ScreenBounds = screenBounds;
        // Native hosts already show the XEngine mark on the OS caption. In-client Paper
        // floaters still draw it via DockSpace when fw != null.
    }

    public int Id { get; }
    public DockSpace DockSpace { get; private set; }
    /// <summary>Persisted native window bounds in logical screen coordinates.</summary>
    public Rect ScreenBounds { get; set; }
    /// <summary>Physical client-area bounds used only for cross-window pointer hit testing.</summary>
    public Rect DockingScreenBounds => _dockingScreenBounds ?? ScreenBounds;
    /// <summary>Native monitor device identity used for placement fallback on restart.</summary>
    public string MonitorId { get; internal set; } = string.Empty;
    /// <summary>Per-window logical-to-physical scale captured with the host bounds.</summary>
    public float DpiScale { get; internal set; } = 1f;
    public bool IsVisible { get; set; } = true;
    public EditorFrameScheduler FrameScheduler { get; } = new();
    public long RenderedFrameCount { get; internal set; }

    internal bool IsPanelVisible(DockPanel panel)
    {
        if (IsPanelVisible(DockSpace.Root, panel)) return true;
        for (int i = 0; i < DockSpace.FloatingWindows.Count; i++)
            if (IsPanelVisible(DockSpace.FloatingWindows[i].Node, panel)) return true;
        return false;
    }

    /// <summary>True when <paramref name="panelType"/> is the active tab of some leaf on this host.</summary>
    public bool HasVisiblePanel(Type panelType)
    {
        ArgumentNullException.ThrowIfNull(panelType);
        if (HasVisiblePanel(DockSpace.Root, panelType)) return true;
        for (int i = 0; i < DockSpace.FloatingWindows.Count; i++)
            if (HasVisiblePanel(DockSpace.FloatingWindows[i].Node, panelType)) return true;
        return false;
    }

    internal static bool IsPanelVisible(DockNode? node, DockPanel panel)
    {
        if (node == null) return false;
        if (!node.IsLeaf)
            return IsPanelVisible(node.ChildA, panel) || IsPanelVisible(node.ChildB, panel);
        return node.Tabs is { Count: > 0 } tabs
            && ReferenceEquals(tabs[Math.Clamp(node.ActiveTabIndex, 0, tabs.Count - 1)], panel);
    }

    internal static bool HasVisiblePanel(DockNode? node, Type panelType)
    {
        if (node == null) return false;
        if (!node.IsLeaf)
            return HasVisiblePanel(node.ChildA, panelType) || HasVisiblePanel(node.ChildB, panelType);
        if (node.Tabs is not { Count: > 0 } tabs) return false;
        return panelType.IsInstanceOfType(tabs[Math.Clamp(node.ActiveTabIndex, 0, tabs.Count - 1)]);
    }
    public IWindow? NativeWindow => _nativeWindow;
    public IWindowSurface? WindowSurface => _windowSurface;
    public PresentationSurfaceId PresentationSurface { get; private set; }
    public bool HasNativeWindow => _nativeWindow != null;
    internal bool IsNativeFocused { get; private set; }

    internal void SetNativeFocused(bool focused)
    {
        if (IsNativeFocused == focused) return;
        IsNativeFocused = focused;
        if (!focused) _paper?.CancelDragEdit();
        FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Window);
    }
    public PaperRenderer? PaperRenderer => _paperRenderer;
    public Paper? Paper => _paper;

    /// <summary>Title shown by a detached native window, derived from its active docked panel.</summary>
    public string WindowTitle
    {
        get
        {
            DockPanel? panel = FindActivePanel(DockSpace.Root);
            for (int i = 0; panel == null && i < DockSpace.FloatingWindows.Count; i++)
                panel = FindActivePanel(DockSpace.FloatingWindows[i].Node);
            return panel?.Title ?? "Untitled";
        }
    }

    /// <summary>Whether this host still owns at least one docked or floating panel.</summary>
    public bool HasPanels
    {
        get
        {
            if (ContainsPanels(DockSpace.Root)) return true;
            for (int i = 0; i < DockSpace.FloatingWindows.Count; i++)
                if (ContainsPanels(DockSpace.FloatingWindows[i].Node)) return true;
            return false;
        }
    }

    internal void AttachNativeWindow(
        IWindow nativeWindow,
        IWindowSurface windowSurface,
        PresentationSurfaceId presentationSurface)
    {
        ArgumentNullException.ThrowIfNull(nativeWindow);
        ArgumentNullException.ThrowIfNull(windowSurface);
        if (_nativeWindow != null)
            throw new InvalidOperationException("The editor surface host already owns a native window.");

        _nativeWindow = nativeWindow;
        _windowSurface = windowSurface;
        PresentationSurface = presentationSurface;
        // Secondary windows are created hidden so Win32 chrome can be applied before first
        // paint. Do not copy that hidden flag here — RenderSecondaryHosts skips IsVisible=false
        // hosts, which would leave the HWND showing an empty white client forever.
    }

    internal void SetDockingScreenBounds(Rect bounds) => _dockingScreenBounds = bounds;

    internal void ReplaceDockSpace(DockSpace dockSpace)
    {
        ArgumentNullException.ThrowIfNull(dockSpace);
        dockSpace.ShowTabBarLeadingMark = DockSpace.ShowTabBarLeadingMark;
        DockSpace = dockSpace;
        FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Layout);
    }

    internal (IWindow? Window, IWindowSurface? Surface, PresentationSurfaceId PresentationSurface) DetachNativeWindow()
    {
        var detached = (_nativeWindow, _windowSurface, PresentationSurface);
        _nativeWindow = null;
        SetNativeFocused(false);
        _windowSurface = null;
        PresentationSurface = default;
        _dockingScreenBounds = null;
        return detached;
    }

    internal void AttachPaper(PaperRenderer renderer, Paper paper)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(paper);
        if (_paperRenderer != null)
            throw new InvalidOperationException("The editor surface host already owns a Paper renderer.");
        _paperRenderer = renderer;
        _paper = paper;
    }

    internal void ReleasePaper()
    {
        PaperRenderer? renderer = _paperRenderer;
        Paper? paper = _paper;
        paper?.CancelDragEdit();
        if (paper != null) OrigamiUI.DragDrop.CancelForSurface(paper);
        _paperRenderer = null;
        _paper = null;
        // Canvas owns font generations and invalidates borrowers before renderer teardown.
        // Clear host references first so repeated close remains safe even if disposal throws.
        if (paper != null) paper.Canvas.Dispose();
        else renderer?.Dispose();
    }

    internal bool OwnsPanel(DockPanel panel)
    {
        if (OwnsPanel(DockSpace.Root, panel)) return true;
        for (int i = 0; i < DockSpace.FloatingWindows.Count; i++)
            if (OwnsPanel(DockSpace.FloatingWindows[i].Node, panel)) return true;
        return false;
    }

    private static bool OwnsPanel(DockNode? node, DockPanel panel)
    {
        if (node == null) return false;
        if (node.IsLeaf)
        {
            List<DockPanel>? tabs = node.Tabs;
            if (tabs == null) return false;
            for (int i = 0; i < tabs.Count; i++)
                if (ReferenceEquals(tabs[i], panel)) return true;
            return false;
        }

        return OwnsPanel(node.ChildA, panel) || OwnsPanel(node.ChildB, panel);
    }

    private static bool ContainsPanels(DockNode? node)
    {
        if (node == null) return false;
        if (node.IsLeaf) return node.Tabs is { Count: > 0 };
        return ContainsPanels(node.ChildA) || ContainsPanels(node.ChildB);
    }

    private static DockPanel? FindActivePanel(DockNode? node)
    {
        if (node == null) return null;
        if (node.IsLeaf)
        {
            List<DockPanel>? tabs = node.Tabs;
            if (tabs is not { Count: > 0 }) return null;
            int index = Math.Clamp(node.ActiveTabIndex, 0, tabs.Count - 1);
            return tabs[index];
        }

        return FindActivePanel(node.ChildA) ?? FindActivePanel(node.ChildB);
    }
}
