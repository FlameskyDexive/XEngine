// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>
/// Maps popup coordinates between editor surfaces so a menu opened on one window can be painted
/// onto another in the same screen-space location.
/// </summary>
internal static class EditorPopupMapping
{
    internal static EditorWorkspace? Workspace;
    internal static Func<Paper?>? MainPaper;

    internal static void Install(EditorWorkspace workspace, Func<Paper?> mainPaper)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        MainPaper = mainPaper ?? throw new ArgumentNullException(nameof(mainPaper));
        ContextMenu.MapPointBetweenPapers = Map;
        ContextMenu.PointerSurfaceFilter = OwnsPointer;
        DragDrop.PointerSurfaceFilter = OwnsPointer;
        DragDrop.KeyboardSurfaceFilter = IsSurfaceFocused;
        DragDrop.PointerOutsideSurfaces = IsPointerOutsideWorkspace;
        DragDrop.ButtonDownProvider = DragButtonDown;
        DragDrop.PointerPositionProvider = DragPointerPosition;
    }

    private static bool DragButtonDown(Paper paper) => Workspace is { } workspace && FindHost(workspace, paper) != null
        ? EditorNativeSurfaceManager.IsDragButtonDown(paper) : paper.IsPointerDown(PaperMouseBtn.Left);

    internal static void Uninstall(EditorWorkspace workspace)
    {
        if (!ReferenceEquals(Workspace, workspace)) return;
        DragDrop.Cancel();
        DragDrop.PointerSurfaceFilter = DragDrop.KeyboardSurfaceFilter = DragDrop.ButtonDownProvider = null;
        DragDrop.PointerOutsideSurfaces = null;
        DragDrop.PointerPositionProvider = null;
        ContextMenu.MapPointBetweenPapers = null;
        ContextMenu.PointerSurfaceFilter = null;
        Workspace = null; MainPaper = null;
    }

    private static Float2 DragPointerPosition(Paper paper)
    {
        if (Workspace is not { } workspace || FindHost(workspace, paper) is not { } host
            || !EditorNativeSurfaceManager.TryGetDragPointer(out var screen)) return paper.PointerPos;
        float scale = Math.Max(.01f, host.DpiScale * EditorTheme.UserScale);
        return new Float2((float)(screen.X - host.DockingScreenBounds.Min.X) / scale,
            (float)(screen.Y - host.DockingScreenBounds.Min.Y) / scale);
    }

    private static bool IsPointerOutsideWorkspace()
    {
        if (Workspace is not { } workspace) return false;
        for (int i = 0; i < workspace.Hosts.Count; i++)
        {
            var host = workspace.Hosts[i];
            if (EditorNativeSurfaceManager.IsPointerOverWindow(host.Id == 0
                ? Runtime.Window.InternalWindow : host.NativeWindow)) return false;
        }
        return true;
    }

    internal static bool OwnsPointer(Paper paper)
    {
        EditorWorkspace? workspace = Workspace;
        if (workspace == null) return true;
        EditorSurfaceHost? host = FindHost(workspace, paper);
        if (host == null) return true; // offscreen/caller-owned Paper
        return EditorNativeSurfaceManager.IsPointerOverWindow(host.Id == 0
            ? Runtime.Window.InternalWindow : host.NativeWindow);
    }

    /// <summary>Keyboard activation is independent of pointer ownership.</summary>
    internal static bool IsSurfaceFocused(Paper paper)
    {
        if (Workspace is not { } workspace) return true;
        EditorSurfaceHost? host = FindHost(workspace, paper);
        if (host == null) return true; // offscreen/caller-owned Paper
        return host.Id == 0 ? Runtime.Window.IsFocused : host.IsNativeFocused;
    }

    internal static (float x, float y) Map(Paper from, Paper to, float x, float y)
    {
        if (ReferenceEquals(from, to)) return (x, y);
        EditorWorkspace? workspace = Workspace;
        if (workspace == null) return (x, y);

        EditorSurfaceHost? source = FindHost(workspace, from);
        EditorSurfaceHost? target = FindHost(workspace, to);
        if (source == null || target == null) return (x, y);

        MapHosts(source, x, y, target, out float mappedX, out float mappedY);
        return (mappedX, mappedY);
    }

    internal static void MapHosts(EditorSurfaceHost from, float x, float y,
        EditorSurfaceHost to, out float mappedX, out float mappedY)
    {
        float sourceScale = Math.Max(0.01f, from.DpiScale * EditorTheme.UserScale);
        float targetScale = Math.Max(0.01f, to.DpiScale * EditorTheme.UserScale);
        double screenX = from.DockingScreenBounds.Min.X + x * sourceScale;
        double screenY = from.DockingScreenBounds.Min.Y + y * sourceScale;
        mappedX = (float)((screenX - to.DockingScreenBounds.Min.X) / targetScale);
        mappedY = (float)((screenY - to.DockingScreenBounds.Min.Y) / targetScale);
    }

    private static EditorSurfaceHost? FindHost(EditorWorkspace workspace, Paper paper)
    {
        Paper? mainPaper = MainPaper?.Invoke();
        for (int i = 0; i < workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = workspace.Hosts[i];
            if (ReferenceEquals(host.Paper, paper)) return host;
            if (host.Id == 0 && mainPaper != null && ReferenceEquals(paper, mainPaper))
                return host;
        }
        return null;
    }
}
