using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.PropertyEditors;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.Editor.Thumbnails;
using XEngine.Editor.Utils;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.Core;

public class EditorApplication : Game
{
    public static EditorApplication? Instance { get; private set; }

    /// <summary>★ Layer 2 (B3-cont) — The in-process MCP server host. When non-null and running, AI
    /// agents connect to a LIVE editor instance (read the running scene, Debug.OnLog, toggle playmode).
    /// Drained each frame via <see cref="OnUpdate"/>. Started on editor launch in serve mode.</summary>
    public XEngine.Ai.Server.AiServerHost? AiServer { get; private set; }

    /// <summary>★ Layer 2 (B3-cont) — Start the in-process MCP server, scanning this assembly plus
    /// package Editor assemblies that declare <c>[McpServerToolType]</c>. Called from Program.Main
    /// when --serve is passed. The tool surface follows the project's edition (engine repo project
    /// → engine-dev; standalone game project → user), consistent with the offline xengine-cli.</summary>
    public void StartAiServer()
    {
        if (AiServer is not null) return;
        AiServer = new XEngine.Ai.Server.AiServerHost(ResolveAiEdition(), McpToolAssemblyScan.Collect());
        _ = AiServer.StartAsync();
    }

    /// <summary>Edition of the opened project, resolved like the offline Host does (layout detection).</summary>
    private static XEngine.Ai.Protocol.AiEdition ResolveAiEdition()
    {
        string? root = Program.StartupProjectPath ?? Project.Current?.RootPath;
        if (root is null) return XEngine.Ai.Protocol.AiEdition.EngineDev;
        root = Path.GetFullPath(root);
        // Tolerate being handed the .xengine file instead of the project directory.
        if (!Directory.Exists(root)) root = Path.GetDirectoryName(root);
        return root is null
            ? XEngine.Ai.Protocol.AiEdition.EngineDev
            : XEngine.Ai.Protocol.AiEditionDetector.Detect(root);
    }

    /// <summary>Restart the AI server so <see cref="McpToolAssemblyScan"/> re-collects tool
    /// assemblies — package Editor assemblies (e.g. com.xengine.spine's runtime_spine_* tools) only
    /// exist after the project's script assemblies load, which happens AFTER --serve boot.</summary>
    public async void RestartAiServer()
    {
        if (AiServer is null) { StartAiServer(); return; }
        try { await AiServer.StopAsync(); } catch { }
        AiServer.Dispose();
        AiServer = null;
        StartAiServer();
    }

    /// <summary>The editor's PropertyGrid configuration (drawers, handlers, callbacks).</summary>
    public static OrigamiUI.PropertyGridConfig PropertyGridConfig { get; private set; } = null!;

    private DockSpace _dockSpace = null!;
    private readonly EditorWorkspace _workspace = new();
    private EditorSurfaceHost? _mainSurfaceHost;
    private EditorNativeSurfaceManager? _nativeSurfaceManager;
    private GUI.NebulaBackground? _nebula;
    private double _introTime = double.MaxValue;
    private const double IntroCloseDuration = 0.55; // bars close over launcher
    private const double IntroHoldDuration = 0.15;
    private const double IntroOpenDuration = 0.55;  // bars open revealing editor
    private const double IntroDuration = IntroCloseDuration + IntroHoldDuration + IntroOpenDuration;
    private bool _introClosing; // true = closing phase (bars sliding in)
    private bool _startupTraceLogged;
    private bool _launcherWasOpen = true;
    private bool _launcherSettingsDeepLinkDone;
    private bool _persistWindowPlacement = true;
    private Int2 _lastLiveMouse;
    private bool _pendingProjectInitialization;
    private IDisposable? _origamiScope;
    private readonly EditorFrameScheduler _frameScheduler = new();
    private readonly EditorShellPaint _shellPaint = new();
    private const string LayoutFileExtension = ".xlayout";
    private string _activeLayoutName = "Default";
    private float _autoPanelScreenshotReadyAt = -1f;
    private bool _autoPanelScreenshotDone;
    private bool _playOnLoadTriggered;
    private bool _startupSelectionApplied;
    private bool _startupVisibilityApplied;
    private double _lastAssetTickWarnUnscaled;

    private string _curDefaultFont;
    private string _curDefaultBoldFont;
    private static readonly List<GameObject> s_gizmoSelected = new();
    private static readonly List<MonoBehaviour> s_gizmoComponents = new();

    /// <summary>
    /// Hot-reload: the gizmo scratch lists are only cleared at the START of the next
    /// <see cref="DrawGizmos"/> pass, but the reload (and its GC verification loop) runs
    /// synchronously inside Window.Update BEFORE that. Clearing them here drops the last frame's
    /// live user MonoBehaviour references so they cannot pin the collectible ALC.
    /// </summary>
    [OnAssemblyUnload]
    internal static void ClearGizmoScratchForReload()
    {
        s_gizmoSelected.Clear();
        s_gizmoComponents.Clear();
    }

    /// <summary>Scheduler for the editor's primary native surface.</summary>
    public EditorFrameScheduler FrameScheduler => _frameScheduler;

    /// <summary>Compared menu / toolbar / status regions on the main Paper. Live probes read capture counts.</summary>
    internal EditorShellPaint ShellPaint => _shellPaint;

    /// <summary>Workspace registry containing the editor's primary surface host.</summary>
    public EditorWorkspace Workspace => _workspace;

    /// <summary>True while any native window belonging to this editor is the foreground window.</summary>
    public bool IsWindowGroupActive => _nativeSurfaceManager?.IsWindowGroupActive ?? Window.IsFocused;

    // Play mode state
    private Echo.EchoObject? _savedEditorScene;
    private bool _isUnloadingPlayScene;
    private int _savedActiveTabIndex = -1;
    private DockNode? _savedActiveTabNode;
    private TimeData? _savedEditorTime;


    public override void InitializeWindow(string title, int width, int height)
    {
        var instance = EditorSettings.Instance;
        bool openLauncher = Program.StartupProjectPath == null;
        _persistWindowPlacement = !openLauncher;

        int clientW;
        int clientH;
        Silk.NET.Windowing.WindowState state;
        if (openLauncher)
        {
            clientW = ProjectLauncher.NativeWindowWidth;
            clientH = ProjectLauncher.NativeWindowHeight;
            state = Silk.NET.Windowing.WindowState.Normal;
        }
        else
        {
            clientW = instance.WindowWidth > 0 ? instance.WindowWidth : width;
            clientH = instance.WindowHeight > 0 ? instance.WindowHeight : height;
            state = instance.WindowMaximized
                ? Silk.NET.Windowing.WindowState.Maximized
                : Silk.NET.Windowing.WindowState.Normal;
        }

        // The editor reports unconstrained rendering performance. Player pacing is separate.
        Window.InitWindow(title, clientW, clientH, state, false);

        // Style the native caption during window creation (Silk raises Load inside
        // Initialize) so the first visible frame is already menu-bar light instead of
        // following the OS light/dark personalization until Initialize() gets there.
        Window.InternalWindow.Load += () => EditorWindowChrome.Apply(Window.InternalWindow);

        Window.Position = new Silk.NET.Maths.Vector2D<int>(
            instance.WindowX > 0 ? instance.WindowX : Window.Position.X,
            instance.WindowY > 0 ? instance.WindowY : Window.Position.Y);
    }

    public override void Initialize()
    {
        Instance = this;
        if (Program.HierarchyPaintSharedAtlas) PaperInstance?.Canvas.EnableFontAtlasRetirement();
        Application.IsEditor = true;
        Application.IsPlaying = false;

        // Persistent editor log (Unity Editor.log counterpart) must start before anything logs.
        // Uses the --project root when available so each project gets its own Logs/Editor.log.
        Core.EditorLog.Initialize(Program.StartupProjectPath);
        if (!string.IsNullOrWhiteSpace(Program.MetalToolchainDiagnostic))
            Runtime.Debug.Log($"[Metal] {Program.MetalToolchainDiagnostic}");

        // Crash capture must be in place before anything else can throw.
        Core.BugReport.CrashHandler.Install();

        // Editor quality tier: smaller shadow atlas + Preview defaults.
        // Empty scenes still skip atlas entirely (see UniversalRenderPipeline / NeedsShadowAtlas).
        Runtime.Rendering.QualitySettings.ApplyEditorDefaults();

        InitializeFont();

        DockSpace.TabBarLeadingMark = static (canvas, rect) => GUI.EditorBranding.Draw(canvas, rect);

        Resize(Window.Size.X, Window.Size.Y);

        // Load Font Awesome as fallback fonts for icons. Keep the FontFile handles so icons can also
        // be drawn in a chosen weight directly (e.g. an outline vs filled star), independent of the
        // fallback resolution order.
        EditorTheme.FontIconOutline = LoadFallbackFont("XEngine.Editor.Resources.fa-regular-400.ttf");
        EditorTheme.FontIconSolid   = LoadFallbackFont("XEngine.Editor.Resources.fa-solid-900.ttf");

        // Load CJK/international fallback fonts from system for localization support
        LoadSystemFallbackFonts();

        // Load editor settings (global, persists across projects)
        _ = EditorSettings.Instance; // triggers load + ApplyTheme

        _dockSpace = new DockSpace(CreateDefaultLayout());
        _nativeSurfaceManager = new EditorNativeSurfaceManager(_workspace, Window.InternalWindow);
        _workspace.TryCreateHost = _nativeSurfaceManager.TryCreate;
        _workspace.ReleaseHost = _nativeSurfaceManager.Release;
        _mainSurfaceHost = new EditorSurfaceHost(0, _dockSpace,
            new Rect(Window.Position.X, Window.Position.Y,
                Window.Position.X + Window.Size.X, Window.Position.Y + Window.Size.Y));
        if (!_workspace.RegisterHost(_mainSurfaceHost))
            throw new InvalidOperationException("Unable to attach the editor's primary surface host.");

        EditorPopupMapping.Install(_workspace, () => PaperInstance);

        // If launched with --project arg, only the cheap Project.Open runs here so the first frame
        // presents quickly and the window pumps OS messages. The heavy open (script assemblies,
        // registries, asset database, layout, scene) runs frame-paced through the same
        // ProjectOpenProgress step machine the launcher uses, with the startup progress screen
        // covering the window meanwhile — the editor never sits blank/unresponsive (Unity 6-style
        // embedded open-progress screen). Assemblies still load BEFORE the asset scan: the scan
        // picks importers via EditorRegistries, and package Editor assemblies (optional importers
        // like com.xengine.spine) only exist after that load.
        if (Program.StartupProjectPath != null)
        {
            try
            {
                var project = Project.Open(Program.StartupProjectPath, exclusive: true);
                project.SetActive();

                // Point the runtime's on-demand shader variant compiles at the project's
                // artifact store (Library/cache/shaders) BEFORE any scene/material draws: without
                // this, draw-time keyword combinations (SKINNED, HAS_UV2, …) re-run the full
                // Slang compile on every session and Play start pays seconds per variant.
                Importers.ShaderImporter.LinkRuntimeArtifactStore(project);

                BeginStartupProjectOpen();
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"Failed to open project from --project arg: {ex.Message}");
                // §4.3: report the failure cause to the XHub session watcher (advisory when
                // no --hub-session was passed — emission is a no-op then).
                string code = Projects.HubSession.IsLeaseConflict(ex)
                    ? Projects.HubSession.CodeProjectLocked
                    : Projects.HubSession.CodeProjectOpenFailed;
                Projects.HubSession.Emit(Projects.HubSession.LockConflict, new { message = ex.Message }, code);
                if (code == Projects.HubSession.CodeProjectOpenFailed)
                    Projects.HubSession.Emit(Projects.HubSession.OpenFailed, new { message = ex.Message }, code);
                Toasts.Warning("无法打开项目", ex.Message);
            }
        }

        // Initialize localization
        XEngine.Rosetta.Loc.Configure(cfg => cfg
            .SetFallbackLocale("en")
            .AddProvider(new EmbeddedResourceProvider(
                System.Reflection.Assembly.GetExecutingAssembly(), "XEngine.Editor.Resources.Locale"))
            .SetLocale(EditorSettings.Instance.Locale)
            .OnMissingKey(XEngine.Rosetta.MissingKeyBehavior.ReturnKey)
        );

        EditorRegistries.Initialize();

        // ★ --serve: start the AI server after script (and optional package Editor) assemblies are
        // available so McpToolAssemblyScan.Collect() sees runtime tools from packages like
        // com.xengine.spine. A --project launch starts it in open step 1 instead (assemblies load
        // there, frame-paced); the launcher flow restarts it in the same step.
        if (Program.Serve && Program.StartupProjectPath == null)
            StartAiServer();

        // Cursor lock toasts
        Input.OnCursorLocked += () =>
            Toasts.Show(Loc.Get("toast.cursor_locked"), Loc.Get("toast.cursor_locked_msg"), ToastType.Info, 3f);
        Input.OnCursorLockFailed += () =>
            Toasts.Show(Loc.Get("toast.cursor_lock_failed"), Loc.Get("toast.cursor_lock_failed_msg"), ToastType.Warning, 3f);

        RegisterMenus();

        if (Project.Current == null)
        {
            // Start with the project launcher: either no --project was given, or the open failed —
            // a pending --project open keeps its own screen up (BeginStartupProjectOpen) and never
            // reaches the launcher.
            ProjectLauncher.Initialize();
        }

        // Initialize status bar log tracking
        InitializeStatusBar();

        // Build the editor's PropertyGrid config
        PropertyGridConfig = new OrigamiUI.PropertyGridConfig();
        OrigamiUI.BuiltInFieldDrawers.Register(PropertyGridConfig.Drawers);
        BuiltInAttributeHandlers.Register(PropertyGridConfig.Handlers);
        PropertyGridConfig.OnBeginRoot = target => Undo.SnapshotIfInteracting(target, OrigamiUI.Origami.ActivePaper);
        PropertyGridConfig.OnFieldChanged = target => (target as Runtime.EngineObject)?.OnValidate();
        PropertyGridConfig.OnBeforeDrawField = (fieldType, value) =>
        {
            if (typeof(Runtime.EngineObject).IsAssignableFrom(fieldType))
                EngineObjectPropertyEditor.SetFieldType(fieldType);
        };
        PropertyGridConfig.DrawTypePicker = (paper, id, baseType, currentValue, onChange) =>
        {
            var types = EditorUtils.GetAllTypes()
                .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Take(20).ToArray();

            if (types.Length == 0) return;

            Type? currentType = currentValue?.GetType();
            int selectedIndex = currentType != null ? Array.IndexOf(types, currentType) + 1 : 0;
            var names = types.Select(t => t.Name).Prepend("(null)").ToArray();

            OrigamiUI.Origami.Dropdown(paper, $"{id}_dd", selectedIndex,
                idx =>
                {
                    if (idx == 0) onChange(null);
                    else if (idx >= 1 && idx <= types.Length) onChange(Activator.CreateInstance(types[idx - 1]));
                }, names).Show();
        };
        PropertyGridConfig.FallbackFieldDrawer = (paper, id, label, fieldType, value, onChange, depth) =>
        {
            if (typeof(Runtime.EngineObject).IsAssignableFrom(fieldType))
                EngineObjectPropertyEditor.SetFieldType(fieldType);
            var editor = EditorRegistries.GetPropertyEditor(fieldType);
            if (editor != null)
            {
                editor.OnGUI(paper, id, label, value, onChange, depth);
                return true;
            }
            return false;
        };
        // A collection element is a "simple" (one-line object-picker row) rather than an expanded
        // per-element foldout when the host has a single-control editor for it - the same set the
        // FallbackFieldDrawer above handles: EngineObject-derived types (GameObject, Material, ...) and
        // AssetRef<T>. Without this, e.g. GameObject[] renders each element's internal fields instead.
        PropertyGridConfig.IsSimpleFieldType = fieldType => EditorRegistries.GetPropertyEditor(fieldType) != null;

        // [Tooltip("...")] on fields and [ShowInInspector] properties surfaces as a row tooltip.
        PropertyGridConfig.ResolveTooltip = member => member?.GetCustomAttribute<Runtime.TooltipAttribute>()?.Text;

        // Field-row right-click menu: Copy/Paste value, per-field prefab Apply/Revert,
        // multi-object "Set to Value of X".
        PropertyGridConfig.OnFieldContextMenu = (paper, menu, req) => GUI.FieldContextMenu.Build(paper, menu, req);

        // Register save handlers
        SaveManager.OnSave += () =>
        {
            if (PrefabEditingMode.IsEditing)
            {
                return PrefabEditingMode.Save()
                    ? Loc.Get("save.prefab", new { name = System.IO.Path.GetFileNameWithoutExtension(PrefabEditingMode.EditingPrefabPath) })
                    : null;
            }
            if (EditorSceneManager.Save())
                return Loc.Get("save.scene", new { name = Runtime.Resources.Scene.Current?.Name ?? "Untitled" });

            // No path yet - prompt for one, but never interrupt an unattended auto-save.
            if (!SaveManager.IsAutoSave)
                PromptSaveAs();
            return null;
        };
        SaveManager.OnSave += () =>
        {
            if (Project.Current == null) return null;
            SaveProjectState();
            return Loc.Get("save.layout");
        };

        EditorWindowChrome.Apply(Window.InternalWindow);

        // Attach to the window events to save the position and state of the window
        Window.InternalWindow.Move += (position) =>
        {
            EditorSettings.Instance.WindowX = position.X;
            EditorSettings.Instance.WindowY = position.Y;
            EditorSettings.Instance.Save();
        };

        Window.InternalWindow.Resize += (size) =>
        {
            EditorWindowChrome.Apply(Window.InternalWindow);
            if (!_persistWindowPlacement) return;
            EditorSettings.Instance.WindowWidth = size.X;
            EditorSettings.Instance.WindowHeight = size.Y;
            EditorSettings.Instance.Save();
        };

        Window.InternalWindow.StateChanged += (state) =>
        {
            // Re-assert the caption style in case the state transition dropped it
            // (same defensive replay as Resize below).
            EditorWindowChrome.Apply(Window.InternalWindow);
            if (_persistWindowPlacement)
                EditorSettings.Instance.WindowMaximized = state == Silk.NET.Windowing.WindowState.Maximized;
            if (_mainSurfaceHost != null)
            {
                bool visible = state != Silk.NET.Windowing.WindowState.Minimized;
                _mainSurfaceHost.IsVisible = visible;
                if (visible)
                    _mainSurfaceHost.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Window);
            }

            if (_persistWindowPlacement)
                EditorSettings.Instance.Save();
        };

        Window.InternalWindow.Closing += () =>
        {
            // Persist layout and native placement before the runtime tears down secondary hosts.
            SaveProjectState();
        };

        Window.FocusChanged += OnEditorSurfaceFocusChanged;
        Window.Resize += size =>
        {
            if (_mainSurfaceHost != null)
                _mainSurfaceHost.ScreenBounds = new Rect(Window.Position.X, Window.Position.Y,
                    Window.Position.X + size.X, Window.Position.Y + size.Y);
            NotifyMainWindowInteraction();
        };
        Window.Move += position =>
        {
            if (_mainSurfaceHost == null) return;
            Rect bounds = _mainSurfaceHost.ScreenBounds;
            float width = bounds.Max.X - bounds.Min.X;
            float height = bounds.Max.Y - bounds.Min.Y;
            _mainSurfaceHost.ScreenBounds = new Rect(position.X, position.Y, position.X + width, position.Y + height);
            NotifyMainWindowInteraction();
        };
        Window.FramebufferResize += _ => NotifyMainWindowInteraction();
        // Import external files dropped from the OS onto the Project panel (see AssetDropImporter).
        Window.FileDrop += AssetDropImporter.HandleFileDrop;
        Input.OnKeyEvent += OnEditorKeyChanged;
        Input.OnMouseEvent += OnEditorMouseChanged;
        InvalidateSurface(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Theme | EditorSurfaceDirtyReason.Window);
        ActivateBackgroundAnimation();
    }

    private static OrigamiUI.PropertyGridConfig? _debugGridConfig;

    /// <summary>
    /// A PropertyGrid config for the Inspector's Debug mode: same drawers/handlers/hooks as the
    /// normal grid, but also surfaces private instance fields (read-only unless [SerializeField]).
    /// </summary>
    public static OrigamiUI.PropertyGridConfig DebugGridConfig
    {
        get
        {
            if (_debugGridConfig != null) return _debugGridConfig;

            var cfg = new OrigamiUI.PropertyGridConfig { IncludePrivateFields = true };
            OrigamiUI.BuiltInFieldDrawers.Register(cfg.Drawers);
            BuiltInAttributeHandlers.Register(cfg.Handlers);
            cfg.OnBeginRoot = PropertyGridConfig.OnBeginRoot;
            cfg.OnFieldChanged = PropertyGridConfig.OnFieldChanged;
            cfg.OnBeforeDrawField = PropertyGridConfig.OnBeforeDrawField;
            cfg.DrawTypePicker = PropertyGridConfig.DrawTypePicker;
            cfg.FallbackFieldDrawer = PropertyGridConfig.FallbackFieldDrawer;
            cfg.IsSimpleFieldType = PropertyGridConfig.IsSimpleFieldType;
            cfg.ResolveTooltip = PropertyGridConfig.ResolveTooltip;
            cfg.OnFieldContextMenu = PropertyGridConfig.OnFieldContextMenu;
            _debugGridConfig = cfg;
            return cfg;
        }
    }

    public void InitializeFont()
    {
        if (EditorTheme.DefaultFont != null) return;

        // Body text uses Geist Medium (500): at 13px the Regular cut's ~1px stems lose coverage
        // through the SDF distance field (DistanceRange=4 spans the whole stem), rendering as
        // gray fuzz. Medium's ~1.3px stems survive the field and read solid. (2026-08 clarity pass)
        EditorTheme.DefaultFont  = LoadBundledFont("Geist-Medium.ttf");
        EditorTheme.FontMedium   = LoadBundledFont("Geist-Medium.ttf");
        EditorTheme.FontSemiBold = LoadBundledFont("Geist-SemiBold.ttf");
        EditorTheme.DefaultBoldFont = LoadBundledFont("Geist-Bold.ttf");
        EditorTheme.FontMono     = LoadBundledFont("JetBrainsMono-Regular.ttf");
        EditorTheme.FontDisplay  = LoadBundledFont("SpaceGrotesk-Bold.ttf");
        EditorTheme.FontLogo     = LoadBundledFont("Audiowide-Regular.ttf");

        _curDefaultFont = EditorTheme.DefaultFontName;
        _curDefaultBoldFont = EditorTheme.DefaultBoldFontName;

        // Rebuild the pushed Origami theme now that fonts exist.
        EditorTheme.SyncOrigami(0f);
    }

    private static XEngine.Scribe.FontFile? LoadBundledFont(string fileName)
    {
        using var stream = GetEmbeddedResource(fileName);
        if (stream == null)
        {
            Runtime.Debug.LogWarning($"Missing bundled font: {fileName}");
            return null;
        }
        return new XEngine.Scribe.FontFile(stream);
    }

    protected override void PreparePaperFrame()
    {
        var fbSize = Window.InternalWindow.FramebufferSize;
        float cs = Math.Max(0.01f, Window.ContentScale);
        float us = Math.Max(0.01f, EditorTheme.UserScale);
        PaperInstance.SetResolution(fbSize.X / (cs * us), fbSize.Y / (cs * us));
        PaperInstance.DisplayFramebufferScale = new Float2(cs * us, cs * us);
    }

    protected override Float2 GetPaperMousePosition()
    {
        // Match the logical button frame: a coalesced press/move may already have a
        // newer native position, but Paper must first hit-test the press origin.
        Int2 p = Input.MousePosition;
        var fb = Window.InternalWindow.FramebufferSize;
        var win = Window.InternalWindow.Size;
        float cs = Math.Max(0.01f, Window.ContentScale);
        float csFbWin = win.X > 0 ? (float)fb.X / win.X : 1f;
        float us = Math.Max(0.01f, EditorTheme.UserScale);
        return new Float2(p.X * csFbWin / (cs * us), p.Y * csFbWin / (cs * us));
    }

    private static Float2 ReadLiveMouse()
    {
        var mice = Window.InternalInput?.Mice;
        if (mice is { Count: > 0 })
        {
            var pos = mice[0].Position;
            return new Float2(pos.X, pos.Y);
        }
        return new Float2(Input.MousePosition.X, Input.MousePosition.Y);
    }

    protected override bool ShouldRoutePointerInput() =>
        !(_nativeSurfaceManager?.IsPointerOverSecondaryWindow ?? false);

    public override void BeginGui(Paper paper)
    {
        using var profilerScope = EditorProfiler.BeginCpuScope("UI.Base");

        // Flush undo system FIRST Paper callbacks fired in the previous frame's EndFrame(),
        // so mutations from OnValueChanged are now visible. FlushFrame compares the Snapshot
        // (taken last frame) against the current state to detect changes.
        Undo.FlushFrame();

        // Escape always unlocks cursor in editor (and drains stacked lock contexts).
        if (Input.GetKeyDown(KeyCode.Escape) && Input.CursorLocked)
            Input.ClearLockContexts();

        // Global keyboard shortcuts
        if (!ShortcutManager.IsRebinding)
        {
            if (ShortcutManager.IsPressed("Global/SaveAs"))
            {
                if (Application.IsPlaying)
                    Toasts.Warning(Loc.Get("save.cant_play_mode"),
                        Loc.Get("save.cant_play_mode_msg"));
                else
                    PromptSaveAs();
            }
            else if (ShortcutManager.IsPressed("Global/NewScene"))
            {
                EditorSceneManager.NewScene();
            }
            else if (ShortcutManager.IsPressed("Global/Undo"))
            {
                Undo.PerformUndo();
            }
            else if (ShortcutManager.IsPressed("Global/Redo"))
            {
                Undo.PerformRedo();
            }
            else if (ShortcutManager.IsPressed("Global/Properties"))
            {
                // Focused Properties window for the current selection (Unity 6 Alt+P).
                GUI.Panels.PropertiesPanel.Open(Selection.ActiveObject);
            }
            else if (ShortcutManager.IsPressed("Global/Play"))
            {
                if (Application.IsPlaying) ExitPlayMode();
                else EnterPlayMode();
            }
            else if (ShortcutManager.IsPressed("Global/Pause"))
            {
                TogglePause();
            }
            else if (ShortcutManager.IsPressed("Global/Step"))
            {
                StepOneFrame();
            }
        }

        // Reset per-frame state
        GameViewInputHandler.IsGameViewFocused = false;

        // Keeps the editor view outside of UI input's reach, to prevent false positives when clicking around
        if (Runtime.UI.EventSystem.Current is { } eventSystem)
            eventSystem.Viewport = new Runtime.UI.EventSystem.HostViewport { ReceivesInput = false };

        float w = paper.ScreenRect.Size.X;
        float h = paper.ScreenRect.Size.Y;

        TickPerfStats((float)Time.UnscaledDeltaTime);
        Selection.UpdatePing((float)Time.UnscaledDeltaTime);
        EditorTheme.TickOrigami((float)Time.UnscaledDeltaTime);

        // Push the editor's Origami theme and begin frame
        _origamiScope?.Dispose();
        _origamiScope = EditorTheme.PushOrigami();
        OrigamiUI.Origami.BeginFrame(paper, (float)Time.UnscaledDeltaTime);

        // Global visual-effect toggles (Preferences > Theme > Effects). Origami owns the drop-shadow /
        // glow distinction and gates its DropShadow()/Glow() helpers on these; Paper just draws shadows.
        OrigamiUI.Origami.DropShadowsEnabled = EditorTheme.DropShadows;
        OrigamiUI.Origami.GlowsEnabled = EditorTheme.AccentGlow;
        paper.Canvas.SetAntiAlias(EditorTheme.AntiAliasing);

        // Save system update (Ctrl+S + auto-save) - after theme push so toasts get icons
        SaveManager.Update((float)Time.UnscaledDeltaTime);

        // Detect project opened (launcher closed since last frame)
        if (!ProjectLauncher.IsOpen && !_introClosing && _launcherWasOpen)
        {
            RestoreEditorWindowPlacement();
            _introTime = 0;
            _introClosing = true;
            _launcherWasOpen = false;
            _pendingProjectInitialization = Project.Current != null && EditorAssetBackend.Instance == null;
            GUI.EditorGuide.ArmAutoStart(); // let the tour play once for this freshly-opened project
            if (Project.Current != null)
                UpdateEditorTitle();
        }

        // --project startup: cover the window with the embedded open-progress screen (Unity 6
        // behavior) while the frame-paced step machine opens the project. Editor chrome and the
        // scene do not exist yet — this screen IS the frame content until the open completes.
        if (ProjectOpenProgress.IsActive && !ProjectLauncher.IsOpen && !_introClosing)
        {
            GUI.StartupProgressScreen.Draw(paper, w, h);
            return;
        }

        // Gizmos were recorded in OnUpdate even when the scheduler skipped present, then thrown
        // away by the next SimulationStep ClearGizmos. Record here so only presented frames pay.
        RecordEditorGizmos(Runtime.Resources.Scene.Current);

        // Layout auto-save is handled by SaveManager's auto-save timer.

        // Show project launcher or intro close phase
        if (ProjectLauncher.IsOpen || _introClosing)
        {
            // Nested import-pump frame: only the open STEPS advance here (via
            // TickLauncherProjectOpen above). Rebuilding the launcher root on this frame is what
            // threw "Element already exists: pl_window" and failed the open; the outer frame
            // paints the launcher as usual.
            if (Window.InPumpRender)
                return;

            // Launcher-open in progress: cover the launcher with the SAME full-screen progress card
            // a --project launch uses (step list + live detail + overall bar), instead of the old
            // two-line footer strip. Data source is identical (ProjectOpenProgress); on completion
            // TickLauncherProjectOpen closes the launcher and the intro transition plays as before.
            if (ProjectOpenProgress.IsActive && !_introClosing)
            {
                GUI.StartupProgressScreen.Draw(paper, w, h);
                return;
            }

            // Always draw the launcher background during close transition
            ProjectLauncher.Draw(paper, (float)Time.UnscaledDeltaTime, forceDraw: _introClosing);

            if (ProjectLauncher.IsOpen)
                _launcherWasOpen = true;

            // Deep link (--launcher-settings [--launcher-settings-page <page>]): open the Settings
            // modal once the launcher is interactive. One-shot — a manual close ends the deep link.
            if (Program.LauncherSettingsOpen && !_launcherSettingsDeepLinkDone)
            {
                _launcherSettingsDeepLinkDone = true;
                if (string.Equals(Program.LauncherSettingsPage, "general", StringComparison.OrdinalIgnoreCase))
                    GUI.LauncherSettings.OpenGeneral();
                else
                    GUI.LauncherSettings.Open();
                if (!string.IsNullOrEmpty(Program.InstallModuleName))
                    GUI.LauncherSettings.StartDeepLinkInstall(Program.InstallModuleName);
            }

            // Block input during close animation
            if (_introClosing)
            {
                paper.Box("intro_blocker")
                    .PositionType(PositionType.SelfDirected).Position(0, 0)
                    .Size(w, h)
                    .Layer(Layer.Overlay)
                    .OnClick(0, (_, _) => { }); // absorb all clicks

                // Switch to editor once screen is fully covered (hold phase)
                if (_introTime >= IntroCloseDuration)
                {
                    InitializePendingProject();
                    _introClosing = false; // next frame draws editor behind open phase
                }
                else
                    return;
            }
            else
            {
                return; // launcher still active, don't draw editor
            }
        }

        // Ensure a scene always exists
        if (Project.Current != null && Runtime.Resources.Scene.Current == null)
            EditorSceneManager.EnsureSceneLoaded();

        // Editor backdrop (behind the translucent glass panels) shared with the launcher.
        _nebula ??= new GUI.NebulaBackground(paper);
        GUI.NebulaBackground.DrawEditorBackground(paper, _nebula, "nebula_bg", w, h, (float)Time.UnscaledDeltaTime);

        using (EditorProfiler.BeginCpuScope("Shell"))
            DrawHeader(paper, w, h);

        float pad = EditorTheme.DockPadding;
        float dockY = EditorTheme.MenuBarHeight + EditorTheme.ToolbarHeight + pad;
        float dockH = h - dockY - pad - EditorTheme.StatusBarHeight;
        _dockSpace.Draw(paper, pad, dockY, w - pad * 2, dockH);

        using (EditorProfiler.BeginCpuScope("Shell"))
            DrawStatusBar(paper, w, h);

        // First-run UI tour (once per user; reset from Preferences > General).
        GUI.EditorGuide.SetDockSpace(_dockSpace);
        if (Project.Current != null && !ProjectLauncher.IsOpen && _introTime >= IntroDuration)
            GUI.EditorGuide.TryAutoStart(GUI.EditorGuide.WelcomeTour());
    }

    // Transport / status chips sit in the dark Unity toolbar under the light File menu.
    private const float HeaderChipHeight = 22f;

    private void DrawHeader(Paper paper, float w, float h)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        float menuH = EditorTheme.MenuBarHeight;
        float toolH = EditorTheme.ToolbarHeight;

        using (paper.Box("header_menu").PositionType(PositionType.SelfDirected).Position(0, 0).Size(w, menuH)
            .BackgroundColor(EditorTheme.MenuBar).Enter())
        {
            _shellPaint.AttachMenu(paper);
            DrawMenuBar(paper, w, menuH);
        }

        using (paper.Box("header_toolbar").PositionType(PositionType.SelfDirected).Position(0, menuH).Size(w, toolH)
            .BackgroundColor(EditorTheme.Toolbar).Enter())
        {
            _shellPaint.AttachToolbar(paper);
            DrawPlayPill(paper, w, toolH, font);
            DrawHeaderStatus(paper, w, toolH, font);
        }
    }

    /// <summary>Centered rounded "pill" holding the play / pause / step transport buttons.</summary>
    private void DrawPlayPill(Paper paper, float w, float band, XEngine.Scribe.FontFile font)
    {
        using (paper.Row("play_pill").PositionType(PositionType.SelfDirected)
            .Size(UnitValue.Auto).Rounded(EditorTheme.Roundness)
            .Margin(UnitValue.StretchOne)
            .BackdropBlur(Origami.Current.Metrics.WindowBackdropBlur)
            // Unity 6.7's transport cluster is one raised #505050 block on the near-black strip.
            .BackgroundColor(EditorTheme.Neutral600).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Enter())
        {
            // Ghost buttons tint their icon by variant: green Play when stopped, red Stop while playing,
            // amber Pause when paused; step stays neutral.
            var play = Origami.IconButton(paper, "btn_play", Application.IsPlaying ? EditorIcons.CircleStop_I : EditorIcons.Play_I)
                .OnClick(() => { if (Application.IsPlaying) ExitPlayMode(); else EnterPlayMode(); })
                .Style(ButtonStyle.Ghost);
            if (Application.IsPlaying) play.Danger(); else play.Success();
            play.Show();

            var pause = Origami.IconButton(paper, "btn_pause", EditorIcons.Pause_I, TogglePause).Style(ButtonStyle.Ghost);
            if (Application.IsPaused) pause.Warning();
            pause.Show();

            Origami.IconButton(paper, "btn_step", EditorIcons.ForwardStep_I, StepOneFrame)
                .Style(ButtonStyle.Ghost).Show();
        }
    }

    // ── Smoothed perf readouts ──────────────────────────────────────────
    // Raw per-frame FPS / frame-time flicker far too fast to read. The frame time is an exponential
    // moving average (~0.5s time constant) so the FPS/ms readout glides continuously instead of
    // snapping; memory samples once a second (GC total moves in coarse steps anyway).
    private static float _titleRefreshClock = 1f;

    private static double _perfWindow;
    private static int _dispFps;
    private static int _dispSustainedFps = -1;   // -1 = no continuous-render sample yet → "—"
    private static float _dispCpuMs;
    private static long _dispMemMb;

    // Formatted status-bar strings are rebuilt at most twice a second (or when the value
    // changes) so a hovered editor renders with zero string allocations per frame.
    private static float _statusTextClock;
    private static int _statusFpsShown = -1;
    private static string _statusFpsNumText = "0";
    private static string _statusMsText = "0.0ms";
    private static string _statusSustainedText = "—";
    private static string? _statusLayoutText;
    private static string _statusLayoutKey = "";

    private static void TickPerfStats(float dt)
    {
        // Dual-value model driven by Window.PresentStats (measured inside the render branch, so
        // idle sleeps never inflate it): PresentFps = the rate actually being delivered right now
        // (drops under on-demand throttling — by design); SustainedFps = the EMA over
        // back-to-back rendered frames = the pipeline's demonstrated capability. -1 sustains the
        // last capability readout when the stream goes idle instead of decaying to a lie.
        _dispFps = (int)MathF.Round((float)Window.PresentStats.PresentFps);
        double sustained = Window.PresentStats.SustainedFps;
        if (sustained > 0)
            _dispSustainedFps = (int)MathF.Round((float)sustained);
        _dispCpuMs = (float)Window.PresentStats.LastCpuMs;

        // Throttled status text: the values glide anyway, so 500ms granularity is invisible
        // while removing interpolated-string allocations from every rendered frame.
        _statusTextClock += dt;
        if (_statusTextClock >= 0.5f || _statusFpsShown < 0)
        {
            _statusTextClock = 0f;
            _statusFpsShown = _dispFps;
            _statusFpsNumText = _dispFps.ToString();
            _statusMsText = string.Create(null, stackalloc char[16], $"{_dispCpuMs:F1}ms");
            _statusSustainedText = _dispSustainedFps > 0 ? _dispSustainedFps.ToString() : "—";
        }

        _perfWindow += dt;
        if (_perfWindow >= 1.0 || _dispMemMb == 0)
        {
            _dispMemMb = GC.GetTotalMemory(false) / (1024 * 1024);
            _perfWindow = 0.0;
        }
    }

    /// <summary>Right-side status cluster: FPS, editor version and project name as themed chips,
    /// then a ghost cog that opens Project Settings.</summary>

    /// <summary>Running engine version without the 'v' prefix — display form ("V1.0.0a1") for
    /// titles. Version records (ProjectVersion.txt stamps, Hub matching) use the canonical
    /// <see cref="CanonicalEngineVersion"/> form instead.</summary>
    public static string EngineVersion { get; } = StripVersion(Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.1");

    /// <summary>Canonical engine version ("1.0.0-a1", XHub plan §4.2): the single format written
    /// to ProjectVersion.txt and compared against project stamps / matched by XHub.</summary>
    public static string CanonicalEngineVersion { get; } =
        Projects.ProjectEngineVersion.Canonicalize(Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.1");

    private static string StripVersion(string version)
    {
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        return version;
    }

    /// <summary>Bare graphics API for the window title: "Vulkan" / "D3D12" / "OpenGL" / "Metal".</summary>
    private static string GraphicsBackendApiName
    {
        get
        {
            string name = XEngine.Runtime.RHI.GraphicsBackendSelection.GetDisplayName(Graphics.Device);
            int paren = name.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? name[..paren] : name;
        }
    }

    /// <summary>Unity-parity window title:
    /// {Project} - {Scene} - Windows, Mac, Linux - XEngine {version} - &lt;Backend&gt;</summary>
    public static void UpdateEditorTitle()
    {
        if (Window.InternalWindow == null) return;
        // No project open (launcher / failed open): Unity-parity pre-project title is the engine
        // name + version (the window icon carries the logo). Never recurse into the full title.
        if (Project.Current == null)
        {
            Window.InternalWindow.Title = $"XEngine {EngineVersion}";
            return;
        }
        string scene = EditorSceneManager.CurrentScenePath is { } p
            ? System.IO.Path.GetFileNameWithoutExtension(p)
            : "Untitled";
        Window.InternalWindow.Title =
            $"{Project.Current.Name} - {scene} - Windows, Mac, Linux - XEngine {EngineVersion} - <{GraphicsBackendApiName}>";
    }

    private void DrawHeaderStatus(Paper paper, float w, float band, XEngine.Scribe.FontFile font)
    {
        float clH = HeaderChipHeight;
        float pad = EditorTheme.DockPadding;
        float rectPadX = 10f, dot = 8f;

        int fps = _statusFpsShown < 0 ? _dispFps : _statusFpsShown;
        string fpsNum = _statusFpsNumText;
        string msText = _statusMsText;
        string sustainedText = _statusSustainedText;
        int cap = _dispSustainedFps;
        // Dot reflects DELIVERED rate; dim when on-demand throttling hides it (not a perf fault).
        var dotColor = fps >= 50 ? EditorTheme.Green400 : (fps >= 25 ? EditorTheme.Amber400 : EditorTheme.Red400);




        // Cluster pinned to the right edge and vertically centered by margins; every child auto-sizes to
        // its text, so there's no width math or MeasureText.
        using (paper.Row("hdr_status").PositionType(PositionType.SelfDirected)
            .Width(UnitValue.Auto).Height(clH)
            .Margin(UnitValue.StretchOne, UnitValue.Pixels(pad), UnitValue.StretchOne, UnitValue.StretchOne).RowBetween(6).Enter())
        {
            // FPS chip: [glowing dot + count] left-anchored, [FPS + X.Xms] right-anchored, spacer between.
            // Auto width with a 120px floor lets the count grow into the spacer without moving anything.
            // Flat on the toolbar strip (Unity 6.7's right-side controls have no chip fill).
            using (paper.Row("hs_fps").Width(UnitValue.Auto).MinWidth(UnitValue.Pixels(120)).Height(clH).Rounded(7)
                .Padding(rectPadX, rectPadX, 0, 0).Enter())
            {
                paper.Box("hs_fps_dot").Width(dot).Height(dot).Margin(0, 7, UnitValue.StretchOne, UnitValue.StretchOne).Rounded(dot / 2f)
                    .BackgroundColor(dotColor).Glow(0, 0, 8, 0, dotColor).IsNotInteractable();
                paper.Box("hs_fps_num").Width(UnitValue.Auto).Height(clH).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text(fpsNum, font).TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSize)
                    .Alignment(TextAlignment.MiddleLeft);
                paper.Box("hs_fps_sp").Height(1).IsNotInteractable();
                paper.Box("hs_fps_lbl").Width(UnitValue.Auto).Height(clH).Margin(0, 4, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text("FPS", font).TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleRight);
                paper.Box("hs_fps_sus").Width(UnitValue.Auto).Height(clH).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text($"可达 {sustainedText}", font).TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleRight);
                paper.Box("hs_fps_ms").Width(UnitValue.Auto).MinWidth(UnitValue.Pixels(33)).Height(clH).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text(msText, font).TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleRight);
            }



            paper.Box("hs_cog").Width(clH).Height(clH).Rounded(7)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Text(EditorIcons.Gear, font).TextColor(EditorTheme.Ink400)
                .Hovered.TextColor(EditorTheme.Ink500).End()
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter)
                .OnClick(0, (_, _) => OpenPanel(typeof(ProjectSettingsPanel)));

            string layoutButtonName = _activeLayoutName.Length <= 12
                ? _activeLayoutName : _activeLayoutName[..11] + "...";
            if (layoutButtonName != _statusLayoutKey)
            {
                _statusLayoutKey = layoutButtonName;
                _statusLayoutText = $"Layout: {layoutButtonName} {EditorIcons.ChevronDown}";
            }
            paper.Box("hdr_layout_btn").Width(148).Height(clH).Rounded(7)
                .Padding(9, 9, 0, 0)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Text(_statusLayoutText, font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleCenter)
                .Tooltip("Layout")
                .OnClick(0, (_, _) => Origami.ContextMenu(
                    (float)paper.PointerPos.X, (float)paper.PointerPos.Y, BuildLayoutMenu));
        }
    }

    // A themed chip that auto-sizes to its text (horizontal padding + Auto width, no MeasureText).
    // Flat on the toolbar strip — Unity 6.7's toolbar readouts carry no chip fill.
    private static void StatusChip(Paper paper, string id, float hRect, string text, XEngine.Scribe.FontFile font)
    {
        using (paper.Row(id).Width(UnitValue.Auto).Height(hRect).Padding(10, 10, 0, 0).Rounded(7)
            .IsNotInteractable().Enter())
        {
            paper.Box(id + "_t").Width(UnitValue.Auto).Height(hRect).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(text, font).TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleCenter);
        }
    }

    public override void EndGui(Paper paper)
    {
        // On-boarding guide overlay (above panels/header, below Origami's popovers/toasts).
        GUI.EditorGuide.Draw(paper, (float)Time.UnscaledDeltaTime);

        // Unity-style corner progress card for popup-worthy background tasks (import/compile).
        // Pushed before EndFrame so the modal stack draws it this frame.
        GUI.Popups.ProgressCornerCard.Tick();

        // Render all Origami overlay systems (drag-drop, context menus, modals, toasts, tooltips)
        OrigamiUI.Origami.EndFrame(paper);

        // Intro animation overlay
        if (_introTime < IntroDuration)
        {
            // Clamp delta to avoid jumping the animation after heavy loading frames.
            // A normal frame is ~16ms; anything above 100ms is a loading stall.
            double introDelta = Math.Min(Time.UnscaledDeltaTime, 0.05);
            _introTime += introDelta;
            DrawIntro(paper);
        }

        // Cycling tip strip drawn last so it sits on top of both the launcher card
        // and the intro animation bars, giving the user something to read while the
        // project loads. The strip fades out as the bars slide away to reveal the
        // editor so it never lingers over a loaded project.
        if (!ProjectLauncher.IsOpen && _introTime < IntroDuration)
        {
            float tipAlpha = 1f;
            const double openStart = IntroCloseDuration + IntroHoldDuration;
            const double fadeOutDuration = 0.8;
            if (_introTime >= openStart)
            {
                float t = (float)((_introTime - openStart) / fadeOutDuration);
                tipAlpha = 1f - Math.Clamp(t, 0f, 1f);
            }

            ProjectLauncher.DrawTipStrip(paper, (float)Time.UnscaledDeltaTime, tipAlpha);
        }

        // Pop the editor Origami theme now that all rendering (including overlays) is done.
        _origamiScope?.Dispose();
        _origamiScope = null;
    }

    // ================================================================
    //  Menu Bar (Origami AppBar)
    // ================================================================

    private void DrawMenuBar(Paper paper, float w, float band)
    {
        float pad = EditorTheme.DockPadding;
        using (paper.Row("menubar_host").PositionType(PositionType.SelfDirected)
            .Width(UnitValue.Auto).Height(band)
            .Margin(UnitValue.Pixels(pad), UnitValue.StretchOne, UnitValue.StretchOne, UnitValue.StretchOne).RowBetween(2).Enter())
        {
            var bar = Origami.MenuBar(paper, "menubar").Height(band).Transparent()
                .ItemColors(EditorTheme.MenuBarInk, EditorTheme.MenuBarInkMuted, EditorTheme.MenuBarHover);
            foreach (var root in MenuRegistry.RootMenus)
                if (root.HasSubItems)
                    bar.Menu(root.Label, ctx => BuildMenu(ctx, root.SubItems));
            bar.Show();
        }
    }

    /// <summary>
    /// Translates a registered <see cref="AppMenuItem"/> subtree into Origami's ContextBuilder,
    /// which is how MenuBar dropdowns are populated. Dynamic label/enabled/checked funcs are
    /// evaluated here since the dropdown is rebuilt each frame it is open.
    /// </summary>
    private static void BuildMenu(ContextBuilder ctx, IReadOnlyList<AppMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                ctx.Separator();
                continue;
            }

            string label = item.DynamicLabelFunc?.Invoke() ?? item.Label;
            bool enabled = item.IsEnabledFunc?.Invoke() ?? item.IsEnabled;

            if (item.HasSubItems)
            {
                List<AppMenuItem> sub = item.SubItems;
                ctx.Submenu(label, c => BuildMenu(c, sub), item.Icon);
            }
            else
            {
                ctx.Item(label, item.OnClick ?? (() => { }), enabled, icon: item.Icon, on: item.IsCheckedFunc?.Invoke() ?? false);
            }
        }
    }

    // ================================================================
    //  Status Bar (Origami AppBar at bottom)
    // ================================================================

    // The status bar sources its log data from the shared ConsolePanel store; this just ensures
    // that store is subscribed to Debug.OnLog even before the Console panel is opened.
    private static void InitializeStatusBar() => ConsolePanel.EnsureSubscribed();

    // Backend name shown in the footer — sourced from the active IGraphicsDevice.
    private static string GraphicsBackendDisplayName =>
        XEngine.Runtime.RHI.GraphicsBackendSelection.GetDisplayName(Graphics.Device);

    private void DrawStatusBar(Paper paper, float w, float h)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        float sh = EditorTheme.StatusBarHeight;
        float fs = EditorTheme.FontSizeSmall;
        var dim = EditorTheme.Ink300;
        var mono = EditorTheme.FontMono ?? font;

        // Glyph-icon (EditorIcons/FontAwesome) + text cell.
        void GlyphCell(string id, string glyph, System.Drawing.Color glyphColor, string text,
            System.Drawing.Color textColor, string? tooltip = null)
        {
            var row = paper.Row(id).Width(UnitValue.Auto).Height(sh).Margin(0, 8, UnitValue.StretchOne, UnitValue.StretchOne);
            if (tooltip != null) row.Tooltip(tooltip);
            using (row.Enter())
            {
                paper.Box(id + "_i").Width(14).Height(sh).Margin(0, 4, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text(glyph, font).TextColor(glyphColor).FontSize(fs).Alignment(PaperUI.TextAlignment.MiddleCenter);
                paper.Box(id + "_t").Width(UnitValue.Auto).Height(sh).IsNotInteractable()
                    .Text(text, font).TextColor(textColor).FontSize(fs).Alignment(PaperUI.TextAlignment.MiddleLeft);
            }
        }

        // Console severity icon (Origami icon) + number, for the log counters.
        void CounterCell(string id, LogSeverity sev, int n)
        {
            var (icon, color) = ConsolePanel.SeverityStyle(sev);
            using (paper.Row(id).Width(UnitValue.Auto).Height(sh).Margin(0, 8, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
            {
                paper.Box(id + "_i").Width(14).Height(sh).Margin(0, 3, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Icon(paper, icon, color, size: 12f);
                paper.Box(id + "_t").Width(UnitValue.Auto).Height(sh).IsNotInteractable()
                    .Text(n.ToString(), font).TextColor(dim).FontSize(fs).Alignment(PaperUI.TextAlignment.MiddleLeft);
            }
        }

        void Divider(string id) => paper.Box(id).Width(1).Height(sh).Margin(0, 0, 5, 5)
            .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

        // Uniform edge padding: the footer's left/right edges and both sides of every divider
        // all use this, so the columns are evenly spaced and the dividers sit centered in the gap.
        float pad = 10f;

        using (paper.Row("statusbar").PositionType(PositionType.SelfDirected)
            .Position(0, h - sh).Size(w, sh)
            // Unity 6.7's footer is the same near-black band as the toolbar (#141414).
            .BackgroundColor(EditorTheme.Toolbar).Enter())
        {
            _shellPaint.AttachStatus(paper);
            // ---------- Column 1: console (last log on the left, counters on the right) ----------
            using (paper.Row("sb_console").Width(UnitValue.StretchOne).Height(sh).Padding(pad, pad, 0, 0).Enter())
            {
                var last = ConsolePanel.LastLog();
                if (last.HasValue)
                {
                    var (sev, msg, src, cnt) = last.Value;
                    var (icon, color) = ConsolePanel.SeverityStyle(sev);
                    paper.Box("sb_log_i").Width(16).Height(sh).Margin(0, 5, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                        .Icon(paper, icon, color, size: 13f);
                    paper.Box("sb_log_m").Width(UnitValue.StretchOne).Height(sh).Margin(0, 6, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                        .Text(msg, font).TextTruncate().TextColor(EditorTheme.Ink400).FontSize(fs)
                        .Alignment(PaperUI.TextAlignment.MiddleLeft).TextTruncate();
                    if (!string.IsNullOrEmpty(src))
                        paper.Box("sb_log_s").Width(UnitValue.Auto).Height(sh).Margin(0, 6, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                            .Text($"[{src}]", mono).TextColor(EditorTheme.InkDim).FontSize(fs - 1)
                            .Alignment(PaperUI.TextAlignment.MiddleLeft);
                    if (cnt > 1)
                        paper.Box("sb_log_c").Width(UnitValue.Auto).Height(sh).IsNotInteractable()
                            .Text($"x{cnt}", font).TextColor(EditorTheme.InkDim).FontSize(fs - 1)
                            .Alignment(PaperUI.TextAlignment.MiddleLeft);
                }

                //paper.Box("sb_console_spacer");

                var (info, warn, err) = ConsolePanel.LogCounts();
                CounterCell("sb_cnt_info", LogSeverity.Normal, info);
                CounterCell("sb_cnt_warn", LogSeverity.Warning, warn);
                CounterCell("sb_cnt_err", LogSeverity.Error, err);
            }

            Divider("sb_div1");

            // ---------- Column 2: current scene ----------
            string? scenePath = EditorSceneManager.CurrentScenePath;
            string sceneName = !string.IsNullOrEmpty(scenePath)
                ? System.IO.Path.GetFileNameWithoutExtension(scenePath)
                : (Runtime.Resources.Scene.Current != null ? Loc.Get("editor.untitled_scene") : Loc.Get("hierarchy.no_scene_loaded"));
            using (paper.Row("sb_scene").Width(UnitValue.Auto).Height(sh).Padding(pad, pad, 0, 0).Enter())
                GlyphCell("sb_scene_cell", EditorIcons.Shapes, EditorTheme.Ink300, sceneName, EditorTheme.Ink400);

            Divider("sb_div2");

            // ---------- Column 2.5: background-task progress (Unity-style status-bar spinner) ----------
            StatusBarProgressCell.Draw(paper);

            // ---------- Column 3: editor stats on the left, graphics backend + Git on the right ----------
            using (paper.Row("sb_stats").Height(sh).Padding(pad, pad, 0, 0).Enter())
            {
                long memMb = _dispMemMb;
                GlyphCell("sb_sel", EditorIcons.ArrowPointer, EditorTheme.Ink300, Selection.Count.ToString(), dim, Loc.Get("editor.stat_selected"));
                GlyphCell("sb_mem", EditorIcons.Microchip, EditorTheme.Ink300, $"{memMb} MB", dim, Loc.Get("editor.stat_memory"));

                paper.Box("sb_stats_spacer");


                Divider("sb_div3");
                DrawGitCell();
            }
        }

        // Git status for the open project (branch + dirty/clean colour), far right of the footer.
        void DrawGitCell()
        {
            GitInfo.Poll();

            var iconColor = EditorTheme.InkDim;
            var textColor = EditorTheme.InkDim;
            string text, tip;
            if (!GitInfo.GitInstalled)
            {
                text = Loc.Get("editor.git_not_installed");
                tip = Loc.Get("editor.git_not_installed_tip");
            }
            else if (!GitInfo.IsRepository)
            {
                text = Loc.Get("editor.git_no_repo");
                tip = Loc.Get("editor.git_no_repo_tip");
            }
            else
            {
                text = GitInfo.Branch;
                iconColor = GitInfo.HasChanges ? EditorTheme.Amber400 : EditorTheme.Green400;
                textColor = EditorTheme.Ink400;
                tip = GitInfo.HasChanges
                    ? Loc.Get("editor.git_dirty", new { branch = GitInfo.Branch })
                    : Loc.Get("editor.git_clean", new { branch = GitInfo.Branch });
            }

            GlyphCell("sb_git", EditorIcons.CodeBranch, iconColor, text, textColor, tip);
        }
    }

    // ================================================================
    //  Editor File Dialog Helper
    // ================================================================

    private static OrigamiUI.FileDialogConfig? s_fileDialogConfig;

    public static OrigamiUI.FileDialogConfig FileDialogConfig
    {
        get
        {
            s_fileDialogConfig ??= new OrigamiUI.FileDialogConfig
            {
                GetIcon = (ext, isDir) => isDir ? EditorIcons.Folder : EditorRegistries.GetFileIcon("file" + ext),
                QuickAccess =
                [
                    (Loc.Get("editor.dir_desktop"), EditorIcons.Desktop, Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                    (Loc.Get("editor.dir_documents"), EditorIcons.FolderOpen, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                    (Loc.Get("editor.dir_downloads"), EditorIcons.Download, System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
                    (Loc.Get("editor.dir_user"), EditorIcons.User, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                ],
                GetDrives = () => System.IO.DriveInfo.GetDrives()
                    .Where(d => d.IsReady).Select(d => (d.Name, d.Name)).ToArray(),
            };
            return s_fileDialogConfig;
        }
    }

    /// <summary>Open a file dialog with editor config (icons, quick access, drives).</summary>
    public static void OpenFileDialog(FileDialogMode mode, Action<string?> onComplete,
        string? startPath = null, string[]? filters = null, string[]? filterLabels = null)
        => OrigamiUI.FileDialog.Open(mode, onComplete, startPath, filters, filterLabels, FileDialogConfig);

    private const int BarCount = 10;

    public static string GetEmbeddedResourceText(string resource)
    {
        var stream = GetEmbeddedResource(resource);

        string data = "";
        using (StreamReader reader = new StreamReader(stream))
        {
            data = reader.ReadToEnd();
        }

        return data;
    }

    public static Stream? GetEmbeddedResource(string resource)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resourceName = "XEngine.Editor.Resources." + resource;

        var stream = assembly.GetManifestResourceStream(resourceName);
        return stream;
    }

    public static FileStream? GetResource(string resource)
    {
        var assembly = Assembly.GetExecutingAssembly();

        var resourceName = resource;

        var pathToFile = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) +
                          resourceName;

        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            using (var fileStream = File.Create(pathToFile))
            {
                stream.Seek(0, SeekOrigin.Begin);
                stream.CopyTo(fileStream);
            }
        }
        return File.OpenRead(pathToFile);
    }

    private void DrawIntro(Paper paper)
    {
        float w = paper.ScreenRect.Size.X;
        float h = paper.ScreenRect.Size.Y;

        paper.Box("intro_overlay")
            .PositionType(PositionType.SelfDirected).Position(0, 0).Size(w, h)
            .IsNotInteractable()
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                float cx = w / 2f;
                float cy = h / 2f;
                var font = EditorTheme.FontLogo ?? EditorTheme.DefaultBoldFont;
                var black = XEngine.Vector.Color32.FromArgb(255, 8, 8, 10);
                float barH = (float)h / BarCount;
                double time = _introTime;

                // -- CLOSE PHASE (0 -> IntroCloseDuration): Bars slide IN, text fades in --
                if (time < IntroCloseDuration)
                {
                    float t = (float)(time / IntroCloseDuration); // 0->1

                    // Bars slide in from off-screen
                    for (int i = 0; i < BarCount; i++)
                    {
                        float delay = i * 0.04f;
                        float slideDuration = 0.5f;
                        float barPhase = Math.Clamp((t - delay) / slideDuration, 0f, 1f);
                        float eased = EaseInOutQuart(barPhase);

                        // Slide from off-screen to on-screen (reverse of open)
                        float slideX = (i % 2 == 0) ? -(1f - eased) * w : (1f - eased) * w;

                        float barY = i * barH;
                        canvas.RectFilled(slideX, barY, w, barH + 1, black);
                    }

                    // Logo + wordmark fade in during second half
                    if (t > 0.5f)
                    {
                        float textPhase = (t - 0.5f) / 0.5f;
                        float eased = EaseOutQuart(textPhase);
                        DrawIntroBrand(canvas, cx, cy, (byte)(eased * 255), font);
                    }
                }
                // -- HOLD PHASE: brief pause with text visible --
                else if (time < IntroCloseDuration + IntroHoldDuration)
                {
                    canvas.RectFilled(0, 0, w, h, black);

                    DrawIntroBrand(canvas, cx, cy, 255, font);
                }
                // -- OPEN PHASE: Bars slide OUT, text fades out --
                else
                {
                    float openStart = (float)(IntroCloseDuration + IntroHoldDuration);
                    float openDuration = (float)(IntroDuration - openStart);
                    float t = Math.Clamp((float)(time - openStart) / openDuration, 0f, 1f);

                    // Bars slide off screen
                    for (int i = 0; i < BarCount; i++)
                    {
                        float delay = i * 0.05f;
                        float slideDuration = 0.5f;
                        float barPhase = Math.Clamp((t - delay) / slideDuration, 0f, 1f);
                        float eased = EaseInOutQuart(barPhase);

                        float slideX = (i % 2 == 0) ? -eased * w : eased * w;

                        float barY = i * barH;
                        canvas.RectFilled(slideX, barY, w, barH + 1, black);
                    }

                    // Logo + wordmark fade out quickly
                    if (t < 0.3f)
                    {
                        float textFade = 1f - (t / 0.3f);
                        byte alpha = (byte)(EaseOutQuart(textFade) * 255);
                        DrawIntroBrand(canvas, cx, cy, alpha, font);
                    }
                }
            }));
    }

    // The intro brand lockup: the raster XEngine mark sits left of the wordmark, centered as one unit.
    private static void DrawIntroBrand(XEngine.Quill.Canvas canvas, float cx, float cy, byte alpha, Scribe.FontFile? font)
    {
        const string word = "XENGINE";
        const float letterSpacing = 10f, gap = 8f;
        var tint = System.Drawing.Color.FromArgb(alpha, 230, 230, 230);

        // Size the logo to the wordmark's height and measure the text (with its spacing) so the
        // [logo | gap | text] lockup can be centered horizontally as a whole.
        float textW = 0f, lockupH = 88f;
        if (font != null)
        {
            var m = canvas.MeasureText(word, EditorTheme.FontSizeLogo, font, letterSpacing);
            textW = (float)m.X;
            lockupH = (float)m.Y;
        }
        float logoH = lockupH * 1.375f;        // logo drawn ~38% larger than the wordmark height
        float logoW = logoH;
        float totalW = logoW + (textW > 0f ? gap + textW : 0f);
        float left = cx - totalW / 2f;

        EditorBranding.Draw(canvas,
            new Rect(left, cy - logoH / 2f, left + logoW, cy + logoH / 2f), alpha);

        if (font != null)
        {
            var textColor = XEngine.Vector.Color32.FromArgb(alpha, 230, 230, 230);
            canvas.DrawText(word, left + logoW + gap, cy, textColor, EditorTheme.FontSizeLogo, font,
                letterSpacing, new Float2(0f, 0.5f), quality: Scribe.FontQuality.Ultra);
        }
    }

    private static float EaseOutQuart(float x) => 1f - MathF.Pow(1f - x, 4f);

    private void InitializePendingProject()
    {
        if (!_pendingProjectInitialization || Project.Current == null) return;
        _pendingProjectInitialization = false;

        // Synchronous fallback (intro-close hold): every step runs to completion, no slicing.
        for (int i = 0; i < ProjectOpenProgress.StepCount; i++)
            ExecuteProjectOpenStep(i, frameSliced: false);

        // A crash record from the previous session prompts once the project is up.
        Core.BugReport.CrashHandler.PromptUnreportedCrashes();
    }

    /// <summary>
    /// Begins the frame-paced project open for a <c>--project</c> launch. No launcher (its static
    /// IsOpen defaults true — close it), no intro animation: the StartupProgressScreen covers the
    /// window until the steps finish, then the completion path in <see cref="TickLauncherProjectOpen"/>
    /// sets the title and prompts for unreported crashes.
    /// </summary>
    private void BeginStartupProjectOpen()
    {
        ProjectLauncher.Close();
        _launcherWasOpen = false;
        _introClosing = false;
        _introTime = IntroDuration + 1; // past the end: no intro animation
        ProjectOpenProgress.Begin();
        InvalidateSurface(EditorSurfaceDirtyReason.Layout);
        _frameScheduler.RegisterAnimation(Runtime.Time.UnscaledTotalTime + 30);
    }

    private bool _startupOpenShotDone;
    private bool _startupOpenShot2Done;
    private double _openStartedAt = -1;

    /// <summary>
    /// Acceptance evidence for the startup progress screen: with XENGINE_WINDOW_SHOT_OUT set,
    /// captures window shots ~0.9s (scan phase) and ~4s (import phase) after the open STARTED
    /// (paths + ".open.png" / ".open2.png"). Relative to the open, not editor start: a launcher
    /// open begins minutes into the session, where absolute-time conditions would both fire on
    /// the first frame before the progress screen ever painted. The logged present-count doubles
    /// as "the window kept rendering during the open" proof.
    /// </summary>
    private void CaptureStartupOpenShot()
    {
        string? dir = Environment.GetEnvironmentVariable("XENGINE_WINDOW_SHOT_OUT");
        if (string.IsNullOrEmpty(dir) || !ProjectOpenProgress.IsActive)
            return;

        if (_openStartedAt < 0)
            _openStartedAt = Time.UnscaledTotalTime;

        double intoOpen = Time.UnscaledTotalTime - _openStartedAt;
        if (!_startupOpenShotDone && Window.PresentCount > 0 && intoOpen >= 0.9f)
        {
            _startupOpenShotDone = true;
            string? shot = EditorWindowCapture.Capture(dir + ".open.png");
            Runtime.Debug.Log(shot != null
                ? $"Startup open shot #1: {shot} (presents so far: {Window.PresentCount})"
                : "Startup open shot #1 FAILED");
        }
        else if (_startupOpenShotDone && !_startupOpenShot2Done && intoOpen >= 4f)
        {
            _startupOpenShot2Done = true;
            string? shot = EditorWindowCapture.Capture(dir + ".open2.png");
            Runtime.Debug.Log(shot != null
                ? $"Startup open shot #2: {shot} (presents so far: {Window.PresentCount})"
                : "Startup open shot #2 FAILED");
        }
    }

    private void TickLauncherProjectOpen()
    {
        CaptureStartupOpenShot();

        if (!ProjectOpenProgress.IsActive)
            return;
        if (Project.Current == null)
        {
            ProjectOpenProgress.Cancel();
            return;
        }

        if (!ProjectOpenProgress.TryBeginWork())
            return;

        try
        {
            if (ExecuteProjectOpenStep(ProjectOpenProgress.DisplayedStep, frameSliced: true)
                && ProjectOpenProgress.CompleteCurrentStep())
            {
                UpdateEditorTitle();
                ProjectEngineVersion.Write(Project.Current.RootPath, CanonicalEngineVersion);
                // §4.4: the hub only counts an open as successful once the editor reports the
                // project fully ready — process start alone is never success.
                Projects.HubSession.Emit(Projects.HubSession.ProjectReady, new
                {
                    projectRoot = Project.Current.RootPath,
                    name = Project.Current.Name,
                    engineVersion = CanonicalEngineVersion,
                });
                ProjectLauncher.Close();

                // A crash record from the previous session prompts once the project is up.
                Core.BugReport.CrashHandler.PromptUnreportedCrashes();
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError(Loc.Get("launcher.open_failed", new { message = ex.Message }));
            EditorAssetBackend.Instance?.Dispose();
            Runtime.AssetDatabase.Current = null;
            Project.CloseCurrent();
            ProjectOpenProgress.Cancel();
            Toasts.Warning(Loc.Get("launcher.open_failed_title"), ex.Message);
            // A --project open that failed mid-flight leaves the editor project-less: show the
            // launcher so the user can pick (or retry) a project instead of an empty shell.
            if (!ProjectLauncher.IsOpen)
                ProjectLauncher.Initialize();
        }
    }

    private EditorAssetBackend? _openingAssetBackend;

    /// <summary>
    /// Runs one project-open step. Returns true when the step finished; frame-sliced callers pass
    /// <paramref name="frameSliced"/> so the asset-database step imports under the per-frame time
    /// budget instead of blocking (the startup screen keeps animating, the window keeps pumping
    /// messages). Synchronous fallbacks (InitializePendingProject) run steps to completion.
    /// </summary>
    private bool ExecuteProjectOpenStep(int step, bool frameSliced)
    {
        switch (step)
        {
            case 0:
                // Script assemblies must load BEFORE the asset scan in step 2: the scan picks
                // importers via EditorRegistries, and package Editor assemblies (optional
                // importers like com.xengine.spine) only exist after this load. A stale compile
                // runs on a background thread here — the startup/launcher screen shows live
                // "Compiling …" detail while the window keeps presenting frames.
                return ScriptAssemblyManager.StartupLoadStep(Project.Current!);
            case 1:
                // Project settings load from ProjectSettings/*.yaml directly (no asset database
                // dependency), so they can load as soon as assemblies exist — the registries and
                // the import pass below read live settings. This also gives launcher-opened
                // projects the settings load the --project path always had.
                EditorRegistries.OnProjectOpened();
                ReinitializeRegistries();
                // Launcher-opened project just loaded fresh script assemblies — the AI server (if
                // already started) must re-scan for package runtime tools. A --project launch
                // starts it here (assemblies load in step 0, after Initialize already ran).
                if (Program.Serve)
                {
                    if (AiServer == null) StartAiServer();
                    else RestartAiServer();
                }
                return true;
            case 2:
                // Frame-sliced asset database open: scan + import advance under the per-frame
                // budget (EditorSettings.AssetWorkSliceBudgetMs), feeding the startup screen's
                // detail line live; a single blocking Initialize is the synchronous fallback.
                _openingAssetBackend ??= new EditorAssetBackend(Project.Current!);
                if (!_openingAssetBackend.InitializeStep(
                        frameSliced ? EditorSettings.Instance.AssetWorkSliceBudgetMs : float.PositiveInfinity))
                    return false;

                _openingAssetBackend = null;
                // The initial project load placed package assemblies in the collectible script
                // context, and the initialize re-ran EditorRegistries.Initialize — run the normal
                // reload registration pass now that project services are ready, so package-owned
                // menus, inspectors, and redirects are visible on first launch rather than only
                // after the first script recompilation.
                ReinitializeRegistries();
                return true;
            case 3:
                ApplyPendingDockLayout();
                return true;
            case 4:
                // Restore scene (from --restore-scene or last saved).
                if (Program.RestoreScenePath != null && System.IO.File.Exists(Program.RestoreScenePath))
                    RestoreAutoSavedScene(Program.RestoreScenePath);
                else
                    EditorSceneManager.EnsureSceneLoaded();
                SceneVisibilityManager.Install();
                SceneVisibilityManager.BindScene(EditorSceneManager.CurrentScenePath);
                InvalidateSurface(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.AssetData);
                return true;
            default:
                throw new InvalidOperationException($"Unknown project-open step {step}.");
        }
    }

    private void ApplyPendingDockLayout()
    {
        DockSpace previousDockSpace = _dockSpace;
        DockNode? loadedRoot = LoadDockLayout();
        DockNode root = loadedRoot ?? CreateDefaultLayout();
        var dockSpace = new DockSpace(root);
        if (loadedRoot == null)
        {
            dockSpace.FloatingWindows.AddRange(previousDockSpace.FloatingWindows);
            previousDockSpace.FloatingWindows.Clear();
        }
        if (_workspace.TryReplaceDockSpace(0, dockSpace))
        {
            _dockSpace = dockSpace;
            _nativeSurfaceManager?.ReconfigureHost(_mainSurfaceHost!);
        }
        MigrateLegacyFloatingWindows();
    }

    private static float EaseInOutQuart(float x) => x < 0.5f ? 8f * x * x * x * x : 1f - MathF.Pow(-2f * x + 2f, 4f) / 2f;

    private Scribe.FontFile? LoadFallbackFont(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) { Runtime.Debug.LogWarning($"Could not load font resource: {resourceName}"); return null; }
        Runtime.Debug.Log($"Loaded fallback font: {resourceName} ({stream.Length} bytes)");
        var fontFile = new Scribe.FontFile(stream);
        AddFallbackFont(fontFile);
        return fontFile;
    }

    private void LoadSystemFallbackFonts()
    {
        // CJK and international font fallback chain. Tries common system fonts
        // in priority order. These cover Japanese, Chinese, Korean, Cyrillic,
        // Arabic, Thai, and other scripts not in the primary Latin font.
        string[] fallbackFamilies =
        [
            // Japanese
            "Yu Gothic UI",        // Windows 10+
            "Meiryo",              // Windows Vista+
            "Hiragino Sans",       // macOS

            // Chinese (Simplified)
            "Microsoft YaHei",     // Windows
            "PingFang SC",         // macOS

            // Chinese (Traditional)
            "Microsoft JhengHei",  // Windows
            "PingFang TC",         // macOS

            // Korean
            "Malgun Gothic",       // Windows
            "Apple SD Gothic Neo", // macOS

            // Universal CJK fallback
            "Noto Sans CJK SC",    // Linux / installed
            "Noto Sans CJK",       // Linux / installed

            // Cyrillic/Greek (usually covered by primary font, but just in case)
            "Noto Sans",           // Linux
        ];

        var systemFonts = PaperInstance.EnumerateSystemFonts().ToArray();
        int loaded = 0;

        foreach (var family in fallbackFamilies)
        {
            // Style is a preference, not a requirement — collection first faces and localized
            // style naming vary per installer, and a Bold-only face still renders Chinese.
            var font = systemFonts.FirstOrDefault(f =>
                f.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase));

            if (font != null)
            {
                AddFallbackFont(font);
                loaded++;
            }
        }

        // Coverage-probe fallback: on stock zh-CN Windows the named candidates above live in
        // collections whose family name may not round-trip exactly, and a bare system may only
        // carry SimSun/SimHei. Accept ANY installed face that actually draws simplified Chinese
        // rather than shipping tofu.
        if (loaded == 0)
        {
            foreach (var font in systemFonts)
            {
                if (font.FindGlyphIndex(ProbeCodepointZh) != 0 && font.FindGlyphIndex(ProbeCodepointZh2) != 0)
                {
                    Runtime.Debug.Log($"CJK fallback via coverage probe: {font.FamilyName}.");
                    AddFallbackFont(font);
                    loaded++;
                    break;
                }
            }
        }

        if (loaded > 0)
            Runtime.Debug.Log($"Loaded {loaded} system fallback font(s) for international text.");
        else
            Runtime.Debug.Log("No CJK-capable system fallback font found; non-Latin text may render as boxes.");
    }

    /// <summary>Probe glyphs: 中 (U+4E2D) and 国 (U+56FD) — simplified-Chinese coverage canary.</summary>
    private const int ProbeCodepointZh = 0x4E2D;
    private const int ProbeCodepointZh2 = 0x56FD;

    /// <summary>
    /// Register a fallback face on the main Paper instance and remember it in
    /// <see cref="EditorTheme.FallbackFonts"/>, so offscreen Paper instances can adopt the same chain.
    /// </summary>
    private void AddFallbackFont(Scribe.FontFile font)
    {
        PaperInstance.AddFallbackFont(font);
        EditorTheme.FallbackFonts.Add(font);
    }

    /// <summary>
    /// Find an open panel of the given type across all workspace hosts.
    /// </summary>
    public DockPanel? FindOpenPanel(Type panelType)
    {
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            DockSpace dockSpace = _workspace.Hosts[i].DockSpace;
            DockPanel? panel = FindInNode(dockSpace.Root, panelType);
            if (panel != null) return panel;
            for (int j = 0; j < dockSpace.FloatingWindows.Count; j++)
            {
                panel = FindInNode(dockSpace.FloatingWindows[j].Node, panelType);
                if (panel != null) return panel;
            }
        }
        return null;
    }

    private static DockPanel? FindInNode(DockNode? node, Type panelType)
    {
        if (node == null) return null;
        if (node.IsLeaf)
            return node.Tabs?.FirstOrDefault(t => t.GetType() == panelType);
        return FindInNode(node.ChildA, panelType) ?? FindInNode(node.ChildB, panelType);
    }

    /// <summary>
    /// Returns true when the pointer currently sits over the leaf hosting a panel of the given type
    /// in the main dock space. Uses the last drawn frame's leaf rect and the main window Paper's
    /// pointer position (both in Paper logical coordinates), so it is valid from out-of-frame
    /// callbacks such as <see cref="Window.FileDrop"/>. Returns false if no such panel is docked.
    /// </summary>
    public bool IsMouseOverPanel(Type panelType)
    {
        if (_dockSpace == null) return false;
        Paper? paper = PaperInstance;
        if (paper == null) return false;
        return _dockSpace.TryGetPanelRect(panelType, out Rect rect) && rect.Contains(paper.PointerPos);
    }

    /// <summary>Enumerate every open panel across all workspace hosts.</summary>
    private IEnumerable<DockPanel> EnumerateAllPanels()
    {
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            DockSpace dockSpace = _workspace.Hosts[i].DockSpace;
            foreach (var p in EnumerateNodePanels(dockSpace.Root))
                yield return p;
            for (int j = 0; j < dockSpace.FloatingWindows.Count; j++)
                foreach (var p in EnumerateNodePanels(dockSpace.FloatingWindows[j].Node))
                    yield return p;
        }
    }

    private static IEnumerable<DockPanel> EnumerateNodePanels(DockNode? node)
    {
        if (node == null) yield break;
        if (node.IsLeaf)
        {
            if (node.Tabs != null)
                foreach (var tab in node.Tabs)
                    yield return tab;
            yield break;
        }
        foreach (var p in EnumerateNodePanels(node.ChildA)) yield return p;
        foreach (var p in EnumerateNodePanels(node.ChildB)) yield return p;
    }

    /// <summary>
    /// Request another frame only for the visible host of a live-data panel.
    /// </summary>
    public void RequestContinuousRedraw(DockPanel? panel = null)
    {
        if (panel == null)
        {
            _frameScheduler.Invalidate(EditorSurfaceDirtyReason.Animation);
            return;
        }
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            if (!host.IsVisible || !host.IsPanelVisible(panel)) continue;
            host.FrameScheduler.Invalidate(EditorSurfaceDirtyReason.Animation);
            if (host.Id == 0) _frameScheduler.Invalidate(EditorSurfaceDirtyReason.Animation);
        }
    }

    // ── Play-mode panel maximize (Unity's Maximize on Play) ─────────────────
    private DockSpace? _maximizedDockSpace;
    private DockNode? _maximizedSavedRoot;
    private DockNode? _maximizedLeaf;
    private DockNode? _maximizedLeafParent;

    /// <summary>
    /// Temporarily present only the leaf node holding <paramref name="panelType"/> by swapping it in
    /// as the dock root. The original tree object stays intact and is restored on exit play — no
    /// serialization is involved, so the layout comes back exactly as it was.
    /// </summary>
    internal void MaximizePanel(Type panelType)
    {
        if (_maximizedSavedRoot != null) return;
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            DockSpace dockSpace = _workspace.Hosts[i].DockSpace;
            DockNode? leaf = FindLeafContaining(dockSpace.Root, panelType);
            if (leaf == null) continue;
            if (ReferenceEquals(dockSpace.Root, leaf)) return; // leaf already fills the layout
            _maximizedDockSpace = dockSpace;
            _maximizedSavedRoot = dockSpace.Root;
            _maximizedLeaf = leaf;
            _maximizedLeafParent = leaf.Parent;
            leaf.Parent = null;
            dockSpace.Root = leaf;
            return;
        }
    }

    /// <summary>Undo <see cref="MaximizePanel"/>; a no-op when nothing is maximized.</summary>
    internal void RestoreMaximizedPanel()
    {
        if (_maximizedDockSpace == null || _maximizedSavedRoot == null) return;
        if (_maximizedLeaf != null) _maximizedLeaf.Parent = _maximizedLeafParent;
        _maximizedDockSpace.Root = _maximizedSavedRoot;
        _maximizedDockSpace = null;
        _maximizedSavedRoot = null;
        _maximizedLeaf = null;
        _maximizedLeafParent = null;
    }

    private static DockNode? FindLeafContaining(DockNode? node, Type panelType)
    {
        if (node == null) return null;
        if (node.IsLeaf)
        {
            if (node.Tabs != null)
                foreach (var tab in node.Tabs)
                    if (tab.GetType() == panelType)
                        return node;
            return null;
        }
        return FindLeafContaining(node.ChildA, panelType) ?? FindLeafContaining(node.ChildB, panelType);
    }

    /// <summary>
    /// Open a panel. If it's already open, focus it. Otherwise create a new instance as a floating window.
    /// </summary>
    public void OpenPanel(Type panelType)
    {
        // Check if already open
        var existing = FindOpenPanel(panelType);
        if (existing != null)
        {
            FocusPanel(panelType);
            return;
        }

        // Create new instance
        if (Activator.CreateInstance(panelType) is not DockPanel panel) return;
        OpenPanelInstance(panel);
    }

    /// <summary>Open a pre-created panel instance in an owned top-level editor window.</summary>
    public void OpenPanelInstance(DockPanel panel, float width = 800, float height = 560)
    {
        Rect mainBounds = _mainSurfaceHost?.ScreenBounds
            ?? new Rect(0, 0, Window.Size.X, Window.Size.Y);
        Rect bounds = new(
            mainBounds.Min.X + 200f,
            mainBounds.Min.Y + 200f,
            mainBounds.Min.X + 200f + width,
            mainBounds.Min.Y + 200f + height);
        if (!_workspace.TryCreateDetachedHost(panel, bounds, out _))
            _workspace.TryAddPanelToHost(panel, 0);
    }

    private void MigrateLegacyFloatingWindows()
    {
        var hostIds = new List<int>(_workspace.Hosts.Count);
        for (int i = 0; i < _workspace.Hosts.Count; i++)
            hostIds.Add(_workspace.Hosts[i].Id);
        for (int i = 0; i < hostIds.Count; i++)
            _workspace.MigrateLegacyFloatingWindows(hostIds[i]);
    }

    /// <summary>
    /// Check if a panel of the given type is currently open.
    /// </summary>
    public bool IsPanelOpen(Type panelType) => FindOpenPanel(panelType) != null;

    /// <summary>Prompt for a scene file path and save the current scene there.</summary>
    private void PromptSaveAs()
    {
        if (Project.Current == null) return;
        EditorApplication.OpenFileDialog(FileDialogMode.Save, path =>
        {
            if (path == null || Project.Current == null) return;
            string rel = EditorAssetBackend.NormalizePath(
                System.IO.Path.GetRelativePath(Project.Current.AssetsPath, path));
            if (!rel.EndsWith(".scene")) rel += ".scene";

            if (EditorSceneManager.SaveAs(rel))
                Toasts.Success(Loc.Get("save.saved"),
                    Loc.Get("save.scene", new { name = Runtime.Resources.Scene.Current?.Name ?? "Untitled" }));
        }, Project.Current.AssetsPath,
           new[] { "*.scene" }, new[] { Loc.Get("editor.filter_scene") });
    }

    // ================================================================
    //  Menu Registration
    // ================================================================

    private void RegisterMenus()
    {
        string file = Loc.Get("menu.file");
        string edit = Loc.Get("menu.edit");

        // File menu
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.new_scene")}", () => EditorSceneManager.NewScene());
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.open_scene")}", () =>
        {
            EditorApplication.OpenFileDialog(FileDialogMode.Open, path =>
            {
                if (path == null || Project.Current == null) return;
                string rel = EditorAssetBackend.NormalizePath(
                    System.IO.Path.GetRelativePath(Project.Current.AssetsPath, path));
                EditorSceneManager.OpenScene(rel);
            }, Project.Current?.AssetsPath,
               new[] { "*.scene" }, new[] { Loc.Get("editor.filter_scene") });
        });
        MenuRegistry.RegisterSeparator(file);
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.save_scene")}", () =>
        {
            if (!EditorSceneManager.Save())
                PromptSaveAs();
        });
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.save_scene_as")}", () => PromptSaveAs());
        MenuRegistry.RegisterSeparator(file);
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.open_project")}", () => ReturnToLauncher());
        MenuRegistry.RegisterSeparator(file);
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.build_project")}", () => OpenPanel(typeof(BuildSettingsPanel)));
        MenuRegistry.RegisterSeparator(file);
        MenuRegistry.Register($"{file}/{Loc.Get("menu.file.exit")}", () => Game.Quit());

        // Edit menu
        MenuRegistry.Register($"{edit}/{Loc.Get("menu.edit.undo")}", () => Undo.PerformUndo(),
            isEnabled: () => Undo.CanUndo,
            dynamicLabel: () => Undo.UndoDescription);
        MenuRegistry.Register($"{edit}/{Loc.Get("menu.edit.redo")}", () => Undo.PerformRedo(),
            isEnabled: () => Undo.CanRedo,
            dynamicLabel: () => Undo.RedoDescription);
        MenuRegistry.RegisterSeparator(edit);
        MenuRegistry.Register($"{edit}/{Loc.Get("menu.edit.project_settings")}", () => OpenPanel(typeof(ProjectSettingsPanel)));
        MenuRegistry.Register($"{edit}/{Loc.Get("menu.edit.save_layout")}", () => SaveProjectState());
        MenuRegistry.RegisterSeparator(edit);
        MenuRegistry.Register($"{edit}/{Loc.Get("menu.edit.preferences")}", () => OpenPanel(typeof(PreferencesPanel)));

        // All [MenuItem] methods: populates Assets/Create/*, Window/*, GameObject/*, and any custom roots
        MenuItemAttribute.PopulateMenuRegistry();

        // Help menu (bug reporter + diagnostics)
        string help = Loc.Get("menu.help");
        MenuRegistry.Register($"{help}/{Loc.Get("menu.help.report_bug")}",
            () => GUI.Popups.BugReporterDialog.Open(), icon: EditorIcons.Bug);
        MenuRegistry.RegisterSeparator(help);
        MenuRegistry.Register($"{help}/{Loc.Get("menu.help.diagnostics")}/{Loc.Get("menu.help.throw_test")}",
            () => Core.BugReport.CrashHandler.ThrowTestException());

        // Remaining Assets menu items that live here due to needing OpenFileDialog or db access
        string assets = Loc.Get("menu.assets");
        MenuRegistry.RegisterSeparator(assets);
        MenuRegistry.Register($"{assets}/{Loc.Get("menu.assets.refresh")}", () =>
        {
            if (Project.Current != null)
            {
                var db = new EditorAssetBackend(Project.Current);
                db.Initialize();
            }
        });
        MenuRegistry.Register($"{assets}/{Loc.Get("menu.assets.reimport_all")}", () =>
        {
            var db = EditorAssetBackend.Instance;
            if (db == null) return;
            foreach (var entry in db.GetAllEntries().ToList())
                db.Reimport(entry.Guid);
            Runtime.Debug.Log("[AssetDatabase] Reimported all assets.");
        });

        // About — last top-level menu bar entry (registered last, so it renders at the end).
        // The bar renderer only draws roots WITH subitems, so model Unity's shape here too:
        // an "About" menu whose single entry opens the dialog.
        string aboutRoot = Loc.Get("menu.about");
        MenuRegistry.Register($"{aboutRoot}/{Loc.Get("about.item")}", () => GUI.Popups.AboutDialog.Open(),
            icon: EditorIcons.Info);
        MenuRegistry.RegisterSeparator(assets);
        MenuRegistry.Register($"{assets}/{Loc.Get("menu.assets.import_package")}", () =>
        {
            EditorApplication.OpenFileDialog(FileDialogMode.Open, path =>
            {
                if (path != null && System.IO.File.Exists(path))
                    GUI.Popups.PackageImportDialog.Open(path);
            },
            startPath: Project.Current?.PackagesPath,
            filters: new[] { "*.xenginepackage" },
            filterLabels: new[] { Loc.Get("editor.filter_package") });
        });
    }

    // ================================================================
    //  Script Compilation
    // ================================================================

    /// <summary>
    /// Called by <see cref="ScriptAssemblyManager"/> right after the new script assemblies are
    /// loaded. Re-scans every registry against the fresh assemblies and reloads project settings.
    /// </summary>
    public void ReinitializeAfterReload()
    {
        // Restart the AI server first (if it was stopped for this reload) so tool registration sees
        // the fresh script assemblies.
        if (_aiServerWasRunningBeforeReload)
        {
            _aiServerWasRunningBeforeReload = false;
            StartAiServer();
        }
        ReinitializeRegistries();
    }

    /// <summary>Whether the AI server was running before the current script reload (see
    /// <see cref="ReleaseScriptReferences"/>); used to restore it afterwards.</summary>
    private bool _aiServerWasRunningBeforeReload;

    /// <summary>
    /// Drops every strong reference the editor holds into the script <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
    /// so it can actually be collected when unloaded. This is the counterpart to
    /// <see cref="ReinitializeAfterReload"/>: tear everything down here, rebuild it there.
    ///
    /// Anything that survives this call and transitively reaches a user type (a live instance, a
    /// <see cref="Type"/> handle, a delegate bound to user code, a <see cref="FieldInfo"/>) pins
    /// the old context and forces a full editor restart instead of a hot-reload.
    /// </summary>
    public void ReleaseScriptReferences()
    {
        // 0. The in-process AI server (if running) reflects [McpServerToolType] types out of the
        //    script assemblies (packages compile their Editor code into the collectible ALC) and
        //    keeps tool singletons alive in its DI host - a permanent pin that makes every
        //    hot-reload fail while --serve is active. Stop and dispose it BEFORE the unload (the
        //    bounded wait ensures the host has actually released the assemblies by then), then
        //    restart it from the fresh assemblies in ReinitializeAfterReload. The external MCP
        //    client sees a transport disconnect and must reconnect.
        if (AiServer != null)
        {
            _aiServerWasRunningBeforeReload = true;
            Runtime.Debug.Log("[EditorApplication] Stopping AI server for script reload (MCP client must reconnect)...");
            try { AiServer.StopAsync().Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Runtime.Debug.LogWarning($"[EditorApplication] AI server stop faulted: {ex.Message}"); }
            AiServer.Dispose();
            AiServer = null;
        }
        ReleaseLibraryReflectionCaches();

        CaptureSelectionForReload();

        // 1. Live object graph: the scene's GameObjects hold user MonoBehaviour instances.
        //    The scene was already serialized to disk by SaveSceneForRestart().
        Selection.Clear();
        Undo.Clear();
        Runtime.Resources.Scene.Unload();

        // 1b. Long-lived editor panels cache scene objects (e.g. the Inspector's last target,
        //     the Hierarchy's drag target). Let each drop its references before the unload.
        if (_dockSpace != null)
            foreach (var panel in EnumerateAllPanels())
                if (panel is IScriptReloadCleanup cleanup)
                    try { cleanup.OnScriptReloadCleanup(); } catch { }

        // Release Paper callbacks as they might otherwise pin ALC types across a reload.
        ReleasePaperRetainedCallbacks();


        // 2. Play-mode leftovers (normally empty outside play mode; cleared defensively).
        _savedEditorScene = null;
        _savedEditorTime = null;
        MenuRegistry.Clear();

        // 3. The Echo serializer cache lives in an external package so we can't call OnAssemblyUnload there.
        Echo.Serializer.ClearCache();

        // 4. Everything tagged [OnAssemblyUnload]
        ScriptReloadCallbacks.InvokeAssemblyUnload();
    }

    private void ReleasePaperRetainedCallbacks()
    {
        _shellPaint.Dispose();
        try
        {
            var paper = PaperInstance;
            if (paper == null) return;

            Type t = paper.GetType();
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

            if (t.GetField("_elements", BF)?.GetValue(paper) is not Array elements) return;

            int count = t.GetField("_elementCount", BF)?.GetValue(paper) is int c ? c : 0;
            count = Math.Clamp(count, 0, elements.Length);
            if (count < elements.Length)
                Array.Clear(elements, count, elements.Length - count);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[EditorApplication] Could not reset PaperUI retained callbacks: {ex.Message}");
        }
    }

    // ================================================================
    //  Selection preserve/restore across a hot-reload
    // ================================================================

    private List<SelectionToken>? _reloadSelection;
    private SelectionToken _reloadActive;
    private bool _hasReloadActive;

    /// <summary>
    /// Hot-reload: third-party reflection caches that were populated with package types while the
    /// old script context was live. Neither library exposes a public reset, and both roots were
    /// observed pinning the Zonezero script LoaderAllocator in a gcdump taken after 10 failed GC
    /// attempts (docs/benchmarks/editor-performance/2026-09-14-incremental-ui-n4/lifecycle):
    /// <list type="bullet">
    /// <item>System.Text.Json keeps a <c>JsonTypeInfo</c> per serialized <see cref="Type"/> on each
    /// <see cref="System.Text.Json.JsonSerializerOptions"/> instance; <c>JsonSerializerOptions.Default</c>
    /// and the MCP SDK's shared options had cached package tool result types.</item>
    /// <item>Microsoft.Extensions.AI's <c>AIFunctionFactory</c> caches a reflection descriptor (the
    /// <see cref="MethodInfo"/> of every <c>[McpServerTool]</c> method) in a static
    /// <c>ConditionalWeakTable</c> keyed by the options instance - which is itself static, so the
    /// weak key never dies.</item>
    /// </list>
    /// Both are cleared reflectively; a missing member only logs, the reload then falls back to the
    /// restart path exactly as before.
    /// </summary>
    private static void ReleaseLibraryReflectionCaches()
    {
        try
        {
            MethodInfo? clearCaches = typeof(System.Text.Json.JsonSerializerOptions).GetMethod(
                "ClearCaches", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes);
            if (clearCaches == null)
                Debug.LogWarning("[EditorApplication] JsonSerializerOptions.ClearCaches not found; hot-reload may be pinned by JSON type caches.");

            var optionsSet = new HashSet<System.Text.Json.JsonSerializerOptions>(ReferenceEqualityComparer.Instance)
            {
                System.Text.Json.JsonSerializerOptions.Default,
            };
            AddStaticOptions(optionsSet, "ModelContextProtocol.McpJsonUtilities, ModelContextProtocol.Core", "DefaultOptions");
            AddStaticOptions(optionsSet, "Microsoft.Extensions.AI.AIJsonUtilities, Microsoft.Extensions.AI.Abstractions", "DefaultOptions");
            if (clearCaches != null)
                foreach (var options in optionsSet)
                    clearCaches.Invoke(options, null);

            Type? factory = Type.GetType("Microsoft.Extensions.AI.AIFunctionFactory, Microsoft.Extensions.AI.Abstractions", throwOnError: false);
            Type? descriptor = factory?.GetNestedType("ReflectionAIFunctionDescriptor", BindingFlags.NonPublic);
            int cleared = 0;
            if (descriptor != null)
            {
                foreach (FieldInfo field in descriptor.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                {
                    if (!field.FieldType.IsGenericType ||
                        field.FieldType.GetGenericTypeDefinition() != typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>))
                        continue;
                    object? table = field.GetValue(null);
                    table?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(table, null);
                    cleared++;
                }
            }
            if (factory != null && cleared == 0)
                Debug.LogWarning("[EditorApplication] AIFunctionFactory descriptor cache not found; hot-reload may be pinned by MCP tool MethodInfos.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[EditorApplication] Releasing library reflection caches failed: {ex.Message}");
        }

        static void AddStaticOptions(HashSet<System.Text.Json.JsonSerializerOptions> set, string typeName, string propertyName)
        {
            Type? type = Type.GetType(typeName, throwOnError: false);
            PropertyInfo? property = type?.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(null) is System.Text.Json.JsonSerializerOptions options)
                set.Add(options);
        }
    }

    /// <summary>
    /// Snapshot the current selection as identifier tokens (called before the selection is cleared).
    /// </summary>
    private void CaptureSelectionForReload()
    {
        _reloadSelection = new List<SelectionToken>();
        _hasReloadActive = false;

        foreach (var obj in Selection.Selected)
        {
            if (!TryMakeSelectionToken(obj, out var token))
                continue;

            _reloadSelection.Add(token);
            if (ReferenceEquals(obj, Selection.ActiveObject))
            {
                _reloadActive = token;
                _hasReloadActive = true;
            }
        }
    }

    /// <summary>
    /// Tries to create a selection token to then restore the selection after script reload.
    /// </summary>
    private static bool TryMakeSelectionToken(object obj, out SelectionToken token)
    {
        switch (obj)
        {
            // Scene GameObject - restore by stable scene identifier.
            case GameObject go:
                token = new SelectionToken(SelKind.GameObject, go.Identifier, Guid.Empty, "", "", false);
                return true;
            // Scene component - restore by owning GameObject + component identifier.
            case MonoBehaviour mb when mb.GameObject.IsValid():
                token = new SelectionToken(SelKind.Component, mb.GameObject.Identifier, mb.Identifier, "", "", false);
                return true;
            // Project asset - restore by AssetID via the asset database.
            case EngineObject eo when eo.AssetID != Guid.Empty:
                token = new SelectionToken(SelKind.Asset, eo.AssetID, Guid.Empty, "", "", false);
                return true;
            // Project browser item - identifier-only, rebuilt from its path/guid.
            case ContentItem ci:
                token = new SelectionToken(SelKind.Content, ci.Guid, Guid.Empty, ci.RelativePath, ci.Name, ci.IsFolder);
                return true;
            default:
                token = default;
                return false;
        }
    }

    /// <summary>
    /// Re-resolve the captured selection tokens against the freshly reloaded scene/assets and re-select them.
    /// </summary>
    public void RestoreSelectionAfterReload()
    {
        if (_reloadSelection == null)
            return;

        var tokens = _reloadSelection;
        _reloadSelection = null;

        Selection.Clear();
        object? active = null;

        foreach (var token in tokens)
        {
            object? resolved = ResolveSelectionToken(token);
            if (resolved == null)
                continue;

            Selection.AddToSelection(resolved);
            if (_hasReloadActive && token.Equals(_reloadActive))
                active = resolved;
        }

        if (active != null)
            Selection.ActiveObject = active;

        _hasReloadActive = false;
    }

    private static object? ResolveSelectionToken(SelectionToken token)
    {
        switch (token.Kind)
        {
            case SelKind.GameObject:
                return Runtime.Resources.Scene.Current?.FindObjectByIdentifier<GameObject>(token.Id);
            case SelKind.Component:
                return Runtime.Resources.Scene.Current?.FindObjectByIdentifier<GameObject>(token.Id)?.GetComponentByIdentifier(token.CompId);
            case SelKind.Asset:
                return Runtime.AssetDatabase.Get(token.Id);
            case SelKind.Content:
                // ContentItem compares by Guid + RelativePath, so a rebuilt instance re-selects the same item.
                return new ContentItem { Guid = token.Id, RelativePath = token.Path, Name = token.Name, IsFolder = token.IsFolder };
            default:
                return null;
        }
    }

    private void ReinitializeRegistries()
    {
        ScriptReloadCallbacks.InvokeAssemblyLoad();
        MenuRegistry.Clear();
        RegisterMenus();
    }

    public void RestoreAutoSavedScene(string path)
    {
        try
        {
            string text = System.IO.File.ReadAllText(path);
            var echo = Echo.EchoObject.ReadFromString(text);
            var ctx = Importers.ImportHelper.CreateTrackingContext();
            var scene = Echo.Serializer.Deserialize<Runtime.Resources.Scene>(echo, ctx);
            if (scene != null)
            {
                // Sidecar (written by SaveSceneForRestart) carries the original Assets-relative
                // path + AssetID. Restoring both means subsequent Ctrl+S writes back to the
                // original scene file instead of prompting Save-As.
                string sidecarPath = path + ".meta";
                if (System.IO.File.Exists(sidecarPath))
                {
                    var lines = System.IO.File.ReadAllLines(sidecarPath);
                    if (lines.Length > 0 && !string.IsNullOrEmpty(lines[0]))
                        EditorSceneManager.CurrentScenePath = lines[0];
                    if (lines.Length > 1 && Guid.TryParse(lines[1], out var id))
                        scene.AssetID = id;
                    try { System.IO.File.Delete(sidecarPath); } catch { }
                }

                Runtime.Resources.Scene.Load(scene);
                Undo.Clear();
                Runtime.Debug.Log("Restored auto-saved scene.");
            }

            // Clean up the temp file
            try { System.IO.File.Delete(path); } catch { }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to restore auto-saved scene: {ex.Message}");
            EditorSceneManager.EnsureSceneLoaded();
        }
    }

    // ================================================================
    //  Project Switching
    // ================================================================

    /// <summary>
    /// Saves the editor window position, size and maximization state.
    /// </summary>
    public void SaveEditorWindowState()
    {
        EditorSettings.Instance.WindowX = Window.Position.X;
        EditorSettings.Instance.WindowY = Window.Position.Y;

        if (_persistWindowPlacement)
        {
            EditorSettings.Instance.WindowWidth = Window.Size.X;
            EditorSettings.Instance.WindowHeight = Window.Size.Y;
            EditorSettings.Instance.WindowMaximized = Window.InternalWindow.WindowState == Silk.NET.Windowing.WindowState.Maximized;
        }

        EditorSettings.Instance.Save();
    }

    /// <summary>Grow from the launcher client size to the last editor placement.</summary>
    private void RestoreEditorWindowPlacement()
    {
        var settings = EditorSettings.Instance;
        int width = settings.WindowWidth > 400 ? settings.WindowWidth : 1280;
        int height = settings.WindowHeight > 300 ? settings.WindowHeight : 800;
        _persistWindowPlacement = true;

        Window.InternalWindow.WindowState = Silk.NET.Windowing.WindowState.Normal;
        Window.Size = new Silk.NET.Maths.Vector2D<int>(width, height);
        Window.Position = new Silk.NET.Maths.Vector2D<int>(
            settings.WindowX > 0 ? settings.WindowX : Window.Position.X,
            settings.WindowY > 0 ? settings.WindowY : Window.Position.Y);
        if (settings.WindowMaximized)
            Window.InternalWindow.WindowState = Silk.NET.Windowing.WindowState.Maximized;
    }

    /// <summary>Clear editor caches: cached thumbnails, the saved dock layout (reset to default), and more.</summary>
    public void ClearEditorCache()
    {
        try { Thumbnails.ThumbnailGenerator.DeleteAll(); } catch { }
        EditorAssetBackend.Instance?.ClearThumbnailTextureCache();
        try
        {
            if (Project.Current != null && System.IO.File.Exists(Project.Current.EditorStatePath))
                System.IO.File.Delete(Project.Current.EditorStatePath);
            if (Project.Current != null)
            {
                string v2Path = Path.Combine(Path.GetDirectoryName(Project.Current.EditorStatePath)!,
                    EditorWorkspaceLayout.FileName);
                if (File.Exists(v2Path)) File.Delete(v2Path);
            }
        }
        catch (Exception ex) { Runtime.Debug.LogWarning($"Failed to clear layout: {ex.Message}"); }
        _dockSpace.Root = CreateDefaultLayout();

        EditorSettings.Instance.SeenGuides.Clear();
        EditorSettings.Instance.Save();
    }

    private void SaveDockLayout()
    {
        if (Project.Current == null) return;
        try
        {
            string json = DockSerializer.Serialize(_dockSpace);
            System.IO.File.WriteAllText(Project.Current.EditorStatePath, json);
            EditorWorkspaceLayout.Capture(_workspace).SaveAtomic(GetWorkspaceLayoutPath(Project.Current));
        }
        catch (Exception ex) { Runtime.Debug.LogError($"Failed to save layout: {ex.Message}"); }
    }

    private DockNode? LoadDockLayout()
    {
        if (Project.Current == null) return null;
        string path = Project.Current.EditorStatePath;
        string v2Path = GetWorkspaceLayoutPath(Project.Current);
        if (EditorWorkspaceLayout.TryLoad(v2Path, out EditorWorkspaceLayout? v2)
            && v2 != null && v2.Hosts.Count > 0)
        {
            if (EditorWindowPlacement.TryGetVirtualDesktopBounds(out Rect desktop))
                v2.ClampToDesktop(desktop);
            v2.RestoreSecondaryHosts(_workspace, CreatePanelFromLayout);
            EditorSurfaceLayout? primary = null;
            for (int i = 0; i < v2.Hosts.Count; i++)
                if (v2.Hosts[i].IsMain) { primary = v2.Hosts[i]; break; }
            primary ??= v2.Hosts[0];
            if (!string.IsNullOrWhiteSpace(primary.DockLayoutJson))
            {
                try
                {
                    DockNode? migrated = DockSerializer.Deserialize(primary.DockLayoutJson,
                        _dockSpace.FloatingWindows, CreatePanelFromLayout);
                    if (migrated != null) return migrated;
                }
                catch (Exception ex)
                {
                    Runtime.Debug.LogWarning($"Failed to restore workspaceLayout.v2: {ex.Message}");
                }
            }
        }
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            string json = System.IO.File.ReadAllText(path);
            return DockSerializer.Deserialize(json, _dockSpace.FloatingWindows, CreatePanelFromLayout);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to load layout: {ex.Message}");
            return null;
        }
    }

    private void BuildLayoutMenu(ContextBuilder menu)
    {
        menu.Header("Layouts");
        menu.Item("Default", () => ApplyLayoutPreset("Default", CreateDefaultLayout),
            on: string.Equals(_activeLayoutName, "Default", StringComparison.Ordinal));
        menu.Item("Wide", () => ApplyLayoutPreset("Wide", CreateWideLayout),
            on: string.Equals(_activeLayoutName, "Wide", StringComparison.Ordinal));
        menu.Item("2 by 3", () => ApplyLayoutPreset("2 by 3", CreateTwoByThreeLayout),
            on: string.Equals(_activeLayoutName, "2 by 3", StringComparison.Ordinal));

        IReadOnlyList<(string Name, string Path)> localLayouts = GetLocalLayouts();
        if (localLayouts.Count > 0)
        {
            menu.Header("Local Layouts");
            for (int i = 0; i < localLayouts.Count; i++)
            {
                (string name, string path) = localLayouts[i];
                menu.Item(name, () => LoadLayoutFromPath(path),
                    on: string.Equals(_activeLayoutName, name, StringComparison.Ordinal));
            }
        }

        menu.Separator();
        bool hasProject = Project.Current != null;
        menu.Item("Save Layout...", SaveLayoutLocally, hasProject);
        menu.Item("Save Layout to File...", SaveLayoutToFile, hasProject);
        menu.Item("Load Layout from File...", LoadLayoutFromFile, hasProject);
    }

    private void ApplyLayoutPreset(string name, Func<DockNode> layoutFactory)
    {
        RemoveSecondaryHosts();
        var dockSpace = new DockSpace(layoutFactory());
        if (!_workspace.TryReplaceDockSpace(0, dockSpace)) return;
        _dockSpace = dockSpace;
        _nativeSurfaceManager?.ReconfigureHost(_mainSurfaceHost!);
        _activeLayoutName = name;
        SaveDockLayout();
        InvalidateSurface(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Window);
    }

    private void SaveLayoutLocally()
    {
        if (Project.Current == null) return;
        string directory = GetLocalLayoutDirectory();
        Directory.CreateDirectory(directory);
        OpenFileDialog(FileDialogMode.Save, SaveLayoutToPath, directory,
            new[] { "*" + LayoutFileExtension }, new[] { "XEngine Layout" });
    }

    private void SaveLayoutToFile()
    {
        if (Project.Current == null) return;
        OpenFileDialog(FileDialogMode.Save, SaveLayoutToPath,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            new[] { "*" + LayoutFileExtension }, new[] { "XEngine Layout" });
    }

    private void SaveLayoutToPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(LayoutFileExtension, StringComparison.OrdinalIgnoreCase))
            path += LayoutFileExtension;
        try
        {
            EditorWorkspaceLayout.Capture(_workspace).SaveAtomic(path);
            _activeLayoutName = Path.GetFileNameWithoutExtension(path);
            Toasts.Success("Layout Saved", _activeLayoutName);
            InvalidateSurface(EditorSurfaceDirtyReason.Layout);
        }
        catch (Exception ex)
        {
            Toasts.Error("Layout Save Failed", ex.Message);
        }
    }

    private void LoadLayoutFromFile()
    {
        if (Project.Current == null) return;
        OpenFileDialog(FileDialogMode.Open, LoadLayoutFromPath, GetLocalLayoutDirectory(),
            new[] { "*" + LayoutFileExtension }, new[] { "XEngine Layout" });
    }

    private void LoadLayoutFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!EditorWorkspaceLayout.TryLoad(path, out EditorWorkspaceLayout? layout) || layout == null)
        {
            Toasts.Error("Layout Load Failed", "The selected file is not a supported XEngine layout.");
            return;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        if (!ApplyWorkspaceLayout(layout, name))
        {
            Toasts.Error("Layout Load Failed", "The selected layout does not contain a usable primary workspace.");
            return;
        }
        Toasts.Success("Layout Loaded", name);
    }

    private bool ApplyWorkspaceLayout(EditorWorkspaceLayout layout, string name)
    {
        EditorSurfaceLayout? primary = null;
        for (int i = 0; i < layout.Hosts.Count; i++)
        {
            if (!layout.Hosts[i].IsMain) continue;
            primary = layout.Hosts[i];
            break;
        }
        if (primary == null && layout.Hosts.Count > 0)
            primary = layout.Hosts[0];
        if (primary == null || string.IsNullOrWhiteSpace(primary.DockLayoutJson)) return false;

        var floating = new List<FloatingWindow>();
        DockNode? root;
        try
        {
            root = DockSerializer.Deserialize(primary.DockLayoutJson, floating, CreatePanelFromLayout);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
        if (root == null) return false;

        var dockSpace = new DockSpace(root);
        dockSpace.FloatingWindows.AddRange(floating);
        RemoveSecondaryHosts();
        if (!_workspace.TryReplaceDockSpace(0, dockSpace)) return false;
        _dockSpace = dockSpace;
        _nativeSurfaceManager?.ReconfigureHost(_mainSurfaceHost!);
        if (EditorWindowPlacement.TryGetVirtualDesktopBounds(out Rect desktop))
            layout.ClampToDesktop(desktop);
        layout.RestoreSecondaryHosts(_workspace, CreatePanelFromLayout);
        MigrateLegacyFloatingWindows();
        _activeLayoutName = name;
        SaveDockLayout();
        InvalidateSurface(EditorSurfaceDirtyReason.Layout | EditorSurfaceDirtyReason.Window);
        return true;
    }

    private void RemoveSecondaryHosts()
    {
        for (int i = _workspace.Hosts.Count - 1; i >= 0; i--)
        {
            int hostId = _workspace.Hosts[i].Id;
            if (hostId != 0)
                _workspace.UnregisterHost(hostId);
        }
    }

    private string GetLocalLayoutDirectory()
    {
        if (Project.Current == null) return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(Path.GetDirectoryName(Project.Current.EditorStatePath)!, "Layouts");
    }

    private IReadOnlyList<(string Name, string Path)> GetLocalLayouts()
    {
        if (Project.Current == null) return Array.Empty<(string Name, string Path)>();
        string directory = GetLocalLayoutDirectory();
        if (!Directory.Exists(directory)) return Array.Empty<(string Name, string Path)>();
        try
        {
            var layouts = new List<(string Name, string Path)>();
            foreach (string path in Directory.EnumerateFiles(directory, "*" + LayoutFileExtension))
                layouts.Add((Path.GetFileNameWithoutExtension(path), path));
            layouts.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return layouts;
        }
        catch (IOException) { return Array.Empty<(string Name, string Path)>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<(string Name, string Path)>(); }
    }

    private static string GetWorkspaceLayoutPath(Project project) =>
        Path.Combine(Path.GetDirectoryName(project.EditorStatePath)!, EditorWorkspaceLayout.FileName);

    internal static DockPanel? CreatePanelFromLayout(string typeName, System.Text.Json.Nodes.JsonObject? state)
    {
        Type? type = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(typeName);
            if (type != null && typeof(DockPanel).IsAssignableFrom(type)) break;
            type = null;
        }
        if (type == null) return new MissingDockPanel(typeName);
        if (type == typeof(MissingDockPanel)) return new MissingDockPanel(typeName);
        var panel = Activator.CreateInstance(type) as DockPanel;
        if (panel != null && state != null)
            try { panel.RestoreState(state); } catch { }
        return panel;
    }

    /// <summary>Save layout and settings for the current project.</summary>
    public void SaveProjectState()
    {
        if (Project.Current == null) return;
        SaveEditorWindowState();

        // Skip layout persistence while the game is running panel state snapshots
        // (selection, scene-view camera) would capture the transient playmode state
        // and overwrite authoring state on next open.
        if (!Application.IsPlaying)
            SaveDockLayout();

        EditorRegistries.SaveSettings();
    }

    public void ReturnToLauncher()
    {
        // §6: "back to the project list" now means back to XHub — save state, hand off to the
        // hub, and close this editor. The in-editor launcher stays as the fallback only when
        // XHub is missing.
        SaveProjectState();
        string? hubExe = Program.LocateHubExe();
        if (hubExe != null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(hubExe)
                {
                    UseShellExecute = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(hubExe)!,
                });
                Runtime.Debug.Log($"[hub] returning to project list via XHub: {hubExe}");
                Game.Quit(); // editor closes; the run loop unwinds and the closing event fires
                return;
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[hub] XHub launch failed: {ex.Message}");
            }
        }

        // Fallback (XHub missing): the legacy in-editor launcher.
        EditorAssetBackend.Instance?.Dispose();
        Runtime.AssetDatabase.Current = null;
        Project.CloseCurrent();

        // Reset state
        _introTime = double.MaxValue;
        _introClosing = false;
        _launcherWasOpen = true;

        // Reopen launcher
        ProjectLauncher.Initialize();

        UpdateEditorTitle();
    }

    // ================================================================
    //  Play Mode
    // ================================================================

    /// <summary>★ Layer 2 (B3-cont): made public so the in-process MCP runtime tool
    /// <c>runtime_playmode</c> can toggle playmode. Preserves the full scene save/restore semantics.</summary>
    private AssetBundles.EditorBundleSimulation? _bundleSimulation;

    public void EnterPlayMode()
    {
        if (Application.IsPlaying) return;

        // Unity's isCompiling behavior: entering play during a script compile would snapshot and
        // serialize a scene that is about to be torn down by the reload — defer with a toast.
        if (Projects.Scripting.ScriptAssemblyManager.IsCompiling)
        {
            Toasts.Show(Loc.Get("progress.compile.playblocked"), Loc.Get("progress.compile.playblocked_msg"), ToastType.Info, 4f);
            return;
        }

        var scene = Runtime.Resources.Scene.Current;
        if (scene == null) return;
        Guid playSceneGuid = scene.AssetID;
        if (playSceneGuid == Guid.Empty && EditorSceneManager.CurrentScenePath is { } sourceScenePath)
        {
            string metaPath = MetaFile.GetMetaPath(System.IO.Path.Combine(Project.Current!.AssetsPath, sourceScenePath));
            if (System.IO.File.Exists(metaPath)) playSceneGuid = MetaFile.Read(metaPath).Guid;
        }

        var playSw = System.Diagnostics.Stopwatch.StartNew();
        if (EditorRegistries.GetSettings<AssetSettings>().EditorSimulateAssetBundles)
        {
            try { _bundleSimulation = AssetBundles.EditorBundleSimulation.Prepare(playSceneGuid); }
            catch (Exception ex)
            {
                Runtime.Debug.LogError("AssetBundle EditorSimulate preparation failed: " + ex.Message);
                return;
            }
        }

        Runtime.Debug.Log($"[PlayInit] prepare done at {playSw.ElapsedMilliseconds}ms");

        // Save active tab (to restore on stop)
        SaveActiveTab();

        // Serialize the current editor scene
        _savedEditorScene = Echo.Serializer.Serialize(scene);
        if (_savedEditorScene == null)
        {
            _bundleSimulation?.Dispose();
            _bundleSimulation = null;
            Runtime.Debug.LogError("Failed to serialize scene for play mode.");
            return;
        }
        Runtime.Debug.Log($"[PlayInit] serialize at {playSw.ElapsedMilliseconds}ms");

        // Clear selection (references will be invalid)
        Selection.Clear();

        // Unload the editor scene
        Runtime.Resources.Scene.Unload();

        // Deserialize a fresh play copy
        var playCtx = Importers.ImportHelper.CreateTrackingContext();
        if (_bundleSimulation != null)
            EditorAssetBackend.Instance?.ReleaseUnpinnedSceneCache();
        _bundleSimulation?.Activate();
        var playScene = _bundleSimulation != null
            ? _bundleSimulation.Backend.LoadScene(playSceneGuid)
            : Echo.Serializer.Deserialize<Runtime.Resources.Scene>(_savedEditorScene, playCtx);
        if (playScene == null)
        {
            _bundleSimulation?.Dispose();
            _bundleSimulation = null;
            var recovery = Echo.Serializer.Deserialize<Runtime.Resources.Scene>(_savedEditorScene, playCtx);
            if (recovery != null) Runtime.Resources.Scene.Load(recovery);
            Runtime.Debug.LogError("Failed to deserialize play scene.");
            return;
        }
        Runtime.Debug.Log($"[PlayInit] deserialize done at {playSw.ElapsedMilliseconds}ms");

        // "Clear on Play" wipes the previous run's logs before any new ones arrive.
        ConsolePanel.OnEnterPlayMode();

        // Set play mode flags BEFORE loading so OnEnable/Start gates pass
        Runtime.Rendering.QualitySettings.ApplyPlayerDefaults();
        Application.IsPlaying = true;
        try { EditorRegistries.GetSettings<GraphicsSettings>().Apply(); } catch { }
        try { EditorRegistries.GetSettings<PlayerSettings>().Apply(); } catch { }
        Application.IsPaused = false;
        Application.StepRequested = false;
        ResetFixedTimeAccumulator();

        // Push fresh play-mode time (game code sees Time.TimeSinceStartup = 0)
        _savedEditorTime = Runtime.Time.CurrentTime;
        Runtime.Time.TimeStack.Clear();
        Runtime.Time.TimeStack.Push(new TimeData());

        // Push play-mode input handler (only forwards input when Game View focused)
        Input.PushHandler(new GameViewInputHandler(Input.Current));

        // Load with full lifecycle (Enable -> OnEnable/Start will fire)
        Runtime.Debug.Log($"[PlayInit] pre-Scene.Load at {playSw.ElapsedMilliseconds}ms");
        if (_bundleSimulation == null)
            EditorAssetBackend.Instance?.PrepareSceneAnimationAssets(playScene);
        Runtime.Resources.Scene.Load(playScene);
        Runtime.Debug.Log($"[PlayInit] Scene.Load done at {playSw.ElapsedMilliseconds}ms");
        Undo.Clear();

        // The serialization above burned real seconds inside this same window tick; drop them so
        // gameplay's first frame runs at a sane DeltaTime instead of teleporting everything.
        ResetDeltaClock();

        // Apply physics settings to the new scene
        try { EditorRegistries.GetSettings<PhysicsSettings>().Apply(); } catch { }

        // Focus the Game View tab, unless the Game view's play-focus setting says to stay put.
        // XENGINE_PLAY_CAPTURE_DIR needs the Game panel drawn every frame — force focus for the
        // capture session regardless of the user's play-focus preference.
        if (GameViewPanel.PlayFocus == PlayFocusMode.Focused || GameViewPanel.CaptureRequested)
            FocusPanel(typeof(GameViewPanel));
        else if (GameViewPanel.PlayFocus == PlayFocusMode.Maximized)
        {
            FocusPanel(typeof(GameViewPanel));
            MaximizePanel(typeof(GameViewPanel));
        }
        EditorAudio.Apply();

        Runtime.Debug.Log("Entered play mode.");
    }

    /// <summary>★ Layer 2 (B3-cont): made public for the <c>runtime_playmode</c> MCP tool.</summary>
    public void ExitPlayMode()
    {
        if (!Application.IsPlaying || _isUnloadingPlayScene) return;

        // Release gameplay resources while lifecycle callbacks are still eligible to run.
        // Clearing IsPlaying first gates OnDisable off and leaks native audio handles.
        // OnDisable may request another stop, so guard this still-playing teardown.
        _isUnloadingPlayScene = true;
        try
        {
            Selection.Clear();
            Runtime.Resources.Scene.Unload();
        }
        finally
        {
            _isUnloadingPlayScene = false;
        }

        // Clear flags before restoring the editor scene.
        Application.IsPlaying = false;
        Application.IsPaused = false;
        Application.StepRequested = false;
        Runtime.Rendering.QualitySettings.ApplyEditorDefaults();
        try { EditorRegistries.GetSettings<GraphicsSettings>().Apply(); } catch { }
        try { EditorRegistries.GetSettings<PlayerSettings>().Apply(); } catch { }

        // Restore the editor scene WITHOUT lifecycle callbacks
        _bundleSimulation?.Dispose();
        _bundleSimulation = null;
        try { EditorRegistries.GetSettings<GraphicsSettings>().Apply(); } catch { }
        if (_savedEditorScene != null)
        {
            var ctx = Importers.ImportHelper.CreateTrackingContext();
            var restoredScene = Echo.Serializer.Deserialize<Runtime.Resources.Scene>(_savedEditorScene, ctx);
            if (restoredScene != null)
                EditorAssetBackend.Instance?.PrepareSceneAnimationAssets(restoredScene);
                Runtime.Resources.Scene.Load(restoredScene);
            Undo.Clear();
            _savedEditorScene = null;
        }

        // Pop play-mode input handler
        if (Input.Current is GameViewInputHandler)
            Input.PopHandler();

        // Restore editor time
        if (_savedEditorTime != null)
        {
            Runtime.Time.TimeStack.Clear();
            Runtime.Time.TimeStack.Push(_savedEditorTime);
            _savedEditorTime = null;
        }

        // Same giant-delta guard on the way out: play session seconds must not land in the next
        // edit-mode frame either.
        ResetDeltaClock();

        // Undo a Maximize-on-Play layout swap before tab restoration touches the tree.
        RestoreMaximizedPanel();

        // Restore the previously active tab
        RestoreActiveTab();

        // Edit-mode audio follows the Scene view's speaker toggle again.
        EditorAudio.Apply();

        Runtime.Debug.Log("Exited play mode.");
    }

    private void TogglePause()
    {
        if (!Application.IsPlaying) return;
        Application.IsPaused = !Application.IsPaused;
    }

    private void StepOneFrame()
    {
        if (!Application.IsPlaying) return;
        Application.IsPaused = true;
        Application.StepRequested = true;
    }

    // ================================================================
    //  Tab Focus Helpers
    // ================================================================

    private void SaveActiveTab()
    {
        for (int i = 0; i < _workspace.Hosts.Count && _savedActiveTabNode == null; i++)
        {
            DockSpace dockSpace = _workspace.Hosts[i].DockSpace;
            _savedActiveTabNode = FindNodeContainingPanel(dockSpace.Root, typeof(GameViewPanel));
            for (int j = 0; j < dockSpace.FloatingWindows.Count && _savedActiveTabNode == null; j++)
            {
                _savedActiveTabNode = FindNodeContainingPanel(
                    dockSpace.FloatingWindows[j].Node, typeof(GameViewPanel));
            }
        }
        _savedActiveTabIndex = _savedActiveTabNode?.ActiveTabIndex ?? -1;
    }

    private void RestoreActiveTab()
    {
        if (_savedActiveTabNode?.Tabs != null && _savedActiveTabIndex >= 0
            && _savedActiveTabIndex < _savedActiveTabNode.Tabs.Count)
        {
            _savedActiveTabNode.ActiveTabIndex = _savedActiveTabIndex;
        }
        _savedActiveTabNode = null;
        _savedActiveTabIndex = -1;
    }

    private void FocusPanel(Type panelType)
    {
        for (int i = 0; i < _workspace.Hosts.Count; i++)
        {
            EditorSurfaceHost host = _workspace.Hosts[i];
            DockNode? node = FindNodeContainingPanel(host.DockSpace.Root, panelType);
            if (node == null)
            {
                for (int j = 0; j < host.DockSpace.FloatingWindows.Count && node == null; j++)
                {
                    node = FindNodeContainingPanel(host.DockSpace.FloatingWindows[j].Node, panelType);
                }
            }

            if (node?.Tabs == null) continue;
            for (int j = 0; j < node.Tabs.Count; j++)
            {
                if (node.Tabs[j].GetType() == panelType)
                {
                    node.ActiveTabIndex = j;
                    _workspace.BringToFront(host.Id);
                    return;
                }
            }
        }
    }

    private static DockNode? FindNodeContainingPanel(DockNode? node, Type panelType)
    {
        if (node == null) return null;
        if (node.IsLeaf && node.Tabs != null)
        {
            foreach (var tab in node.Tabs)
                if (tab.GetType() == panelType)
                    return node;
            return null;
        }
        return FindNodeContainingPanel(node.ChildA, panelType)
            ?? FindNodeContainingPanel(node.ChildB, panelType);
    }

    // ================================================================
    //  Scene Control Editor overrides Game's default scene lifecycle
    // ================================================================

    /// <summary>
    /// Editor does NOT auto-update the scene. SceneView handles it.
    /// </summary>
    public override void OnUpdate(Runtime.Resources.Scene? scene)
    {
        EditorProfiler.BeginCpuFrame();
        using var profilerScope = EditorProfiler.BeginCpuScope("Frame");

        if (!_startupTraceLogged && Window.PresentCount > 0)
        {
            _startupTraceLogged = true;
            StartupTrace.LogSummaryOnce();
        }

        // ★ Layer 2 (B3-cont): execute any MCP tool handlers queued onto the main thread this frame.
        // Runs first so tool-driven mutations are visible to the scene update below.
        AiServer?.Dispatcher.Drain();

        // Dispatcher handlers can enter/exit Play Mode, which unloads and replaces the scene that
        // Game.SimulationStep captured at the beginning of this frame. Refresh before touching it
        // so a queued scene transition cannot update a disposed scene.
        scene = Runtime.Resources.Scene.Current;

        // Always update lifecycle gating is per-component via ShouldExecuteGameplay.
        // Components only run Start/Update/LateUpdate if IsPlaying or [ExecuteAlways].
        if (Application.ShouldRunGameplay)
            Application.IsGameplayExecuting = true;

        if (scene is { IsDisposed: false })
            scene.Update();

        Application.IsGameplayExecuting = false;

        // Import progress modal repaints: RenderFrame renders modal overlays too, so pumping the
        // Open-steps advance in the UPDATE phase: the import pump's nested RenderOnce frame must
        // never run GPU work while a step is mid-flight (double frame-fence signal → 5s
        // vkWaitForFences timeout → device lost), so the launcher tick moved out of BeginGui.
        // A --project launch runs the same step machine without the launcher (StartupProgressScreen).
        if (ProjectLauncher.IsOpen || ProjectOpenProgress.IsActive)
            TickLauncherProjectOpen();

        if (_titleRefreshClock >= 1f)
        {
            _titleRefreshClock = 0f;
            UpdateEditorTitle();
        }

        TickEditorAssetWork();
        GUI.ProgressDemoMenu.Tick(Time.UnscaledDeltaTime);
        GUI.ProgressDemoMenu.TryStartFromEnv();
        Inspector.MaterialEditorGUI.Tick();
        TryApplyStartupSelection();
        TryApplyStartupVisibility();
        TryRunStartupMenu();
        TryAutoPanelScreenshot();
    }

    private bool _startupMenuDone;
    private float _startupMenuReadyAt = -1f;

    /// <summary>--run-menu: invoke an editor menu item once the project is ready, then quit.
    /// Used headlessly to run builder menus (e.g. Zonezero/Rebuild Battle HUD Prefab).</summary>
    private void TryRunStartupMenu()
    {
        if (_startupMenuDone || string.IsNullOrEmpty(Program.RunMenuPath))
            return;
        if (Project.Current == null || EditorAssetBackend.Instance == null || Runtime.Resources.Scene.Current == null)
            return;
        // Project editor menus live in script assemblies. Firing before the first compile
        // finishes reports "not found" and quits (Icey/Assets/CollectBundles, etc.).
        if (Projects.Scripting.ScriptAssemblyManager.IsCompiling
            || Projects.Scripting.ScriptAssemblyManager.IsStartupLoadInProgress)
            return;
        if (_startupMenuReadyAt < 0f)
        {
            _startupMenuReadyAt = Time.UnscaledTotalTime;
            return;
        }
        if (Time.UnscaledTotalTime - _startupMenuReadyAt < 3f)
            return; // let imports settle

        _startupMenuDone = true;
        var item = FindMenuItemByPath(Program.RunMenuPath);
        bool ok = false;
        if (item is { IsEnabled: true } && item.OnClick != null)
        {
            item.OnClick();
            ok = true;
        }
        Runtime.Debug.Log($"[--run-menu] '{Program.RunMenuPath}' → {(ok ? "ok" : "not found / disabled")}");
        Environment.ExitCode = ok ? 0 : 1;
        Game.Quit();
    }

    private static AppMenuItem? FindMenuItemByPath(string path)
    {
        string[] segments = path.Split('/');
        var current = MenuRegistry.RootMenus;
        AppMenuItem? found = null;
        foreach (string seg in segments)
        {
            found = null;
            foreach (var item in current)
            {
                if (item.Label == seg && !item.IsSeparator) { found = item; break; }
            }
            if (found is null) return null;
            current = found.SubItems;
        }
        return found;
    }

    /// <summary>
    /// Watcher import / script compile / thumbnails used to run only in <see cref="BeginGui"/>,
    /// so an idle skip-present loop stalled the asset pipeline. OnUpdate now ticks at ~60 Hz
    /// even when the surface is retained.
    /// </summary>
    private void TickEditorAssetWork()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool canProcessAssets = !EditorSettings.Instance.ReimportOnFocusOnly || IsWindowGroupActive;
        if (canProcessAssets)
        {
            EditorAssetBackend.Instance?.ProcessFileChanges();
            // Apply completed background imports (time-sliced; no-op when nothing pending).
            EditorAssetBackend.Instance?.ApplyCompletedImports(Theming.EditorSettings.Instance.AssetWorkSliceBudgetMs);
            ScriptAssemblyManager.Update();
            ThumbnailGenerator.ProcessOne();
        }

        EditorAssetBackend.Instance?.TickIdleSweep();

        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // Startup grace: the first seconds legitimately batch initial watcher imports, script
        // compiles and thumbnail work, so a slow tick there is expected - only steady-state
        // stalls are worth surfacing.
        if (ms > 50 && Time.UnscaledTotalTime > 10 && Time.UnscaledTotalTime - _lastAssetTickWarnUnscaled > 2)
        {
            _lastAssetTickWarnUnscaled = Time.UnscaledTotalTime;
            Runtime.Debug.LogWarning($"[Editor] Asset tick took {ms:F0}ms (watcher/import/compile).");
        }
    }

    private void TryApplyStartupSelection()
    {
        if (_startupSelectionApplied) return;

        if (!string.IsNullOrEmpty(Program.SelectObjectName))
        {
            var scene = Runtime.Resources.Scene.Current;
            if (scene == null) return;
            foreach (var go in scene.AllObjects)
            {
                if (go.Name != Program.SelectObjectName) continue;
                _startupSelectionApplied = true;
                Selection.Select(go);
                InvalidateSurface(EditorSurfaceDirtyReason.Selection);
                return;
            }
            return;
        }

        if (string.IsNullOrEmpty(Program.SelectAssetPath))
            return;
        var db = EditorAssetBackend.Instance;
        if (db == null) return;

        AssetEntry? entry = db.GetEntry(Program.SelectAssetPath);
        if (entry == null && Guid.TryParse(Program.SelectAssetPath, out Guid guid))
            entry = db.GetEntry(guid);
        if (entry == null)
        {
            string folder = Program.SelectAssetPath.Replace('\\', '/').TrimEnd('/');
            if (GUI.Panels.ProjectPanel.IsPackagesTreePath(folder)
                || folder.Length == 0
                || Directory.Exists(Path.Combine(Project.Current!.AssetsPath, folder)))
            {
                if (FindOpenPanel(typeof(ProjectPanel)) is not ProjectPanel projectPanel)
                    return;
                _startupSelectionApplied = true;
                projectPanel.NavigateTo(folder);
                InvalidateSurface(EditorSurfaceDirtyReason.Selection);
            }
            return;
        }

        _startupSelectionApplied = true;
        Selection.Select(new ContentItem
        {
            Guid = entry.Guid,
            RelativePath = entry.Path,
            Name = System.IO.Path.GetFileName(entry.Path),
        });
        if (!string.IsNullOrEmpty(Program.SelectAssetTab)
            && Enum.TryParse<XEngine.Editor.AnimationEditor.ModelImportTab>(Program.SelectAssetTab, ignoreCase: true, out var startupTab))
        {
            XEngine.Editor.Inspector.ModelAssetEditor.StartupTab = startupTab;
        }
        InvalidateSurface(EditorSurfaceDirtyReason.Selection);
    }

    private void TryApplyStartupVisibility()
    {
        if (_startupVisibilityApplied) return;
        if (string.IsNullOrEmpty(Program.HideObjectName) && string.IsNullOrEmpty(Program.UnpickableObjectName))
        {
            _startupVisibilityApplied = true;
            return;
        }

        var scene = Runtime.Resources.Scene.Current;
        if (scene == null) return;

        SceneVisibilityManager.Install();
        bool hideDone = string.IsNullOrEmpty(Program.HideObjectName);
        bool pickDone = string.IsNullOrEmpty(Program.UnpickableObjectName);
        foreach (var go in scene.AllObjects)
        {
            if (!hideDone && go.Name == Program.HideObjectName)
            {
                SceneVisibilityManager.SetHidden(go, true);
                hideDone = true;
            }
            if (!pickDone && go.Name == Program.UnpickableObjectName)
            {
                SceneVisibilityManager.SetPickingEnabled(go, false);
                pickDone = true;
            }
        }

        if (!hideDone || !pickDone)
        {
            if (Time.UnscaledTotalTime < 8f) return;
        }

        _startupVisibilityApplied = true;
        SceneViewPanel.ActiveInstance?.InvalidateRender();
        InvalidateSurface(EditorSurfaceDirtyReason.ExplicitRepaint);
    }

    private void TryAutoPanelScreenshot()
    {
        // --play-on-load alone (no --panel-screenshot): enter play once the scene is ready and
        // stay running (used with XENGINE_PLAY_CAPTURE_DIR for live-session frame capture).
        if (Program.PlayOnLoad && string.IsNullOrEmpty(Program.PanelScreenshotType))
        {
            if (Project.Current != null && EditorAssetBackend.Instance != null &&
                Runtime.Resources.Scene.Current != null && _startupVisibilityApplied &&
                !_playOnLoadTriggered && !Application.IsPlaying)
            {
                _playOnLoadTriggered = true;
                EnterPlayMode();
            }
            return;
        }

        if (_autoPanelScreenshotDone || string.IsNullOrEmpty(Program.PanelScreenshotType))
            return;
        if (Project.Current == null || EditorAssetBackend.Instance == null)
            return;
        if (Runtime.Resources.Scene.Current == null)
            return;
        if (!_startupVisibilityApplied)
            return;

        // --play-on-load: enter play mode before the capture window so the shot shows the
        // ANIMATED state (skinning/animation acceptance), then quit as usual.
        if (Program.PlayOnLoad && !_playOnLoadTriggered && !Application.IsPlaying)
        {
            _playOnLoadTriggered = true;
            EnterPlayMode();
            _autoPanelScreenshotReadyAt = -1f; // restart the settle timer after play begins
            return;
        }

        if (_autoPanelScreenshotReadyAt < 0f)
        {
            _autoPanelScreenshotReadyAt = Time.UnscaledTotalTime;
            // Open the target panel now so its async loads (package catalog refresh, etc.) settle
            // within the capture window instead of racing the offscreen render.
            EditorRuntimeTools.EnsurePanelOpenForCapture(Program.PanelScreenshotType);
            InvalidateSurface(EditorSurfaceDirtyReason.ExplicitRepaint);
            return;
        }
        float settleSeconds = 4f;
        if (float.TryParse(Environment.GetEnvironmentVariable("XENGINE_SCREENSHOT_DELAY"), out float parsed))
            settleSeconds = parsed;
        if (Time.UnscaledTotalTime - _autoPanelScreenshotReadyAt < settleSeconds)
        {
            InvalidateSurface(EditorSurfaceDirtyReason.ExplicitRepaint);
            return;
        }

        _autoPanelScreenshotDone = true;
        int captureW = 420;
        int captureH = 720;
        if (Program.PanelScreenshotType.Contains("InspectorPanel", StringComparison.Ordinal))
        {
            captureW = 480;
            captureH = 1600;
        }
        else if (Program.PanelScreenshotType.Contains("ProjectPanel", StringComparison.Ordinal))
        {
            captureW = 1200;
            captureH = 720;
        }
        else if (Program.PanelScreenshotType.Contains("SceneViewPanel", StringComparison.Ordinal))
        {
            captureW = 900;
            captureH = 600;
        }
        else if (Program.PanelScreenshotType.Contains("ProjectSettingsPanel", StringComparison.Ordinal))
        {
            captureW = 760;
            captureH = 720;
        }
        else if (Program.PanelScreenshotType.Contains("PackageManagerPanel", StringComparison.Ordinal))
        {
            // Three-pane catalog browser (rail + list + details) needs the full workspace width;
            // the async remote refresh also settles within the capture window.
            captureW = 1180;
            captureH = 760;
        }
        else if (Program.PanelScreenshotType.Contains("BundleCollectorPanel", StringComparison.Ordinal))
        {
            // YooAsset-style collector editor: toolbar + settings + Groups|Collectors two-column body.
            captureW = 1180;
            captureH = 760;
        }
        var result = EditorRuntimeTools.CapturePanelNow(
            Program.PanelScreenshotType, captureW, captureH, Program.PanelScreenshotOut);
        if (!string.IsNullOrEmpty(result.Error))
        {
            Runtime.Debug.LogError(result.Error);
            Environment.ExitCode = 1;
        }
        else
        {
            Runtime.Debug.Log($"Panel screenshot: {result.Path}");
        }

        // Whole-window capture channel (chrome: status bar, modals, corner cards, startup screen).
        // The panel capture above renders one DockPanel offscreen; XENGINE_WINDOW_SHOT_OUT additionally
        // BitBlts the live editor window before the quit, for editor-chrome acceptance evidence.
        string? windowShotPath = Environment.GetEnvironmentVariable("XENGINE_WINDOW_SHOT_OUT");
        if (!string.IsNullOrEmpty(windowShotPath))
        {
            string? windowShot = EditorWindowCapture.Capture(windowShotPath);
            Runtime.Debug.Log(windowShot != null
                ? $"Window screenshot: {windowShot}"
                : "Window screenshot FAILED");
        }
        Game.Quit();
    }

    /// <summary>
    /// Record this frame's component gizmos into <see cref="Runtime.Debug"/> so the viewport render
    /// (later in the frame) can draw them. <see cref="Runtime.Debug.ClearGizmos"/> empties the builder
    /// at the top of every frame, so anything that renders a viewport outside the normal
    /// Update-then-Render order has to record first or it draws a scene with no gizmos.
    /// </summary>
    internal void RecordEditorGizmos(Runtime.Resources.Scene? scene)
    {
        // Editor gizmos are a scene-view concept, and the scene view edits UI in world space, so queue
        // them with the world-space canvas override active - otherwise the canvas wireframe would be
        // laid out at the screen/RT size and disagree with the world-space UI it's drawn around.
        bool prevWorldSpace = GameCanvas.EditorWorldSpaceOverride;
        GameCanvas.EditorWorldSpaceOverride = true;
        try
        {
            // Always allow gizmos in editor (even when not playing)
            if (scene != null)
            {
                scene.DrawGizmos();
                if (PhysicsDebugger.AnyOverlayEnabled)
                    PhysicsDebugger.Draw(scene.Physics);
            }

            if (Selection.Count > 0)
            {
                Selection.GetSelected(s_gizmoSelected);
                for (int i = 0; i < s_gizmoSelected.Count; i++)
                {
                    var go = s_gizmoSelected[i];
                    go.GetComponents(s_gizmoComponents);
                    for (int c = 0; c < s_gizmoComponents.Count; c++)
                    {
                        var comp = s_gizmoComponents[c];
                        if (!SceneVisibilityManager.IsVisibleInHierarchy(comp.GameObject)) continue;
                        if (Runtime.Debug.GizmoFilter is { } gizmoFilter && !gizmoFilter(comp)) continue;
                        Runtime.Debug.BeginGizmoObject(comp);
                        try { comp.DrawGizmosSelected(); }
                        finally { Runtime.Debug.EndGizmoObject(); }
                    }
                }
            }
        }
        finally
        {
            GameCanvas.EditorWorldSpaceOverride = prevWorldSpace;
        }
    }

    /// <summary>Queues one repaint for an editor-owned UI or model mutation.</summary>
    public void InvalidateSurface(EditorSurfaceDirtyReason reason)
    {
        _frameScheduler.Invalidate(reason);
        // Detached hosts have their own input bridges. Main-window mouse movement must
        // not rebuild every idle window. Model/layout mutations still reach all hosts.
        var sharedReasons = reason & ~EditorSurfaceDirtyReason.Input;
        if (sharedReasons != EditorSurfaceDirtyReason.None)
            _nativeSurfaceManager?.InvalidateSecondary(sharedReasons);
    }

    private void NotifyMainWindowInteraction()
    {
        _frameScheduler.Invalidate(EditorSurfaceDirtyReason.Window);
        _frameScheduler.RegisterAnimation(Time.UnscaledTotalTime + 0.2);
    }

    protected override bool ShouldRenderFrame()
    {
        bool isMinimized = Window.InternalWindow.WindowState == Silk.NET.Windowing.WindowState.Minimized;
        if (isMinimized)
        {
            _frameScheduler.SetFocused(false);
            SetIdleTickRate(busy: false);
            return false;
        }

        // On-demand rendering disabled (default): classic blanket render — every frame presents,
        // so live content changes (Inspector transform edits, tooling writes) show immediately.
        if (!EditorSettings.Instance.OnDemandRendering)
        {
            SetIdleTickRate(busy: true);
            return true;
        }

        // Keep pumping frames while the launcher is running a blocking project-open step so the
        // progress bar can advance even if the window is unfocused.
        if (ProjectOpenProgress.IsActive)
        {
            SetIdleTickRate(busy: true);
            return true;
        }

        // Progress tasks animate (spinner / corner card) even while the rest of the editor is
        // idle-retained, so a visible task keeps frames flowing.
        if (Core.Progress.EditorProgress.HasVisibleWork)
        {
            SetIdleTickRate(busy: true);
            return true;
        }

        // Frame Debugger active: render every frame (Unity behavior). The armed capture needs a
        // next BeginFrame to fire, and stepping the selected event replays a truncated frame that
        // must keep re-rendering — idle on-demand rendering would otherwise stall both forever.
        if (Runtime.Rendering.FrameDebugger.IsEnabled)
        {
            SetIdleTickRate(busy: true);
            return true;
        }

        // Play Mode with gameplay advancing presents every frame (Unity behavior): simulation
        // output must reach the screen even with zero editor input — the dirty-reason scheduler
        // has no per-frame invalidation source while playing, which would otherwise cap the Game
        // View at whatever incidental invalidations occur (~20 Hz observed vs a ~70 Hz sim tick).
        // Paused play restores invalidation-driven scheduling; Step raises StepRequested for its
        // single frame and renders it through this same gate.
        if (Application.ShouldRunGameplay)
        {
            SetIdleTickRate(busy: true);
            return true;
        }

        bool menuOpen = Modal.IsOpen || ContextMenu.IsOpen || MenuBarBuilder.HasOpenDropdown;
        bool pointerOverEditor = _nativeSurfaceManager?.IsPointerOverAnyEditorWindow ?? Window.IsFocused;
        bool keepUiLive = EditorUiRenderGate.ShouldKeepUiLive(
            menuOpen, IsWindowGroupActive, pointerOverEditor);

        // Focused / hovered must keep the Paper pass *available*, but not necessarily rendering.
        // The historical blanket force-render existed because the scheduler going Idle left menu
        // catchers on a dead framebuffer. Input/selection/asset/theme invalidations now queue
        // repaints through the scheduler, so a focused editor with nothing dirty presents nothing
        // (Unity-style idle). Menus/modals/dropdowns still force every frame while open.
        if (keepUiLive)
        {
            _frameScheduler.SetFocused(true);
            UpdateInteractiveScheduling();

            double liveNow = Time.UnscaledTotalTime;
            if (_introClosing || _introTime < IntroDuration)
                _frameScheduler.RegisterAnimation(liveNow + 0.1);

            _lastLiveMouse = new Int2((int)ReadLiveMouse().X, (int)ReadLiveMouse().Y);

            if (menuOpen)
            {
                SetIdleTickRate(busy: true);
                return true;
            }

            bool renderLive = _frameScheduler.ShouldRender(liveNow);
            SetIdleTickRate(busy: renderLive);
            return renderLive;
        }

        _frameScheduler.SetFocused(false);
        UpdateInteractiveScheduling();

        double now = Time.UnscaledTotalTime;
        if (_introClosing || _introTime < IntroDuration)
            _frameScheduler.RegisterAnimation(now + 0.1);

        bool render = _frameScheduler.ShouldRender(now);
        if (render)
            _lastLiveMouse = new Int2((int)ReadLiveMouse().X, (int)ReadLiveMouse().Y);
        SetIdleTickRate(busy: render);
        return render;
    }

    // P1-1 idle throttle: when nothing renders and no simulation runs, drop the skip-present
    // tick rate from 60 Hz to 15 Hz. Input still wakes instantly (the idle pump waits on the
    // window message queue); watcher/MCP polling tolerates ≤66 ms latency.
    private const double EditorIdleLoopHz = 15.0;

    private void SetIdleTickRate(bool busy)
    {
        double target = busy || Application.IsPlaying || Application.ShouldRunGameplay
            ? Window.IdleLoopHz
            : EditorIdleLoopHz;
        if (Window.IdleLoopTargetHz != target)
            Window.IdleLoopTargetHz = target;
    }

    private void UpdateInteractiveScheduling()
    {
        // A click's release reaches Paper during Update — one loop iteration AFTER the mouse
        // event already invalidated — so the frame that actually carries the edge needs its own
        // repaint here, or the click hangs until an unrelated redraw (usually the next mouse
        // move). ShouldRenderFrame runs after Update, so this is the same-iteration gate.
        // NeedsFollowUpFrame is the next hop: that consumed-click frame already laid out with
        // the old widget state, so tree expand/collapse must paint once more (plus a short
        // animation window) without waiting for a mouse move.
        if (EditorUiRenderGate.ShouldInvalidateForPaperInput(
                PaperInstance.HasPendingButtonEdge, PaperInstance.NeedsFollowUpFrame))
            InvalidateSurface(EditorSurfaceDirtyReason.Input);
        if (EditorUiRenderGate.ShouldRegisterClickFollowUpAnimation(PaperInstance.NeedsFollowUpFrame))
            _frameScheduler.RegisterAnimation(
                Time.UnscaledTotalTime + EditorUiRenderGate.PaperClickFollowUpSeconds);

        if (Input.InputString.Length != 0 || Input.MouseWheelDelta != 0f)
            InvalidateSurface(EditorSurfaceDirtyReason.Input);

        Float2 live = ReadLiveMouse();
        var livei = new Int2((int)live.X, (int)live.Y);
        bool mouseMoved = livei != _lastLiveMouse;
        if (mouseMoved)
            InvalidateSurface(EditorSurfaceDirtyReason.Input);

        bool interactive = mouseMoved || Input.CursorLocked || Input.AnyKey
            || Input.GetMouseButton(0)
            || Input.GetMouseButton(1)
            || Input.GetMouseButton(2);
        if (interactive)
            _frameScheduler.RequestInteractiveFrame();
        else
            _frameScheduler.ReleaseInteractiveFrame();
    }

    private void OnEditorSurfaceFocusChanged(bool focused)
    {
        _nativeSurfaceManager?.SetMainWindowFocused(focused);
        _frameScheduler.SetFocused(IsWindowGroupActive);
        if (!focused)
        {
            // The primary Paper belongs to Game, not to the workspace host.
            PaperInstance?.CancelDragEdit();
            Input.ClearLockContexts();
        }
        if (focused)
            ActivateBackgroundAnimation();
    }

    private void OnEditorKeyChanged(KeyCode key, bool pressed)
    {
        InvalidateSurface(EditorSurfaceDirtyReason.Input);
    }

    private void OnEditorMouseChanged(MouseButton button, float x, float y, bool held, bool moved)
    {
        InvalidateSurface(EditorSurfaceDirtyReason.Input);
    }

    private void ActivateBackgroundAnimation()
    {
        if (EditorTheme.AnimatedBackground && IsWindowGroupActive)
        {
            _frameScheduler.RegisterAnimation(
                Time.UnscaledTotalTime + GUI.EditorBackgroundAnimationPolicy.DefaultIdleTimeoutSeconds);
        }
    }

    /// <summary>
    /// Editor does NOT auto-render the scene. SceneView handles it via EditorCamera.
    /// </summary>
    public override void OnRender(Runtime.Resources.Scene? scene)
    {
        // Don't render SceneView panel renders the editor camera to its own RT.
    }

    /// <summary>
    /// Editor does NOT call scene OnGui. Paper UI is driven by editor panels.
    /// </summary>
    public override void OnGui(Runtime.Resources.Scene? scene, PaperUI.Paper paper)
    {
        // Don't call scene.OnGui editor controls all UI.
    }

    public override void Closing()
    {
        PaperInstance?.CancelDragEdit();
        try { _nativeSurfaceManager?.Dispose(); }
        finally
        {
            _nativeSurfaceManager = null;
            _shellPaint.Dispose();
            PaperInstance?.Canvas.Dispose();
        }
    }

    // ================================================================
    //  Default Layout
    // ================================================================

    internal static DockNode CreateDefaultLayout()
    {
        // Authoring workspace: Hierarchy beside Scene/Game, Project/Console below,
        // and a full-height Inspector. Saved user layouts take precedence on load.
        return DockNode.Split(SplitDirection.Horizontal, 0.76f,
            DockNode.Split(SplitDirection.Vertical, 0.59f,
                DockNode.Split(SplitDirection.Horizontal, 0.25f,
                    DockNode.Leaf(new HierarchyPanel()),
                    DockNode.Leaf(new SceneViewPanel(), new GameViewPanel())),
                DockNode.Leaf(new ProjectPanel(), new ConsolePanel())),
            DockNode.Leaf(new InspectorPanel()));
    }

    private static DockNode CreateWideLayout()
    {
        return DockNode.Split(SplitDirection.Horizontal, 0.78f,
            DockNode.Split(SplitDirection.Vertical, 0.76f,
                DockNode.Leaf(new SceneViewPanel(), new GameViewPanel()),
                DockNode.Split(SplitDirection.Horizontal, 0.58f,
                    DockNode.Leaf(new ProjectPanel()),
                    DockNode.Leaf(new ConsolePanel()))),
            DockNode.Split(SplitDirection.Vertical, 0.42f,
                DockNode.Leaf(new HierarchyPanel()),
                DockNode.Leaf(new InspectorPanel())));
    }

    private static DockNode CreateTwoByThreeLayout()
    {
        return DockNode.Split(SplitDirection.Horizontal, 1f / 3f,
            DockNode.Split(SplitDirection.Vertical, 0.5f,
                DockNode.Leaf(new HierarchyPanel()),
                DockNode.Leaf(new ProjectPanel())),
            DockNode.Split(SplitDirection.Horizontal, 0.5f,
                DockNode.Split(SplitDirection.Vertical, 0.5f,
                    DockNode.Leaf(new SceneViewPanel()),
                    DockNode.Leaf(new GameViewPanel())),
                DockNode.Split(SplitDirection.Vertical, 0.5f,
                    DockNode.Leaf(new InspectorPanel()),
                    DockNode.Leaf(new ConsolePanel()))));
    }
}
