// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;

using ModelContextProtocol.Server;

using XEngine.Ai.Server;
using XEngine.Editor;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.XUI;
using XEngine.XUI.Core;

namespace XEngine.XUI.Editor;

/// <summary>
/// In-process MCP tools for live XUI screens. Registered when <c>com.xengine.xui</c> Editor is loaded;
/// the editor's <c>McpToolAssemblyScan</c> picks this assembly up with no editor-side change.
/// </summary>
/// <remarks>
/// <para>
/// These close the loop the offline tools cannot: <c>xui_validate</c> proves a document is legal,
/// but only a built screen shows whether an anchor actually resolved where the author meant. That is
/// what <c>runtime_xui_tree</c> reports — <b>measured</b> rects, not authored attributes — and what
/// <c>runtime_screenshot</c> then confirms in pixels.
/// </para>
/// <para>
/// A screen opens here <b>without a controller</b>. Nothing is wired, so nothing responds, which is
/// exactly right for checking that a layout is what the document says before any C# exists for it.
/// </para>
/// </remarks>
[McpServerToolType]
public static class XuiRuntimeTools
{
    [McpServerTool(Name = "runtime_xui_open", UseStructuredContent = true)]
    [Description("Import a .xui document and open one of its screens in the live editor scene, with no controller. Use runtime_screenshot afterwards to see it. Pass screenName to pick among several; omit it for the document's first screen.")]
    public static Task<XuiLiveScreenResult> Open(
        MainThreadDispatcher dispatcher,
        [Description("Project-relative .xui path, e.g. UI/login/Login.xui.")] string documentPath,
        [Description("Screen to open. Omit for the document's first screen.")] string? screenName = null)
        => dispatcher.InvokeOnMainThreadAsync(() => OpenScreen(documentPath, screenName));

    [McpServerTool(Name = "runtime_xui_state", UseStructuredContent = true)]
    [Description("List the XUI screens currently built in the live editor scene: name, layer, visibility, element count, and whether a controller is driving each one.")]
    public static Task<XuiLiveStateResult> State(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(CollectState);

    [McpServerTool(Name = "runtime_xui_tree", UseStructuredContent = true)]
    [Description("Read a live screen's hierarchy with MEASURED rects (x, y, width, height in canvas space) rather than authored attributes. Forces a layout pass first, at the given resolution, so the numbers are real without needing a GameView open. This is how you check that an anchor, size, or layout resolved where the document meant — a screenshot shows that something is wrong, this shows which element and by how much. Pass different width/height to check a responsive layout at several resolutions.")]
    public static Task<XuiLiveTreeResult> Tree(
        MainThreadDispatcher dispatcher,
        [Description("Screen name, as reported by runtime_xui_state.")] string screenName,
        [Description("Measure at this width in pixels. Default 1920.")] int width = 1920,
        [Description("Measure at this height in pixels. Default 1080.")] int height = 1080,
        [Description("Max nodes to return (1-2000). Default 500.")] int maxNodes = 500)
        => dispatcher.InvokeOnMainThreadAsync(() => ReadTree(screenName, width, height, maxNodes));

    [McpServerTool(Name = "runtime_xui_reload", UseStructuredContent = true)]
    [Description("Re-import a .xui document and rebuild every open screen it declares, keeping any controller and its state. This is the edit-save-look loop: change the document, call this, then runtime_screenshot.")]
    public static Task<XuiLiveScreenResult> Reload(
        MainThreadDispatcher dispatcher,
        [Description("Project-relative .xui path to re-import.")] string documentPath)
        => dispatcher.InvokeOnMainThreadAsync(() => ReloadDocument(documentPath));

    [McpServerTool(Name = "runtime_xui_close", UseStructuredContent = true)]
    [Description("Destroy a live XUI screen. Pass screenName for one, or omit to close every open screen.")]
    public static Task<XuiLiveStateResult> Close(
        MainThreadDispatcher dispatcher,
        [Description("Screen to close. Omit to close all.")] string? screenName = null)
        => dispatcher.InvokeOnMainThreadAsync(() => CloseScreen(screenName));

    [McpServerTool(Name = "runtime_xui_locale", UseStructuredContent = true)]
    [Description("Switch the live translation locale, or report the current one. Every .po under Assets/ is loaded and registered first, so this works on a project the editor opened without re-importing anything. Open screens are re-translated in place — a controller-written label is not clobbered. Pass an empty locale to go back to the untranslated source strings. Run xui_i18n_extract first to produce the catalogs.")]
    public static Task<XuiLocaleResult> SetLocale(
        MainThreadDispatcher dispatcher,
        [Description("Locale tag, matching a .po file name (e.g. zh-Hans). Omit to report without switching; pass \"\" for the source strings.")] string? locale = null)
        => dispatcher.InvokeOnMainThreadAsync(() => ApplyLocale(locale));

    // ------------------------------------------------------------------
    //  Implementation (main thread)
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads every catalog in the project, then switches.
    /// </summary>
    /// <remarks>
    /// Catalogs are registered here rather than relying on the importer having run this session: the
    /// importer registers on import, which covers editing a <c>.po</c>, but not an editor that opened a
    /// project whose assets were already imported. Loading them on demand makes the tool work from a
    /// cold start, which is the state an agent actually finds the editor in.
    /// </remarks>
    internal static XuiLocaleResult ApplyLocale(string? locale)
    {
        int loaded = LoadCatalogs(out List<string> failures);

        if (locale != null) Xui.Locale = locale;

        var retranslated = 0;
        foreach (string name in Xui.OpenScreens)
        {
            if (Xui.Screen(name) is { } open) retranslated += Xui.Retranslate(open.Instance);
        }

        return new XuiLocaleResult(
            Xui.Locale,
            new List<string>(Xui.AvailableLocales),
            loaded,
            Xui.TranslationCount,
            retranslated,
            failures.Count > 0 ? string.Join("; ", failures) : null);
    }

    /// <summary>
    /// Registers every <c>.po</c> in the project, and any font sitting beside it.
    /// </summary>
    /// <remarks>
    /// A font named after the locale next to its catalog — <c>Locales/zh-Hans.ttf</c> beside
    /// <c>Locales/zh-Hans.po</c> — becomes that locale's font. A convention rather than a config file:
    /// the pairing is visible in the project folder, and there is no third place to keep in sync.
    /// </remarks>
    private static int LoadCatalogs(out List<string> failures)
    {
        failures = new List<string>();
        int loaded = 0;

        EditorAssetBackend? backend = EditorAssetBackend.Instance;
        if (backend == null) return 0;
        string[] allPaths = backend.GetAllAssetPaths();

        // The database's own path list rather than a directory scan: it already knows what is imported,
        // and a file it has not seen has no asset to register anyway.
        foreach (string relative in backend.GetAllAssetPaths())
        {
            if (!relative.EndsWith(".po", StringComparison.OrdinalIgnoreCase)) continue;

            Guid guid = backend.PathToGuid(relative);
            if (guid == Guid.Empty) continue;

            if (AssetDatabase.Get(guid) is not XuiCatalogAsset asset)
            {
                failures.Add(relative + " did not import as a translation catalog.");
                continue;
            }

            if (asset.ParseError is { } error)
            {
                failures.Add(error);
                continue;
            }

            if (asset.Catalog is { Locale.Length: > 0 } catalog)
            {
                Xui.RegisterCatalog(catalog);
                RegisterSiblingFont(backend, allPaths, relative, catalog.Locale);
                loaded++;
            }
        }

        return loaded;
    }

    internal static XuiLiveScreenResult OpenScreen(string documentPath, string? screenName)
    {
        if (!TryLoad(documentPath, reimport: false, out XuiDocumentAsset? asset, out string? error))
            return XuiLiveScreenResult.Failed(documentPath, error!);

        if (asset!.Document == null)
            return XuiLiveScreenResult.Failed(documentPath, "The document does not parse: " + asset.ParseError);

        string target = screenName ?? FirstScreenName(asset);
        if (target.Length == 0)
            return XuiLiveScreenResult.Failed(documentPath, "The document declares no screens.");

        Scene? scene = Scene.Current;
        if (scene is not { IsDisposed: false })
            return XuiLiveScreenResult.Failed(documentPath, "No scene is open. Open one before building a screen.");

        // Register the document so Reload can find it again by screen name alone.
        if (Xui.Documents is XuiDocumentTable table) table.Add(asset);
        else Xui.Documents = CreateTable(asset);

        Xui.Scene = scene;

        try
        {
            XuiOpenScreen open = Xui.ShowDocument(asset, target);
            return Describe(documentPath, open, null);
        }
        catch (Exception ex)
        {
            return XuiLiveScreenResult.Failed(documentPath, ex.Message);
        }
    }

    internal static XuiLiveStateResult CollectState()
    {
        var screens = new List<XuiLiveScreenInfo>();
        foreach (string name in Xui.OpenScreens)
        {
            XuiOpenScreen? open = Xui.Screen(name);
            if (open == null) continue;
            screens.Add(new XuiLiveScreenInfo(
                open.ScreenName,
                open.Layer.ToString(),
                open.Visible,
                open.Instance.Ids.Count,
                open.Controller?.GetType().Name));
        }

        return new XuiLiveStateResult(Scene.Current?.Name ?? "(none)", screens.Count, screens, null);
    }

    internal static XuiLiveTreeResult ReadTree(string screenName, int width, int height, int maxNodes)
    {
        XuiOpenScreen? open = Xui.Screen(screenName);
        if (open == null)
        {
            return new XuiLiveTreeResult(
                screenName, 0, false, 0, 0, Array.Empty<XuiLiveNode>(),
                "Screen \"" + screenName + "\" is not open. Call runtime_xui_state to list what is.");
        }

        int measureWidth = Math.Clamp(width, 16, 16384);
        int measureHeight = Math.Clamp(height, 16, 16384);
        Measure(open.Instance, measureWidth, measureHeight);

        int cap = Math.Clamp(maxNodes, 1, 2000);
        var nodes = new List<XuiLiveNode>(Math.Min(cap, 64));
        var idsByObject = new Dictionary<GameObject, string>();
        foreach (string id in open.Instance.Ids)
        {
            GameObject? go = open.Instance.Find(id);
            if (go is { IsDisposed: false }) idsByObject[go] = id;
        }

        Collect(open.Instance.Root, idsByObject, depth: 0, nodes, cap);
        return new XuiLiveTreeResult(
            screenName, nodes.Count, nodes.Count >= cap, measureWidth, measureHeight, nodes, null);
        
    }

    /// <summary>
    /// Forces a layout pass at a chosen resolution and restores whatever the editor had.
    /// </summary>
    /// <remarks>
    /// Without this the rects read back as zero: <c>ComputedRect</c> is written by a canvas rebuild,
    /// and a canvas that has never rendered (no GameView panel open, not in play mode) has never had
    /// one. Overriding the screen size rather than requiring a GameView also means an agent can ask
    /// what the layout does at 1920x1080 and at 720x1280 without resizing anything.
    /// </remarks>
    private static void Measure(XuiScreenInstance instance, int width, int height)
    {
        if (instance.Root is not { IsDisposed: false } root) return;
        GameCanvas? canvas = root.GetComponent<GameCanvas>();
        if (canvas is not { IsDisposed: false }) return;

        Float2? previous = GameCanvas.ScreenSizeOverride;
        try
        {
            GameCanvas.ScreenSizeOverride = new Float2(width, height);
            canvas.MarkDirty(UIDirtyFlags.Layout | UIDirtyFlags.Hierarchy);
            canvas.RebuildIfDirty();
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = previous;
        }
    }

    internal static XuiLiveScreenResult ReloadDocument(string documentPath)
    {
        if (!TryLoad(documentPath, reimport: true, out XuiDocumentAsset? asset, out string? error))
            return XuiLiveScreenResult.Failed(documentPath, error!);

        if (asset!.Document == null)
            return XuiLiveScreenResult.Failed(documentPath, "The document does not parse: " + asset.ParseError);

        if (Xui.Documents is XuiDocumentTable table) table.Add(asset);
        else Xui.Documents = CreateTable(asset);

        int reloaded = 0;
        XuiOpenScreen? last = null;
        for (int i = 0; i < asset.Document.Screens.Count; i++)
        {
            string name = asset.Document.Screens[i].Name;
            if (!Xui.Reload(name)) continue;
            reloaded++;
            last = Xui.Screen(name);
        }

        if (reloaded == 0)
        {
            return XuiLiveScreenResult.Failed(
                documentPath,
                "The document re-imported, but none of its screens are open. Call runtime_xui_open first.");
        }

        return Describe(documentPath, last!, reloaded == 1 ? null : reloaded + " screens rebuilt.");
    }

    internal static XuiLiveStateResult CloseScreen(string? screenName)
    {
        if (screenName == null) Xui.CloseAll();
        else Xui.Close(screenName);
        return CollectState();
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static void Collect(
        GameObject go,
        Dictionary<GameObject, string> idsByObject,
        int depth,
        List<XuiLiveNode> nodes,
        int cap)
    {
        if (nodes.Count >= cap) return;

        // A GameCanvas has no RectTransform: its extent is the canvas root rect. Reporting 0x0 for the
        // screen root would read as "the screen has no size", which is the opposite of true.
        RectTransform? rect = go.RectTransform;
        Rect measured = go.GetComponent<GameCanvas>() is { IsDisposed: false } canvas
            ? canvas.RootRect
            : rect?.ComputedRect ?? default;

        nodes.Add(new XuiLiveNode(
            go.Name,
            idsByObject.TryGetValue(go, out string? id) ? id : null,
            depth,
            go.EnabledInHierarchy,
            measured.Min.X,
            measured.Min.Y,
            measured.Size.X,
            measured.Size.Y,
            ComponentNames(go)));

        IReadOnlyList<GameObject> children = go.Children;
        for (int i = 0; i < children.Count; i++)
            Collect(children[i], idsByObject, depth + 1, nodes, cap);
    }

    private static IReadOnlyList<string> ComponentNames(GameObject go)
    {
        var names = new List<string>(4);
        foreach (MonoBehaviour component in go.GetComponents<MonoBehaviour>())
        {
            // RectTransform is on every node; listing it everywhere is noise.
            if (component is RectTransform) continue;
            names.Add(component.GetType().Name);
        }
        return names;
    }

    private static bool TryLoad(
        string documentPath,
        bool reimport,
        out XuiDocumentAsset? asset,
        out string? error)
    {
        asset = null;
        error = null;

        if (!documentPath.EndsWith(".xui", StringComparison.OrdinalIgnoreCase))
        {
            error = "Expected a .xui path, got '" + documentPath + "'.";
            return false;
        }

        EditorAssetBackend? backend = EditorAssetBackend.Instance;
        if (backend == null)
        {
            error = "The editor asset database is not available.";
            return false;
        }

        string relative = Normalize(documentPath);
        Guid guid = backend.PathToGuid(relative);
        if (guid == Guid.Empty)
        {
            error = "No asset at '" + relative + "'. Paths are relative to Assets/.";
            return false;
        }

        // Re-import first on the reload path: the on-disk edit is the whole point, and the cached
        // asset would otherwise rebuild the previous text.
        if (reimport) backend.Reimport(guid);

        if (AssetDatabase.Get(guid) is not XuiDocumentAsset loaded)
        {
            error = "'" + relative + "' did not import as a XUI document. Is com.xengine.xui installed?";
            return false;
        }

        asset = loaded;
        return true;
    }

    /// <summary>
    /// Binds <c>&lt;locale&gt;.ttf</c> / <c>.otf</c> next to the catalog as that locale's font.
    /// </summary>
    /// <remarks>
    /// This is whole-asset substitution, which is all XGUI can do: its text has Bold / Italic / Mono
    /// slots but no per-character fallback chain, so a locale whose glyphs the default font lacks needs
    /// a font of its own or renders blank. Saying so plainly beats implying a fallback that does not
    /// exist — the failure would otherwise show up only in the locale nobody on the team reads.
    /// </remarks>
    private static void RegisterSiblingFont(
        EditorAssetBackend backend, string[] allPaths, string catalogPath, string locale)
    {
        string folder = catalogPath.Contains('/')
            ? catalogPath.Substring(0, catalogPath.LastIndexOf('/') + 1)
            : string.Empty;

        foreach (string extension in FontExtensions)
        {
            string candidate = folder + locale + extension;
            foreach (string path in allPaths)
            {
                if (!string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase)) continue;

                Guid guid = backend.PathToGuid(path);
                if (guid != Guid.Empty) Xui.RegisterLocaleFont(locale, new AssetRef<FontAsset>(guid));
                return;
            }
        }
    }

    private static readonly string[] FontExtensions = { ".ttf", ".otf" };

    /// <summary>Accepts a path with or without the leading <c>Assets/</c>, since both read naturally.</summary>
    private static string Normalize(string documentPath)
    {
        string path = documentPath.Replace('\\', '/');
        return path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring("Assets/".Length)
            : path;
    }

    private static string FirstScreenName(XuiDocumentAsset asset)
        => asset.Document is { Screens.Count: > 0 } document ? document.Screens[0].Name : string.Empty;

    private static XuiDocumentTable CreateTable(XuiDocumentAsset asset)
    {
        var table = new XuiDocumentTable();
        table.Add(asset);
        return table;
    }

    private static XuiLiveScreenResult Describe(string documentPath, XuiOpenScreen open, string? note)
        => new(
            true,
            documentPath,
            open.ScreenName,
            open.Layer.ToString(),
            open.Visible,
            open.Instance.Ids.Count,
            note);
}

/// <summary>Result of opening or reloading a screen.</summary>
/// <summary>
/// The live translation state. <c>Retranslated</c> counts the strings written back into open screens,
/// which is the number that shows whether a switch actually reached the UI.
/// </summary>
public sealed record XuiLocaleResult(
    string Locale,
    IReadOnlyList<string> AvailableLocales,
    int CatalogsLoaded,
    int Translations,
    int Retranslated,
    string? Error);

public sealed record XuiLiveScreenResult(
    bool Ok,
    string DocumentPath,
    string? ScreenName,
    string? Layer,
    bool Visible,
    int ElementCount,
    string? Message)
{
    public static XuiLiveScreenResult Failed(string documentPath, string message)
        => new(false, documentPath, null, null, false, 0, message);
}

public sealed record XuiLiveScreenInfo(
    string ScreenName,
    string Layer,
    bool Visible,
    int ElementCount,
    /// <summary>Null for a screen opened straight from a document, with no controller.</summary>
    string? Controller);

public sealed record XuiLiveStateResult(
    string SceneName,
    int ScreenCount,
    IReadOnlyList<XuiLiveScreenInfo> Screens,
    string? Message);

/// <summary>
/// One node of a live screen. X/Y/Width/Height are the rect layout actually computed, which is the
/// point of this tool: the document says what was asked for, this says what happened.
/// </summary>
public sealed record XuiLiveNode(
    string Name,
    string? Id,
    int Depth,
    bool Enabled,
    float X,
    float Y,
    float Width,
    float Height,
    IReadOnlyList<string> Components);

/// <summary>
/// A measured screen. <c>MeasuredWidth</c>/<c>MeasuredHeight</c> record the resolution the rects were
/// computed at, because a rect means nothing without knowing what it was measured against.
/// </summary>
public sealed record XuiLiveTreeResult(
    string ScreenName,
    int NodeCount,
    bool Truncated,
    int MeasuredWidth,
    int MeasuredHeight,
    IReadOnlyList<XuiLiveNode> Nodes,
    string? Message);
