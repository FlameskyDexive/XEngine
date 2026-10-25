// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>What a navigation did, so the caller can tell "opened" from "already there".</summary>
public enum XuiNavigationResult
{
    /// <summary>The screen was built and shown.</summary>
    Opened,

    /// <summary>The screen was already open; it was re-shown with the new query.</summary>
    Reshown,

    /// <summary>The link named a screen no document declares.</summary>
    UnknownScreen,

    /// <summary>The link was not a valid <c>scheme://Screen?query</c>.</summary>
    MalformedLink,
}

/// <summary>
/// Opens screens from deep links, and enforces which screens may be open at once.
/// </summary>
/// <remarks>
/// <para>
/// Two things live here that do not belong in <see cref="Xui"/>. The first is links: a screen should be
/// reachable from a notification, a shared URL, or a tutorial step without that caller knowing a C#
/// type. The second is exclusivity — a <c>Normal</c> screen is full-screen content, so opening one
/// closes the others. Leaving that to call sites is how two menus end up drawn on top of each other,
/// with the bug reproducing only in the order someone happened to tap.
/// </para>
/// <para>
/// <c>Fixed</c>, <c>PopUp</c> and <c>Other</c> stack instead: a HUD and a modal are additive by
/// definition.
/// </para>
/// </remarks>
public static class XuiRouter
{
    private static readonly List<XuiRouteQuery> History = new();

    /// <summary>Link scheme this router answers to. Set it to the app's own id for external links.</summary>
    public static string Scheme { get; set; } = XuiLink.DefaultScheme;

    /// <summary>How many entries <see cref="Back"/> can walk. Older entries are dropped.</summary>
    public static int MaxHistory { get; set; } = 32;

    /// <summary>The links navigated to, oldest first.</summary>
    public static IReadOnlyList<XuiRouteQuery> Entries => History;

    /// <summary>The link currently showing, or null before the first navigation.</summary>
    public static XuiRouteQuery? Current => History.Count == 0 ? null : History[History.Count - 1];

    /// <summary>
    /// Opens the screen a link addresses, passing the query as the controller's <c>OnShow</c> context.
    /// </summary>
    public static XuiNavigationResult Navigate(string link)
    {
        if (!TryParse(link, out XuiRouteQuery? query))
            return XuiNavigationResult.MalformedLink;

        return Navigate(query!);
    }

    /// <summary>Opens a screen by name with parameters, without going through a link string.</summary>
    public static XuiNavigationResult Navigate(string screenName, IReadOnlyDictionary<string, string> parameters)
        => Navigate(new XuiRouteQuery(screenName, parameters));

    public static XuiNavigationResult Navigate(XuiRouteQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        XuiDocumentAsset? asset = Xui.Documents.Resolve(query.ScreenName);
        ScreenDef? screen = asset?.Document?.FindScreen(query.ScreenName);
        if (screen == null) return XuiNavigationResult.UnknownScreen;

        bool wasOpen = Xui.Screen(query.ScreenName) != null;

        // A Normal screen is exclusive content. Closing the others here rather than at call sites is
        // what stops two menus from being drawn at once depending on tap order.
        if (screen.Layer == XuiLayerKind.Normal) CloseOtherNormalScreens(query.ScreenName);

        Xui.ShowScreen(query.ScreenName, query);
        Record(query, screen.Layer);

        return wasOpen ? XuiNavigationResult.Reshown : XuiNavigationResult.Opened;
    }

    /// <summary>
    /// Returns to the previous link, closing the current screen. Returns false at the start of history.
    /// </summary>
    public static bool Back()
    {
        if (History.Count < 2) return false;

        XuiRouteQuery leaving = History[History.Count - 1];
        History.RemoveAt(History.Count - 1);
        XuiRouteQuery target = History[History.Count - 1];

        Xui.Close(leaving.ScreenName);

        // Re-navigating would push a duplicate history entry, so the target is shown directly.
        Xui.ShowScreen(target.ScreenName, target);
        return true;
    }

    /// <summary>Forgets the navigation history. Does not close anything.</summary>
    public static void ClearHistory() => History.Clear();

    /// <summary>Parses a link with this router's scheme. See <see cref="XuiLink"/> for the grammar.</summary>
    public static bool TryParse(string link, out XuiRouteQuery? query)
        => XuiLink.TryParse(link, out query, Scheme);

    private static void CloseOtherNormalScreens(string keep)
    {
        List<string>? toClose = null;
        foreach (string name in Xui.OpenScreens)
        {
            if (string.Equals(name, keep, StringComparison.Ordinal)) continue;
            XuiOpenScreen? open = Xui.Screen(name);
            if (open == null || open.Layer != XuiLayerKind.Normal) continue;
            (toClose ??= new List<string>()).Add(name);
        }

        if (toClose == null) return;
        for (int i = 0; i < toClose.Count; i++) Xui.Close(toClose[i]);
    }

    /// <summary>
    /// Adds a navigation to the back history — but only a <c>Normal</c> one.
    /// </summary>
    /// <remarks>
    /// A HUD or a modal is not a place you came from. Recording them would make Back walk sideways
    /// through overlays that were never content, so opening a HUD after a screen would make Back
    /// "return" to the HUD and leave the screen closed.
    /// </remarks>
    private static void Record(XuiRouteQuery query, XuiLayerKind layer)
    {
        if (layer != XuiLayerKind.Normal) return;

        // Re-navigating to the same screen replaces the entry instead of stacking duplicates, so Back
        // does not walk through several copies of where you already are.
        if (History.Count > 0
            && string.Equals(History[History.Count - 1].ScreenName, query.ScreenName, StringComparison.Ordinal))
        {
            History[History.Count - 1] = query;
            return;
        }

        History.Add(query);
        while (History.Count > MaxHistory && MaxHistory > 0) History.RemoveAt(0);
    }
}
