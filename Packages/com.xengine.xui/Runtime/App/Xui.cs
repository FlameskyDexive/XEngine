// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>Supplies the imported document a screen name lives in.</summary>
/// <remarks>
/// XUI does not go looking for assets itself. The engine resolves assets by GUID, and a UI layer that
/// guessed at paths would be the one place in the project where a moved file silently stops working.
/// The application registers what it loaded; XUI only asks.
/// </remarks>
public interface IXuiDocumentSource
{
    XuiDocumentAsset? Resolve(string screenName);
}

/// <summary>A plain screen-name → document table. Fill it as documents load.</summary>
public sealed class XuiDocumentTable : IXuiDocumentSource
{
    private readonly Dictionary<string, XuiDocumentAsset> _documents = new(StringComparer.Ordinal);

    /// <summary>Registers every screen a document declares.</summary>
    public void Add(XuiDocumentAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        XuiDocument? document = asset.Document;
        if (document == null) return;

        for (int i = 0; i < document.Screens.Count; i++)
            _documents[document.Screens[i].Name] = asset;
    }

    public void Remove(string screenName) => _documents.Remove(screenName);

    public void Clear() => _documents.Clear();

    public XuiDocumentAsset? Resolve(string screenName)
        => _documents.TryGetValue(screenName, out XuiDocumentAsset? asset) ? asset : null;
}

/// <summary>One built screen. The controller is optional so a document can be previewed on its own.</summary>
public sealed class XuiOpenScreen
{
    internal XuiOpenScreen(string screenName, XuiLayerKind layer, XuiScreenInstance instance, XuiController? controller)
    {
        ScreenName = screenName;
        Layer = layer;
        Instance = instance;
        Controller = controller;
    }

    public string ScreenName { get; }
    public XuiLayerKind Layer { get; }
    public XuiScreenInstance Instance { get; internal set; }

    /// <summary>Null for a preview opened straight from a document.</summary>
    public XuiController? Controller { get; }

    public bool Visible { get; internal set; }
}

/// <summary>
/// Opening, hiding, and closing screens.
/// </summary>
/// <remarks>
/// <para>
/// Game code opens a screen by its <b>controller type</b>, so a renamed or deleted screen is a compile
/// error at every call site rather than a silent no-op at runtime.
/// </para>
/// <para>
/// <see cref="ShowDocument"/> is the exception, and it exists for the editor: it builds a screen with
/// no controller at all, which is what lets an agent open a <c>.xui</c> and look at it before any C#
/// has been written for it.
/// </para>
/// </remarks>
public static partial class Xui
{
    private static readonly Dictionary<Type, XuiRegistration> Registrations = new();
    private static readonly Dictionary<string, Type> RegistrationsByScreen = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, XuiOpenScreen> Open = new(StringComparer.Ordinal);

    private static IXuiDocumentSource _documents = new XuiDocumentTable();

    /// <summary>Where documents come from. Defaults to an empty table.</summary>
    public static IXuiDocumentSource Documents
    {
        get => _documents;
        set => _documents = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The scene screens are created in. Defaults to <see cref="Scene.Current"/>; set it when the UI
    /// belongs to a scene other than the active one.
    /// </summary>
    public static Scene? Scene { get; set; }

    /// <summary>The theme name applied to newly built screens. Null uses each document's first theme.</summary>
    public static string? Theme { get; set; }

    /// <summary>Screens currently built, whether or not they have a controller.</summary>
    public static IReadOnlyCollection<string> OpenScreens => Open.Keys;

    public static XuiOpenScreen? Screen(string screenName)
        => Open.TryGetValue(screenName, out XuiOpenScreen? open) ? open : null;

    // ------------------------------------------------------------------
    //  Registration
    // ------------------------------------------------------------------

    /// <summary>
    /// Registers a controller type. The scaffolder emits a call to this from the generated file, so a
    /// screen registers itself by existing and there is no central list to keep in step.
    /// </summary>
    public static void Register<TController>(string screenName, XuiLayerKind? layer = null)
        where TController : XuiController, new()
    {
        if (string.IsNullOrEmpty(screenName)) throw new ArgumentException("screenName is required.");
        Registrations[typeof(TController)] = new XuiRegistration(screenName, layer, () => new TController());
        RegistrationsByScreen[screenName] = typeof(TController);
    }

    public static void Unregister<TController>() where TController : XuiController
    {
        if (Registrations.Remove(typeof(TController), out XuiRegistration registration))
            RegistrationsByScreen.Remove(registration.ScreenName);
    }

    /// <summary>The screen a controller type is registered for, or null when it is not registered.</summary>
    public static string? RegisteredScreen<TController>() where TController : XuiController
        => Registrations.TryGetValue(typeof(TController), out XuiRegistration registration)
            ? registration.ScreenName
            : null;

    /// <summary>Tears down every open screen and forgets every registration.</summary>
    public static void Reset()
    {
        CloseAll();
        Registrations.Clear();
        RegistrationsByScreen.Clear();
        Scene = null;
        Theme = null;
    }

    /// <summary>Tears down every open screen, leaving registrations in place.</summary>
    public static void CloseAll()
    {
        foreach (KeyValuePair<string, XuiOpenScreen> entry in Open)
            Teardown(entry.Value);
        Open.Clear();
    }

    // ------------------------------------------------------------------
    //  Show / hide / close
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the screen if needed, then shows it. Re-showing an open screen reuses the hierarchy and
    /// the controller — <c>OnRegisterEvents</c> does not run twice, which is what stops a re-open from
    /// stacking duplicate handlers.
    /// </summary>
    public static TController Show<TController>(object? context = null)
        where TController : XuiController, new()
    {
        if (!Registrations.TryGetValue(typeof(TController), out XuiRegistration registration))
        {
            throw new InvalidOperationException(
                typeof(TController).Name + " is not registered. The generated " + typeof(TController).Name
                + "View.g.cs calls Xui.Register for it — re-run xui_scaffold if it is missing, or call "
                + "Xui.Register<" + typeof(TController).Name + "> yourself.");
        }

        XuiOpenScreen open = GetOrBuild(registration);
        SetVisible(open, true, context);
        return (TController)open.Controller!;
    }

    /// <summary>
    /// Builds and shows a screen straight from a document, with no controller.
    /// </summary>
    /// <remarks>
    /// This is the editor and agent path: open a <c>.xui</c> and look at it. Nothing is wired, so
    /// nothing responds — which is exactly right for checking that a layout is what the document says.
    /// </remarks>
    public static XuiOpenScreen ShowDocument(XuiDocumentAsset asset, string screenName)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (Open.TryGetValue(screenName, out XuiOpenScreen? already))
        {
            SetVisible(already, true, null);
            return already;
        }

        ScreenDef screen = asset.Document?.FindScreen(screenName)
            ?? throw new InvalidOperationException(
                asset.ParseError != null
                    ? "The document does not parse: " + asset.ParseError
                    : "Screen \"" + screenName + "\" is not declared in " + asset.AssetPath + ".");

        XuiScreenInstance instance = Build(asset, screenName, screen.Layer);
        var open = new XuiOpenScreen(screenName, screen.Layer, instance, controller: null);
        instance.Root.Enabled = false;
        Open[screenName] = open;
        SetVisible(open, true, null);
        return open;
    }

    /// <summary>
    /// Shows a screen by name, using its registered controller when there is one and building straight
    /// from the document when there is not.
    /// </summary>
    /// <remarks>
    /// Game code should prefer <see cref="Show{TController}"/> — a type cannot be misspelled. This
    /// exists for the router, where the name comes from a link, and for the editor, where a document
    /// may have no controller yet.
    /// </remarks>
    public static XuiOpenScreen ShowScreen(string screenName, object? context = null)
    {
        if (string.IsNullOrEmpty(screenName)) throw new ArgumentException("screenName is required.");

        if (RegistrationsByScreen.TryGetValue(screenName, out Type? controllerType)
            && Registrations.TryGetValue(controllerType, out XuiRegistration registration))
        {
            XuiOpenScreen controlled = GetOrBuild(registration);
            SetVisible(controlled, true, context);
            return controlled;
        }

        if (Open.TryGetValue(screenName, out XuiOpenScreen? already))
        {
            SetVisible(already, true, context);
            return already;
        }

        return ShowDocument(ResolveDocument(screenName), screenName);
    }

    /// <summary>Hides a screen without destroying it. The next Show reuses everything.</summary>
    public static void Hide<TController>() where TController : XuiController
    {
        if (FindOpen<TController>() is { } open) SetVisible(open, false, null);
    }

    public static bool Hide(string screenName)
    {
        if (!Open.TryGetValue(screenName, out XuiOpenScreen? open)) return false;
        SetVisible(open, false, null);
        return true;
    }

    /// <summary>Hides and destroys a screen. The next Show rebuilds it from the document.</summary>
    public static void Close<TController>() where TController : XuiController
    {
        if (FindOpen<TController>() is { } open) Close(open.ScreenName);
    }

    public static bool Close(string screenName)
    {
        if (!Open.TryGetValue(screenName, out XuiOpenScreen? open)) return false;

        SetVisible(open, false, null);
        Teardown(open);
        Open.Remove(screenName);
        return true;
    }

    /// <summary>Looks up a built controller, or null when the screen is not open.</summary>
    public static TController? Find<TController>() where TController : XuiController
        => FindOpen<TController>()?.Controller as TController;

    /// <summary>
    /// Rebuilds an open screen from its (re-imported) document, keeping the controller and its state.
    /// </summary>
    /// <remarks>
    /// This is the hot-reload entry point. The controller instance, its fields, and its subscriptions
    /// all survive; only the hierarchy and the view binding are replaced. <c>OnRegisterEvents</c>
    /// deliberately does not run again — re-running it would double every subscription the controller
    /// made, turning a reload into a bug.
    /// </remarks>
    public static bool Reload(string screenName)
    {
        if (!Open.TryGetValue(screenName, out XuiOpenScreen? open)) return false;

        // EngineObject overloads ==, so the pattern (not a null check) is what asks whether the asset
        // is still usable rather than merely non-null.
        if (_documents.Resolve(screenName) is not { IsDisposed: false } asset) return false;

        bool wasVisible = open.Visible;
        DestroyHierarchy(open.Instance);

        XuiScreenInstance instance = Build(asset, screenName, open.Layer);
        open.Instance = instance;
        instance.Root.Enabled = false;

        if (open.Controller is { } controller)
        {
            controller.BindInstance(instance);
            controller.OnBuildView();
        }

        open.Visible = false;
        if (wasVisible) SetVisible(open, true, null);
        return true;
    }

    /// <summary>Reloads every open screen. Used when a document import may have touched several.</summary>
    public static int ReloadAll()
    {
        var names = new List<string>(Open.Keys);
        int reloaded = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (Reload(names[i])) reloaded++;
        }
        return reloaded;
    }

    // ------------------------------------------------------------------
    //  Internals
    // ------------------------------------------------------------------

    private static XuiOpenScreen GetOrBuild(XuiRegistration registration)
    {
        if (Open.TryGetValue(registration.ScreenName, out XuiOpenScreen? existing)) return existing;

        XuiDocumentAsset asset = ResolveDocument(registration.ScreenName);
        XuiController controller = registration.Create();
        controller.ScreenName = registration.ScreenName;
        controller.Layer = registration.Layer
            ?? asset.Document?.FindScreen(registration.ScreenName)?.Layer
            ?? XuiLayerKind.Normal;

        controller.OnInitCoreData();

        XuiScreenInstance instance = Build(asset, registration.ScreenName, controller.Layer);
        controller.BindInstance(instance);
        controller.OnBuildView();

        if (!controller.EventsRegistered)
        {
            controller.OnRegisterEvents();
            controller.EventsRegistered = true;
        }

        // Hidden until Show says otherwise, so OnShow is the only thing that makes a screen appear.
        instance.Root.Enabled = false;
        var open = new XuiOpenScreen(registration.ScreenName, controller.Layer, instance, controller);
        Open[registration.ScreenName] = open;
        return open;
    }

    private static XuiDocumentAsset ResolveDocument(string screenName)
        => _documents.Resolve(screenName)
        ?? throw new InvalidOperationException(
            "No document registered for screen \"" + screenName
            + "\". Add the imported .xui asset to Xui.Documents before opening it.");

    private static XuiScreenInstance Build(XuiDocumentAsset asset, string screenName, XuiLayerKind layer)
    {
        if (asset.Document == null)
        {
            throw new InvalidOperationException(
                "The document for screen \"" + screenName + "\" does not parse: " + asset.ParseError);
        }

        // EngineObject overloads ==, so a plain null check asks the wrong question about a destroyed
        // scene. The pattern covers null and disposed at once.
        Scene? scene = Scene ?? XEngine.Runtime.Resources.Scene.Current;
        XuiRoot? root = scene is { IsDisposed: false } live ? XuiRoot.GetOrCreate(live) : null;

        var options = new XuiBuildOptions
        {
            Scene = scene,
            Parent = root?.RootFor(layer),
            SortOrder = root?.NextSortOrder(layer) ?? 0,
        };
        if (Theme != null) options.Palette = XuiPalette.FromDocument(asset.Document, Theme);

        return asset.BuildScreen(screenName, options)
            ?? throw new InvalidOperationException(
                "Screen \"" + screenName + "\" is not declared in " + asset.AssetPath + ".");
    }

    private static void SetVisible(XuiOpenScreen open, bool visible, object? context)
    {
        XuiController? controller = open.Controller;

        if (open.Visible == visible)
        {
            // Re-showing a visible screen still delivers the context: callers use Show to hand a screen
            // new data, and silently dropping it would surface later as stale UI.
            if (visible && controller != null)
            {
                controller.IsVisible = true;
                controller.OnShow(context);
            }
            return;
        }

        open.Visible = visible;
        if (open.Instance.Root is { IsDisposed: false } root)
        {
            XuiAnimationTicker? ticker = root.GetComponent<XuiAnimationTicker>();

            if (visible)
            {
                // Enable first so "show" has live objects to move, and cancel any exit still waiting to
                // switch the root off — otherwise a quick hide/show pair blanks the screen mid-entrance.
                if (ticker is { IsDisposed: false }) ticker.DisableWhenIdle = false;
                root.Enabled = true;
                PlayTrigger(open, "show");
            }
            else if (PlayTrigger(open, "hide") > 0 && ticker is { IsDisposed: false })
            {
                // Leave the screen up until its exit has played out; the ticker switches it off.
                ticker.DisableWhenIdle = true;
            }
            else
            {
                root.Enabled = false;
            }
        }

        if (controller == null) return;
        controller.IsVisible = visible;
        if (visible) controller.OnShow(context);
        else controller.OnHide();
    }

    /// <summary>
    /// Plays a named transition on every animator in a screen.
    /// </summary>
    /// <remarks>
    /// Screens fire <c>show</c> and <c>hide</c> themselves so a document can declare an entrance
    /// without any controller code. Any other trigger name is a controller's to play.
    /// </remarks>
    public static int PlayTrigger(string screenName, string trigger)
        => Open.TryGetValue(screenName, out XuiOpenScreen? open) ? PlayTrigger(open, trigger) : 0;

    private static int PlayTrigger(XuiOpenScreen open, string trigger)
    {
        if (open.Instance.Root is not { IsDisposed: false } root) return 0;
        return PlayTrigger(root, trigger);
    }

    private static int PlayTrigger(GameObject node, string trigger)
    {
        int played = 0;
        if (node.GetComponent<XuiAnimator>() is { IsDisposed: false } animator
            && animator.Play(trigger))
        {
            played++;
        }

        IReadOnlyList<GameObject> children = node.Children;
        for (int i = 0; i < children.Count; i++) played += PlayTrigger(children[i], trigger);
        return played;
    }

    /// <summary>
    /// Advances every animator in a screen. The per-screen ticker does this each frame; this exists so
    /// a test or tool can step time deterministically instead of waiting for frames.
    /// </summary>
    public static bool TickAnimations(string screenName, float deltaSeconds)
    {
        if (!Open.TryGetValue(screenName, out XuiOpenScreen? open)) return false;
        if (open.Instance.Root is not { IsDisposed: false } root) return false;
        if (root.GetComponent<XuiAnimationTicker>() is not { IsDisposed: false } ticker) return false;

        ticker.Tick(deltaSeconds);
        return true;
    }

    private static void Teardown(XuiOpenScreen open)
    {
        if (open.Controller is { } controller)
        {
            controller.BeforeUnload();
            controller.ReleaseView();
            controller.EventsRegistered = false;
            controller.IsVisible = false;
        }
        DestroyHierarchy(open.Instance);
        open.Visible = false;
    }

    private static void DestroyHierarchy(XuiScreenInstance instance)
    {
        if (instance.Root is { IsDisposed: false } root) root.Dispose();
    }

    private static XuiOpenScreen? FindOpen<TController>() where TController : XuiController
    {
        foreach (KeyValuePair<string, XuiOpenScreen> entry in Open)
        {
            if (entry.Value.Controller is TController) return entry.Value;
        }
        return null;
    }

    private readonly struct XuiRegistration
    {
        public XuiRegistration(string screenName, XuiLayerKind? layer, Func<XuiController> create)
        {
            ScreenName = screenName;
            Layer = layer;
            Create = create;
        }

        public string ScreenName { get; }

        /// <summary>Null means "whatever the document declares".</summary>
        public XuiLayerKind? Layer { get; }

        public Func<XuiController> Create { get; }
    }
}
