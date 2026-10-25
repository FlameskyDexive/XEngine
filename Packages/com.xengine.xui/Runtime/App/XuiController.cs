// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// The business half of a screen. Lifecycle only — no element ids, no GameObjects.
/// </summary>
/// <remarks>
/// <para>
/// The hooks run in a fixed order and each one exists because something is only valid from that point
/// on: <see cref="OnInitCoreData"/> before anything is built, <see cref="OnBuildView"/> once the
/// hierarchy and binding exist, <see cref="OnRegisterEvents"/> once (never re-run on a re-show), then
/// <see cref="OnShow"/> / <see cref="OnHide"/> per visibility change, and
/// <see cref="BeforeUnload"/> before the hierarchy is destroyed.
/// </para>
/// <para>
/// This is XPlay XUI's <c>IXUIEventHandler</c> contract, collapsed into virtuals on a base class:
/// XEngine screens are GameObject trees rather than ET entities, so a separate handler object would be
/// a file with no state of its own.
/// </para>
/// </remarks>
public abstract class XuiController
{
    /// <summary>Screen name from <see cref="XuiScreenAttribute"/>. Set when the screen opens.</summary>
    public string ScreenName { get; internal set; } = string.Empty;

    public XuiLayerKind Layer { get; internal set; } = XuiLayerKind.Normal;

    /// <summary>The assembled screen. Null before the first build.</summary>
    public XuiScreenInstance? Instance { get; internal set; }

    public bool IsVisible { get; internal set; }

    /// <summary>True once <see cref="OnRegisterEvents"/> has run for this controller.</summary>
    internal bool EventsRegistered { get; set; }

    /// <summary>Runs before the hierarchy is built; the only place to adjust what the build needs.</summary>
    protected internal virtual void OnInitCoreData() { }

    /// <summary>The hierarchy and the view binding both exist from here on.</summary>
    protected internal virtual void OnBuildView() { }

    /// <summary>Runs exactly once per controller. Subscribe here, not in <see cref="OnShow"/>.</summary>
    protected internal virtual void OnRegisterEvents() { }

    /// <summary>Runs every time the screen becomes visible, with whatever the caller passed to Show.</summary>
    protected internal virtual void OnShow(object? context) { }

    protected internal virtual void OnHide() { }

    /// <summary>Last call before the hierarchy is destroyed. Release anything the screen owned.</summary>
    protected internal virtual void BeforeUnload() { }

    /// <summary>Points the controller at a rebuilt screen. Called by the hot-reload path.</summary>
    internal abstract void BindInstance(XuiScreenInstance instance);

    internal abstract void ReleaseView();
}

/// <summary>A controller with its generated view binding.</summary>
public abstract class XuiController<TView> : XuiController where TView : XuiView
{
    private TView? _view;

    /// <summary>
    /// The generated binding. Business code reaches every control through this and nothing else.
    /// </summary>
    public TView View => _view
        ?? throw new InvalidOperationException(
            "Screen \"" + ScreenName + "\" has no view yet. View is available from OnBuildView onwards.");

    /// <summary>
    /// Builds the binding. The scaffolder generates this override so no reflection is involved — which
    /// also keeps the screen working under NativeAOT.
    /// </summary>
    protected abstract TView CreateView(XuiScreenInstance screen);

    internal override void BindInstance(XuiScreenInstance instance)
    {
        Instance = instance;
        if (_view == null) _view = CreateView(instance);
        else _view.Rebind(instance);
    }

    internal override void ReleaseView()
    {
        _view?.Dispose();
        _view = null;
        Instance = null;
    }
}
