// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.GUI.Panels;

namespace XEngine.Editor.Core;

/// <summary>
/// Scopes secondary-host invalidation and interactive presents so an idle floater is not
/// rebuilt because the main window had input, a modal, or a panel it does not show.
/// Theme / window / explicit-repaint still reach every host. Animation reaches a host
/// whose active tab is a Profiler, a previewing/recording Animation window, or a
/// package panel registered via <see cref="RegisterLiveTool"/>. Opt-in via
/// <c>--shell-paint-incremental</c>; off, every secondary still receives the shared reason.
/// </summary>
public static class EditorShellIdleHosts
{
    static readonly List<(Type Panel, Func<bool> Wants)> s_liveTools = new();

    /// <summary>Package panels (Timeline, later Animator / graphs) register here so Animation
    /// invalidation and the secondary present pump can reach a live preview without the
    /// editor core taking a compile-time dependency on the package type.</summary>
    public static void RegisterLiveTool(Type panelType, Func<bool> wants)
    {
        ArgumentNullException.ThrowIfNull(panelType);
        ArgumentNullException.ThrowIfNull(wants);
        for (int i = 0; i < s_liveTools.Count; i++)
        {
            if (s_liveTools[i].Panel == panelType)
            {
                s_liveTools[i] = (panelType, wants);
                return;
            }
        }
        s_liveTools.Add((panelType, wants));
    }

    public static void UnregisterLiveTool(Type panelType)
    {
        if (panelType == null) return;
        for (int i = s_liveTools.Count - 1; i >= 0; i--)
            if (s_liveTools[i].Panel == panelType) s_liveTools.RemoveAt(i);
    }

    static bool RegisteredLive(EditorSurfaceHost host)
    {
        for (int i = 0; i < s_liveTools.Count; i++)
        {
            if (host.HasVisiblePanel(s_liveTools[i].Panel) && s_liveTools[i].Wants())
                return true;
        }
        return false;
    }

    /// <summary>Whether this detached host should queue <paramref name="reason"/>.</summary>
    public static bool HostWantsReason(EditorSurfaceHost host, EditorSurfaceDirtyReason reason)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!Program.ShellPaintIncremental) return true;
        if ((reason & (EditorSurfaceDirtyReason.Theme
            | EditorSurfaceDirtyReason.ExplicitRepaint
            | EditorSurfaceDirtyReason.Window)) != 0)
            return true;
        if ((reason & EditorSurfaceDirtyReason.Selection) != 0
            && host.HasVisiblePanel(typeof(InspectorPanel)))
            return true;
        if ((reason & EditorSurfaceDirtyReason.SceneCamera) != 0
            && host.HasVisiblePanel(typeof(SceneViewPanel)))
            return true;
        if ((reason & EditorSurfaceDirtyReason.GameView) != 0
            && host.HasVisiblePanel(typeof(GameViewPanel)))
            return true;
        if ((reason & EditorSurfaceDirtyReason.AssetData) != 0
            && host.HasVisiblePanel(typeof(ProjectPanel)))
            return true;
        if ((reason & EditorSurfaceDirtyReason.Animation) != 0
            && (host.HasVisiblePanel(typeof(ProfilerPanel))
                || (host.HasVisiblePanel(typeof(AnimationClipWindow))
                    && AnimationClipWindow.WantsContinuousPresent)
                || (host.HasVisiblePanel(typeof(AnimatorWindow))
                    && AnimatorWindow.WantsContinuousPresent)
                || RegisteredLive(host)))
            return true;
        return false;
    }

    /// <summary>
    /// A detached host whose active tab is a live Profiler (recording, current-frame view, or
    /// listening), a previewing/recording Animation window, or a registered live tool
    /// (Timeline preview, later Animator / graphs) must present every editor tick.
    /// Historical inspection with recording/preview off stays idle.
    /// </summary>
    public static bool ShouldPumpHost(EditorSurfaceHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return (host.HasVisiblePanel(typeof(ProfilerPanel)) && ProfilerPanel.WantsContinuousPresent)
            || (host.HasVisiblePanel(typeof(AnimationClipWindow)) && AnimationClipWindow.WantsContinuousPresent)
            || (host.HasVisiblePanel(typeof(AnimatorWindow)) && AnimatorWindow.WantsContinuousPresent)
            || RegisteredLive(host);
    }

    /// <summary>
    /// Interactive present for a secondary host. Spreading context menus still paint on every
    /// surface they are mirrored onto; an unowned modal on the main window does not.
    /// </summary>
    public static bool SecondaryIsInteractive(bool pointerOverHost, bool spreadingMenu, bool modalOpen)
    {
        if (!Program.ShellPaintIncremental)
            return pointerOverHost || spreadingMenu || modalOpen;
        return pointerOverHost || spreadingMenu;
    }
}
