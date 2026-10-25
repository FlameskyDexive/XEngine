using XEngine.Editor.Core;
using XEngine.Editor.Build;
using XEngine.Editor.Build.Android;
using XEngine.Editor.Build.HarmonyOS;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;


namespace XEngine.Editor;


public static class Program
{
    /// <summary>If set via --project arg, the editor opens this project directly (skips launcher).</summary>
    public static string? StartupProjectPath { get; private set; }
    private static int RestartParentProcessId { get; set; }

    /// <summary>XHub session id (§4.3): enables NDJSON lifecycle events for the XHub watcher.</summary>
    public static string? HubSessionId { get; private set; }

    /// <summary>If set via --restore-scene arg, this scene is loaded instead of the last saved scene.</summary>
    public static string? RestoreScenePath { get; private set; }

    /// <summary>If set via --buildmode arg, the editor runs a build directly and doesn't start any UI.</summary>
    public static bool BuildMode { get; private set; } = false;

    /// <summary>★ Gen-P3.1 — Headless scan+import of dirty/new assets into Library/cache, then exit.</summary>
    public static bool RefreshAssetsMode { get; private set; }

    /// <summary>When set, starts the in-process MCP server for live editor inspection.</summary>
    public static bool Serve { get; private set; }

    /// <summary>Successful Metal toolchain probe details, logged after the editor log starts.</summary>
    internal static string? MetalToolchainDiagnostic { get; private set; }

    /// <summary>When set (--hub-info), print the hub handshake JSON and exit.</summary>
    public static bool HubInfo { get; private set; }

    /// <summary>When set (--hub-command), execute one stdin JSON request and exit.</summary>
    public static bool HubCommand { get; private set; }

    /// <summary>Deep link: open the launcher Settings modal once the launcher is up.</summary>
    public static bool LauncherSettingsOpen { get; private set; }

    /// <summary>Deep link: Settings page to open (platforms|general); null = default.</summary>
    public static string? LauncherSettingsPage { get; private set; }

    /// <summary>Deep link: module name to auto-install after the Settings modal opens
    /// (pairs with <see cref="LauncherSettingsOpen"/>).</summary>
    public static string? InstallModuleName { get; private set; }

    /// <summary>When set, capture this DockPanel offscreen after the project is ready, then quit.</summary>
    public static string? PanelScreenshotType { get; private set; }

    /// <summary>When true, enters play mode once the project/scene is ready (see --play-on-load).</summary>
    public static bool PlayOnLoad { get; private set; }

    /// <summary>Menu path to invoke once the project is ready (see --run-menu).</summary>
    public static string? RunMenuPath { get; private set; }

    /// <summary>Optional output PNG path for <see cref="PanelScreenshotType"/>.</summary>
    public static string? PanelScreenshotOut { get; private set; }

    /// <summary>When set, select this asset (project-relative path or GUID) after the project is ready.</summary>
    public static string? SelectAssetPath { get; private set; }

    /// <summary>When set, open this tab (Model|Rig|Animation|Materials) of the selected asset's
    /// import-settings inspector on first draw (pairs with --select-asset, e.g. Animation-tab captures).</summary>
    public static string? SelectAssetTab { get; private set; }

    /// <summary>When set, select the first scene GameObject with this name after the project is ready.</summary>
    public static string? SelectObjectName { get; private set; }

    /// <summary>When set, hide this GameObject from Scene View (editor visibility, not Enabled).</summary>
    public static string? HideObjectName { get; private set; }

    /// <summary>When set, mark this GameObject unpickable in Scene View.</summary>
    public static string? UnpickableObjectName { get; private set; }

    /// <summary>When set, seed the Hierarchy search box (supports <c>t:Type</c>).</summary>
    public static string? HierarchySearch { get; private set; }
    public static bool IncrementalHierarchy { get; private set; }
    public static bool HierarchyPaintReference { get; private set; }
    public static bool HierarchyPaintMatchAtlas { get; private set; }
    public static bool HierarchyPaintSharedAtlas { get; private set; }
    public static bool HierarchyPaintIncremental { get; private set; }
    /// <summary>Project panel: folder tree and content area painted through visual-state compared
    /// subtree regions, versioned thumbnail draws, virtualized list rows and banded grid rows.</summary>
    public static bool ProjectPaintIncremental { get; private set; }
    /// <summary>Console panel: message list and detail pane painted through visual-state compared
    /// subtree regions; the row painter declares an exact dependency key instead of an opaque draw.</summary>
    public static bool ConsolePaintIncremental { get; private set; }
    /// <summary>Inspector panel: the property scroll body is painted through a visual-state compared
    /// subtree region. The preview bar stays on the host (it samples a live scene texture).</summary>
    public static bool InspectorPaintIncremental { get; private set; }
    /// <summary>Main-window menu / toolbar / status bar painted through compared subtree regions,
    /// and secondary hosts skip invalidations for panels they do not show. Default off: chrome
    /// rebuilds every presented frame and every floater still receives the shared dirty reason.</summary>
    public static bool ShellPaintIncremental { get; private set; }
    /// <summary>Profiler panel: charts and the details scroll are painted through compared subtree
    /// regions; the toolbar stays on the host. A visible live Profiler also receives Animation so a
    /// detached host keeps presenting while other floaters stay idle.</summary>
    public static bool ProfilerPaintIncremental { get; private set; }
    /// <summary>Scene view: the strip above the 3D viewport is a compared subtree; the viewport
    /// and overlays stay on the host.</summary>
    public static bool ScenePaintIncremental { get; private set; }
    /// <summary>Game view: the strip above the game image is a compared subtree; the viewport
    /// stays on the host.</summary>
    public static bool GamePaintIncremental { get; private set; }
    /// <summary>Animation clip window: property list, ruler/events and dopesheet/curves are
    /// compared subtree regions with a visible-time key query. The toolbar stays on the host.
    /// A visible preview/recording session also receives Animation so a detached host keeps
    /// presenting.</summary>
    public static bool AnimationPaintIncremental { get; private set; }
    /// <summary>Timeline window: track headers and ruler/sheet are compared subtree regions.
    /// The toolbar and clip inspector stay on the host. A visible playing director also
    /// receives Animation so a detached host keeps presenting.</summary>
    public static bool TimelinePaintIncremental { get; private set; }
    /// <summary>Animator window: layers/parameters list and the graph canvas are compared
    /// subtree regions. Toolbar, tabs and inspector stay on the host. A visible play-link
    /// also receives Animation so a detached host keeps presenting.</summary>
    public static bool AnimatorPaintIncremental { get; private set; }

    /// <summary>When paired with <c>--buildmode</c>, deploys the completed Android package.</summary>
    public static bool BuildAndRun { get; private set; } = false;

    /// <summary>If set via --output arg, it builds the game into this path when built through the --build arg.</summary>
    public static string? BuildOutputPath { get; private set; }

    public static string? BuildPipelineId { get; private set; }

    /// <summary>Name, project relative path or asset GUID of the build profile to build, if any.</summary>
    public static string? BuildProfileToken { get; private set; }

    public static string? AndroidToolchainImportSource { get; private set; }

    public static string? AndroidToolchainImportDestination { get; private set; }

    public static string? AndroidToolchainResolveLockPath { get; private set; }

    public static string? AndroidToolchainVerifyLockPath { get; private set; }

    public static string? AndroidToolchainImportedRoot { get; private set; }

    public static string? AndroidDotNetRoot { get; private set; }

    public static string? AndroidJavaSdkRoot { get; private set; }

    public static string? AndroidSdkRoot { get; private set; }

    public static string? AndroidNdkRoot { get; private set; }

    public static string? AndroidBundletoolPath { get; private set; }

    public static string? HarmonyOSProjectRoot { get; private set; }

    public static string? PlatformToolchainInstallPlatform { get; private set; }

    public static string? PlatformToolchainCacheRoot { get; private set; }

    public static bool AndroidAcceptSdkLicenses { get; private set; }

    public static string? HarmonyOSLocalBundleArchive { get; private set; }

    public static string? HarmonyOSBundleImportPath { get; private set; }

    public static string? HarmonyOSResolveLockPath { get; private set; }

    public static string? HarmonyOSOfficialSdkRoot { get; private set; }

    public static string? HarmonyOSBuildLockPath { get; private set; }

    public static HarmonyOSOutputMode? HarmonyOSBuildOutputMode { get; private set; }

    public static string? HarmonyOSDeviceId { get; private set; }

    public static string? HarmonyOSAbilityName { get; private set; }

    public static HarmonyOSBuildRunInstallMode? HarmonyOSBuildInstallMode { get; private set; }

    public static bool HarmonyOSCleanupNewInstallationOnFailure { get; private set; }

    public static int HarmonyOSTargetApi { get; private set; } = 13;

    public static bool HarmonyOSTargetApiSpecified { get; private set; }

    public static string HarmonyOSTargetAbi { get; private set; } = "arm64-v8a";

    public static bool HarmonyOSTargetAbiSpecified { get; private set; }


    private static void LogCommands(List<string> args)
    {
        foreach ((string[] aliases, Action<List<string>> a, int m, string desc) in s_commands)
        {
            if (args.Count == 0 || Array.Exists(aliases, (x) => x.Equals(args[0])))
                Console.WriteLine($"[{string.Join(", ", aliases)}]: {desc}");
        }

        Environment.Exit(0);
    }


    private static (string[] commandAliases, Action<List<string>> commandAction, int mandatoryArgs, string description)[] s_commands =
    [
        (["--help", "-help", "-h"], LogCommands, -1,
            "Displays help information for a specific command, or all commands if no command is specified"),

        (["--project", "-project", "-p"], (args) => StartupProjectPath = args[0], 1,
            "Opens the provided source project path directly (skips launcher)"),

        (["--hub-session"], (args) => HubSessionId = args[0], 1,
            "XHub session id: report project lifecycle events for the hub (pairs with --project)"),

        (["--hub-info"], (_) => HubInfo = true, 0,
            "Prints engine version/protocol/capabilities as JSON and exits (XHub capability probe)"),

        (["--hub-command"], (_) => HubCommand = true, 0,
            "Reads one JSON request from stdin, executes it, reports NDJSON events on stdout, exits"),

        (["--serve"], (_) => Serve = true, 0,
            "Starts the in-process MCP server for live editor inspection"),

        (["--launcher-settings"], (_) => LauncherSettingsOpen = true, 0,
            "Opens the launcher Settings modal (Platforms page) at startup — deep link for tests/automation"),

        (["--launcher-settings-page"], (args) => { LauncherSettingsOpen = true; LauncherSettingsPage = args[0]; }, 1,
            "Opens the launcher Settings modal on a specific page (platforms|general) — deep link for tests/automation"),

        (["--install-module"], (args) => { LauncherSettingsOpen = true; InstallModuleName = args[0]; }, 1,
            "Starts installing an editor-level module by name once the launcher Settings page opens (pairs with --launcher-settings)"),

        (["--restart-parent"], (args) => RestartParentProcessId = int.Parse(args[0]), 1,
            "Internal restart handoff: wait for the previous editor to exit before opening its project"),

        (["--play-on-load"], (_) => PlayOnLoad = true, 0,
            "Enters play mode after the project and scene are ready (pairs with --panel-screenshot)"),

        (["--run-menu"], (args) => RunMenuPath = args[0], 1,
            "Invoke an editor menu item by path (e.g. 'Zonezero/Rebuild Battle HUD Prefab') after the project is ready, then quit"),

        (["--panel-screenshot"], (args) => PanelScreenshotType = args[0], 1,
            "After the project is ready, capture a DockPanel (full type name) to PNG and quit"),

        (["--screenshot-out"], (args) => PanelScreenshotOut = args[0], 1,
            "Output PNG path for --panel-screenshot"),

        (["--select-asset"], (args) => SelectAssetPath = args[0], 1,
            "Select an asset (project-relative path or GUID) after the project is ready"),

        (["--select-asset-tab"], (args) => SelectAssetTab = args[0], 1,
            "Open this tab (Model|Rig|Animation|Materials) of the selected asset's import inspector (pairs with --select-asset)"),

        (["--select-object"], (args) => SelectObjectName = args[0], 1,
            "Select a scene GameObject by name after the project is ready"),

        (["--hide-object"], (args) => HideObjectName = args[0], 1,
            "Hide a scene GameObject from Scene View after the project is ready"),

        (["--unpickable-object"], (args) => UnpickableObjectName = args[0], 1,
            "Mark a scene GameObject unpickable in Scene View after the project is ready"),

        (["--hierarchy-search"], (args) => HierarchySearch = args[0], 1,
            "Seed the Hierarchy search box after the project is ready (supports t:Type)"),
        (["--hierarchy-paint-reference"], (_) => { IncrementalHierarchy = true; HierarchyPaintReference = true; }, 0,
            "Validate production Hierarchy subtree painting with forced recapture each frame."),
        (["--hierarchy-paint-match-atlas"], (_) => { IncrementalHierarchy = true; HierarchyPaintReference = true; HierarchyPaintMatchAtlas = true; }, 0,
            "Diagnostic only: match the subtree atlas initial size to its host."),
        (["--hierarchy-paint-shared-atlas"], (_) => { IncrementalHierarchy = true; HierarchyPaintReference = true; HierarchyPaintSharedAtlas = true; }, 0,
            "Experimental forced Hierarchy capture with shared host fonts and bounded paint replay."),
        (["--hierarchy-paint-incremental"], (_) => { IncrementalHierarchy = true; HierarchyPaintReference = true; HierarchyPaintSharedAtlas = true; HierarchyPaintIncremental = true; }, 0,
            "Experimental Hierarchy paint reuse with exact visual dependency comparison."),
        (["--incremental-hierarchy"], (_) => IncrementalHierarchy = true, 0,
            "Enable experimental indexed Hierarchy rows"),
        (["--project-paint-incremental"], (_) => ProjectPaintIncremental = true, 0,
            "Experimental Project panel paint reuse: compared subtree regions, versioned thumbnails, virtualized rows."),
        (["--console-paint-incremental"], (_) => ConsolePaintIncremental = true, 0,
            "Experimental Console panel paint reuse: compared subtree regions for the log list and detail pane, versioned row paint."),
        (["--inspector-paint-incremental"], (_) => InspectorPaintIncremental = true, 0,
            "Experimental Inspector panel paint reuse: compared subtree region for the property scroll body; preview stays on the host."),
        (["--shell-paint-incremental"], (_) => ShellPaintIncremental = true, 0,
            "Experimental editor chrome paint reuse: compared subtree regions for the menu bar, toolbar and status bar, and idle secondary hosts skip unrelated invalidations."),
        (["--profiler-paint-incremental"], (_) => ProfilerPaintIncremental = true, 0,
            "Experimental Profiler paint reuse: compared subtree regions for the chart strips and details scroll; toolbar stays on the host. Pair with --shell-paint-incremental so a detached live Profiler is the only floater that receives Animation."),
        (["--scene-paint-incremental"], (_) => ScenePaintIncremental = true, 0,
            "Experimental Scene view chrome paint reuse: compared subtree region for the toolbar strip; the 3D viewport stays on the host."),
        (["--game-paint-incremental"], (_) => GamePaintIncremental = true, 0,
            "Experimental Game view chrome paint reuse: compared subtree region for the toolbar strip; the game image stays on the host."),
        (["--animation-paint-incremental"], (_) => AnimationPaintIncremental = true, 0,
            "Experimental Animation window paint reuse: compared subtree regions for the property list, ruler/events and dopesheet/curves; toolbar stays on the host. Pair with --shell-paint-incremental so a detached preview is the only floater that receives Animation."),
        (["--timeline-paint-incremental"], (_) => TimelinePaintIncremental = true, 0,
            "Experimental Timeline window paint reuse: compared subtree regions for the track headers and ruler/sheet; toolbar and inspector stay on the host. Pair with --shell-paint-incremental so a detached playing director is the only floater that receives Animation."),
        (["--animator-paint-incremental"], (_) => AnimatorPaintIncremental = true, 0,
            "Experimental Animator window paint reuse: compared subtree regions for the layers/parameters list and graph canvas; toolbar and inspector stay on the host. Pair with --shell-paint-incremental so a detached play-link is the only floater that receives Animation."),

        (["--restore-scene", "-restore", "-r"], (args) => RestoreScenePath = args[0], 1,
            "Loads the provided scene as the default scene to open instead of the last saved scene"),

        (["--refresh-assets"], (_) => RefreshAssetsMode = true, 0,
            "Headless scan+import of dirty/new assets into Library/cache, then exit (requires --project)"),

        (["--buildmode", "-build", "-b"], (args) => { BuildMode = true; StartupProjectPath = args[0]; }, 1,
            "Builds a project directly without opening the editor UI"),

        (["--output", "-output", "-o"], (args) => BuildOutputPath = args[0], 1,
            "Specifies the build output path when the editor builds a project"),

        (["--platform"], (args) => BuildPipelineId = NormalizeBuildPipelineId(args[0]), 1,
            "Selects the build pipeline (desktop, android, ios, or harmonyos)"),

        (["--build-profile"], (args) => BuildProfileToken = args[0], 1,
            "Builds a named .buildprofile asset (name, project relative path, or asset GUID)"),

        (["--build-and-run", "--run"], (_) => BuildAndRun = true, 0,
            "Builds and launches through the selected platform pipeline (Android APK/AAB, HarmonyOS HAP, or iOS .app via xcrun devicectl on macOS)"),


        (["--import-android-toolchain"], (args) => AndroidToolchainImportSource = args[0], 1,
            "Imports the whitelisted Unity AndroidPlayer toolchain into the XEngine cache"),

        (["--android-toolchain-cache"], (args) => AndroidToolchainImportDestination = args[0], 1,
            "Overrides the destination used by --import-android-toolchain"),

        (["--resolve-android-toolchain"], (args) => AndroidToolchainResolveLockPath = args[0], 1,
            "Resolves, probes, dry-runs, and writes an Android toolchain lock"),

        (["--verify-android-toolchain-lock"], (args) => AndroidToolchainVerifyLockPath = args[0], 1,
            "Revalidates every file referenced by an Android toolchain lock"),

        (["--android-imported-toolchain"], (args) => AndroidToolchainImportedRoot = args[0], 1,
            "Selects a verified XEngine Unity AndroidPlayer import"),

        (["--android-dotnet"], (args) => AndroidDotNetRoot = args[0], 1,
            "Overrides the .NET SDK root for Android toolchain resolution"),

        (["--android-jdk"], (args) => AndroidJavaSdkRoot = args[0], 1,
            "Overrides the JDK root for Android toolchain resolution"),

        (["--android-sdk"], (args) => AndroidSdkRoot = args[0], 1,
            "Overrides the Android SDK root for Android toolchain resolution"),

        (["--android-ndk"], (args) => AndroidNdkRoot = args[0], 1,
            "Overrides the Android NDK root for Android toolchain resolution"),

        (["--android-bundletool"], (args) => AndroidBundletoolPath = args[0], 1,
            "Overrides the bundletool jar for Android toolchain resolution"),

        (["--harmonyos-project-root"], (args) => HarmonyOSProjectRoot = args[0], 1,
            "Selects the project root used by HarmonyOS Bundle import and SDK resolution"),

        (["--import-harmonyos-bundle"], (args) => HarmonyOSBundleImportPath = args[0], 1,
            "Imports a verified OpenHarmony SDK Bundle into the selected project"),

        (["--resolve-harmonyos-sdk"], (args) => HarmonyOSResolveLockPath = args[0], 1,
            "Revalidates a project-local OpenHarmony SDK Bundle lock and resolves an official SDK"),

        (["--install-platform-tools"], (args) => PlatformToolchainInstallPlatform = args[0], 1,
            "Downloads/installs the toolchain pinned by an installed com.xengine.platform.* module (android|harmonyos|ios) into the managed SDK root"),

        (["--platform-toolchain-cache"], (args) => PlatformToolchainCacheRoot = args[0], 1,
            "Overrides the managed SDK root used by --install-platform-tools"),

        (["--accept-sdk-licenses"], (_) => AndroidAcceptSdkLicenses = true, 0,
            "Explicit consent to the Google Android SDK licenses (developer.android.com/studio/terms); required for the sdkmanager step of --install-platform-tools android"),

        (["--harmonyos-bundle"], (args) => HarmonyOSLocalBundleArchive = args[0], 1,
            "Local OpenHarmony SDK Bundle archive imported by --install-platform-tools harmonyos (used while the GitHub feed does not host it)"),

        (["--harmonyos-sdk"], (args) => HarmonyOSOfficialSdkRoot = args[0], 1,
            "Overrides the official OpenHarmony SDK root"),

        (["--harmonyos-lock"], (args) => HarmonyOSBuildLockPath = args[0], 1,
            "Selects the project-local OpenHarmony SDK Bundle lock for a build"),

        (["--harmonyos-api"], (args) =>
        {
            if (!int.TryParse(args[0], out int api))
                throw new Exception("--harmonyos-api requires an integer API level.");
            HarmonyOSTargetApi = api;
            HarmonyOSTargetApiSpecified = true;
        }, 1, "Selects the OpenHarmony Native SDK API level"),

        (["--harmonyos-abi"], (args) =>
        {
            HarmonyOSTargetAbi = args[0];
            HarmonyOSTargetAbiSpecified = true;
        }, 1,
            "Selects the OpenHarmony ABI (arm64-v8a or x86_64)"),

        (["--harmonyos-output"], (args) => HarmonyOSBuildOutputMode = ParseHarmonyOSOutputMode(args[0]), 1,
            "Selects HarmonyOS output mode (export-project, hap, or app)"),

        (["--device"], (args) => HarmonyOSDeviceId = args[0], 1,
            "Selects an HDC device for HarmonyOS Build & Run"),

        (["--harmonyos-ability"], (args) => HarmonyOSAbilityName = args[0], 1,
            "Selects the HarmonyOS entry ability for Build & Run"),

        (["--harmonyos-install-mode"], (args) => HarmonyOSBuildInstallMode = ParseHarmonyOSInstallMode(args[0]), 1,
            "Selects HarmonyOS install-or-replace or launch-only behavior"),

        (["--cleanup-new-install-on-failure"], (_) => HarmonyOSCleanupNewInstallationOnFailure = true, 0,
            "Removes only an application newly installed by a failed HarmonyOS run"),
    ];


    /// <summary>
    /// §6: locates XHub, translates legacy launcher deep links (<c>--launcher-settings</c>,
    /// <c>--install-module</c>) into XHub pages, launches it, and reports success. The editor
    /// exits when this returns true — the XHub instance owns the project list from here.
    /// </summary>
    /// <summary>
    /// §6: whether this launch should hand the project list over to XHub — true only for
    /// project-less GUI launches; every explicit tool mode stays in the editor.
    /// </summary>
    internal static bool ShouldHandOffToHub() =>
        !BuildMode && !RefreshAssetsMode && !Serve && !HubInfo && !HubCommand &&
        StartupProjectPath == null &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XENGINE_NO_HUB_HANDOFF"));

    internal static bool TryHandOffToHub()
    {
        string? hubExe = LocateHubExe();
        if (hubExe == null)
        {
            // §6: XHub missing — surface install-location and repair guidance. The in-editor
            // launcher stays available as the fallback so the user is never stranded.
            string searched = string.Join("\n  ", LocateHubCandidates());
            Runtime.Debug.LogError(
                "XHub 未找到，无法转交项目列表。已查找以下位置：\n  " + searched +
                "\n请安装 XHub 或设置 XENGINE_XHUB_EXE 指向 XHub 可执行文件。");
            Console.Error.WriteLine(
                "[xengine] XHub not found; falling back to the in-editor launcher. Searched:\n  " + searched);
            return false;
        }

        var hubArgs = new List<string>();
        // §6 参数迁移: legacy launcher deep links translate into XHub pages.
        if (LauncherSettingsOpen)
            hubArgs.Add("--page settings");
        if (!string.IsNullOrEmpty(InstallModuleName))
            hubArgs.Add("--page settings");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = hubExe,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(hubExe) ?? AppContext.BaseDirectory,
            };
            foreach (string arg in hubArgs)
                startInfo.ArgumentList.Add(arg);
            Process.Start(startInfo);
            Runtime.Debug.Log($"[hub] handing off to XHub: {hubExe}");
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[hub] XHub launch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>XHub discovery: explicit override → editor-relative → per-user install roots.</summary>
    internal static string? LocateHubExe()
    {
        string? overridePath = Environment.GetEnvironmentVariable("XENGINE_XHUB_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        foreach (string candidate in LocateHubCandidates())
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    internal static IEnumerable<string> LocateHubCandidates()
    {
        string baseDir = AppContext.BaseDirectory;
        // Editor payload layout: Build/Editor/<Config>[/net10.0] — walk a few levels for a
        // sibling XHub install; also the per-user roots the hub installer uses (N9).
        string hubExe = OperatingSystem.IsWindows() ? "XHub.exe" : "XHub";
        yield return Path.Combine(baseDir, hubExe);
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", hubExe));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XHub", hubExe));
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Programs", "XEngine", "XHub", hubExe);
        yield return Path.Combine(localAppData, "XEngine", "XHub", hubExe);
        if (OperatingSystem.IsMacOS())
        {
            // macOS distribution layout: XHub.app bundles the executable under Contents/MacOS.
            yield return Path.Combine(baseDir, "XHub.app", "Contents", "MacOS", "XHub");
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "XHub",
                "bin", "Release", "net10.0", "osx-arm64", "publish", "XHub.app", "Contents", "MacOS", "XHub"));
            yield return Path.Combine("/Applications", "XHub.app", "Contents", "MacOS", "XHub");
        }
    }

    /// <summary>Hub handshake (§4.3 --hub-info): version, architecture, protocol, capabilities.</summary>
    private static void PrintHubInfo()
    {
        var info = new
        {
            protocolVersion = Projects.HubSession.ProtocolVersion,
            engineVersion = Projects.ProjectEngineVersion.Canonicalize(Assembly.GetExecutingAssembly()
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.1"),
            displayVersion = Core.EditorApplication.EngineVersion,
            architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            capabilities = new[] { "sessionEvents", "createProject" },
            templateDirs = Array.Empty<string>(),
        };
        Console.WriteLine(JsonSerializer.Serialize(info));
    }

    internal static void ReadArguments(string[] args)
    {
        Queue<string> argQueue = new(args);

        while (argQueue.TryDequeue(out string result))
        {
            if (result.StartsWith("--graphics=", StringComparison.OrdinalIgnoreCase))
            {
                SetGraphicsBackend(result["--graphics=".Length..]);
                continue;
            }

            // P7 render-thread switches. Graphics reads them straight off the process command
            // line (they must be resolvable before the device exists), so the parser only has to
            // accept and skip them instead of routing them anywhere.
            if (result.StartsWith("--render-thread=", StringComparison.OrdinalIgnoreCase)
                || result.Equals("--no-render-thread", StringComparison.OrdinalIgnoreCase)
                || result.StartsWith("--frame-pipelining=", StringComparison.OrdinalIgnoreCase))
                continue;

            if (result.Equals("--graphics", StringComparison.OrdinalIgnoreCase)
                || result.Equals("-g", StringComparison.OrdinalIgnoreCase))
            {
                if (!argQueue.TryDequeue(out string? backend))
                    throw new Exception($"Not enough arguments specified for '{result}'");
                SetGraphicsBackend(backend);
                continue;
            }

            (string[]? a, Action<List<string>>? action, int argsCount, string? d) =
                Array.Find(s_commands, (x) => Array.Exists(x.commandAliases, (x) => x.Equals(result)));

            if (action == null)
                throw new Exception($"Unknown argument '{result}'");

            List<string> cmdArgs = [];

            while (argQueue.TryPeek(out string next))
            {
                // Next argument is a command
                if (next.StartsWith('-'))
                    break;

                if (argsCount > -1 && cmdArgs.Count + 1 > argsCount)
                    throw new Exception($"Too many arguments specified for '{result}'");

                cmdArgs.Add(argQueue.Dequeue());
            }

            if (cmdArgs.Count < argsCount)
                throw new Exception($"Not enough arguments specified for '{result}'");

            action.Invoke(cmdArgs);
        }

        if ((!string.IsNullOrWhiteSpace(HarmonyOSBundleImportPath) || !string.IsNullOrWhiteSpace(HarmonyOSResolveLockPath)) &&
            string.IsNullOrWhiteSpace(HarmonyOSProjectRoot))
        {
            throw new Exception("--harmonyos-project-root is required with HarmonyOS Bundle import or SDK resolution.");
        }
        if (!BuildMode &&
            (!string.IsNullOrWhiteSpace(BuildPipelineId) || !string.IsNullOrWhiteSpace(BuildProfileToken) ||
             BuildAndRun || HarmonyOSBuildOutputMode.HasValue ||
             !string.IsNullOrWhiteSpace(HarmonyOSBuildLockPath) || !string.IsNullOrWhiteSpace(HarmonyOSDeviceId) ||
             !string.IsNullOrWhiteSpace(HarmonyOSAbilityName) || HarmonyOSBuildInstallMode.HasValue ||
             HarmonyOSCleanupNewInstallationOnFailure))
        {
            throw new Exception("Build pipeline and HarmonyOS run overrides require --buildmode.");
        }
    }

    internal static void ApplyBuildModeOverrides(BuildSettings settings, EditorSettings editorSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(editorSettings);
        ApplyBuildProfileSelection(settings);

        bool hasHarmonyOSOverride = HarmonyOSTargetApiSpecified || HarmonyOSTargetAbiSpecified ||
            HarmonyOSBuildOutputMode.HasValue || !string.IsNullOrWhiteSpace(HarmonyOSBuildLockPath) ||
            !string.IsNullOrWhiteSpace(HarmonyOSOfficialSdkRoot) || !string.IsNullOrWhiteSpace(HarmonyOSDeviceId) ||
            !string.IsNullOrWhiteSpace(HarmonyOSAbilityName) || HarmonyOSBuildInstallMode.HasValue ||
            HarmonyOSCleanupNewInstallationOnFailure;
        if (!hasHarmonyOSOverride)
            return;
        if (!string.Equals(settings.ActivePipelineId, "xengine.harmonyos", StringComparison.Ordinal))
            throw new InvalidOperationException("HarmonyOS build overrides require the xengine.harmonyos pipeline.");

        HarmonyOSBuildProfile profile = settings.GetProfile<HarmonyOSBuildProfile>(typeof(HarmonyOSBuildPipeline));
        if (HarmonyOSTargetApiSpecified)
            profile.TargetApi = HarmonyOSTargetApi;
        if (HarmonyOSTargetAbiSpecified)
            profile.TargetAbis = [HarmonyOSTargetAbi];
        if (HarmonyOSBuildOutputMode.HasValue)
            profile.OutputMode = HarmonyOSBuildOutputMode.Value;
        if (!string.IsNullOrWhiteSpace(HarmonyOSDeviceId))
            profile.RunDeviceId = HarmonyOSDeviceId;
        if (!string.IsNullOrWhiteSpace(HarmonyOSAbilityName))
            profile.AbilityName = HarmonyOSAbilityName;
        if (HarmonyOSBuildInstallMode.HasValue)
            profile.InstallMode = HarmonyOSBuildInstallMode.Value;
        if (HarmonyOSCleanupNewInstallationOnFailure)
            profile.CleanupNewInstallationOnFailure = true;
        if (!string.IsNullOrWhiteSpace(HarmonyOSOfficialSdkRoot))
            editorSettings.HarmonyOSOfficialSdkRoot = HarmonyOSOfficialSdkRoot;
        if (!string.IsNullOrWhiteSpace(HarmonyOSBuildLockPath))
            editorSettings.HarmonyOSLastResolvedLockPath = HarmonyOSBuildLockPath;
    }

    /// <summary>
    /// Resolves <c>--build-profile</c> and <c>--platform</c> into the active profile and pipeline for
    /// this build. The selection is only applied in memory: a headless build must not rewrite the
    /// project's persisted choice, otherwise a CI machine building three profiles in a row would
    /// leave the project pointing at whichever ran last.
    /// </summary>
    internal static void ApplyBuildProfileSelection(BuildSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(BuildProfileToken))
        {
            BuildProfileHandle handle = BuildProfileRegistry.Resolve(BuildProfileToken!)
                ?? throw new InvalidOperationException(DescribeUnknownProfile(BuildProfileToken!));

            string profilePipeline = handle.Profile.PipelineId ?? "";
            if (!string.IsNullOrWhiteSpace(BuildPipelineId) &&
                !string.Equals(profilePipeline, BuildPipelineId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Build profile '{handle.DisplayName}' targets '{profilePipeline}', which conflicts with " +
                    $"--platform '{BuildPipelineId}'. Pass only one of the two.");
            }

            settings.ActiveProfileGuid = handle.Guid;
            settings.ActivePipelineId = profilePipeline;
            return;
        }

        if (string.IsNullOrWhiteSpace(BuildPipelineId))
            return;

        // --platform on its own still selects a pipeline, but it must not silently inherit the player
        // setting and asset import overrides of a profile that targets a different platform.
        if (BuildProfileRegistry.GetActive(settings) is { } active &&
            !string.Equals(active.Profile.PipelineId, BuildPipelineId, StringComparison.Ordinal))
        {
            settings.ActiveProfileGuid = Guid.Empty;
        }

        settings.ActivePipelineId = BuildPipelineId;
    }

    private static string DescribeUnknownProfile(string token)
    {
        List<BuildProfileHandle> available = BuildProfileRegistry.LoadAll();
        if (available.Count == 0)
            return $"No build profile matches '{token}'. This project has no .buildprofile assets yet.";

        var names = new List<string>(available.Count);
        foreach (BuildProfileHandle handle in available)
            names.Add($"'{handle.DisplayName}'");

        return $"No build profile matches '{token}'. Available profiles: {string.Join(", ", names)}.";
    }

    private static void SetGraphicsBackend(string name)
    {
        if (!Runtime.RHI.GraphicsBackendSelection.TryParseName(name, out Runtime.RHI.GraphicsBackend backend))
            throw new Exception($"Unknown graphics backend '{name}'");
        Runtime.RHI.GraphicsBackendSelection.Preferred = backend;
    }

    private static string NormalizeBuildPipelineId(string value) => value.ToLowerInvariant() switch
    {
        "desktop" or "xengine.desktop" => "xengine.desktop",
        "android" or "xengine.android" => "xengine.android",
        "ios" or "xengine.ios" => "xengine.ios",
        "harmonyos" or "xengine.harmonyos" => "xengine.harmonyos",
        "web" or "browser" or "wechat" or "douyin" or "xengine.web" => "xengine.web",
        _ => throw new Exception($"Unknown build platform '{value}'."),
    };

    private static HarmonyOSOutputMode ParseHarmonyOSOutputMode(string value) => value.ToLowerInvariant() switch
    {
        "export" or "export-project" => HarmonyOSOutputMode.ExportProject,
        "hap" => HarmonyOSOutputMode.Hap,
        "app" => HarmonyOSOutputMode.App,
        _ => throw new Exception($"Unknown HarmonyOS output mode '{value}'."),
    };

    private static HarmonyOSBuildRunInstallMode ParseHarmonyOSInstallMode(string value) => value.ToLowerInvariant() switch
    {
        "install-or-replace" => HarmonyOSBuildRunInstallMode.InstallOrReplace,
        "launch-only" => HarmonyOSBuildRunInstallMode.LaunchOnly,
        _ => throw new Exception($"Unknown HarmonyOS install mode '{value}'."),
    };


    /// <summary>
    /// True when the process must print to stdout and then typically exit: help, headless build,
    /// and toolchain import/resolve. GUI launches (double-click, <c>--project</c>, <c>--serve</c>)
    /// stay console-free under the Release WinExe subsystem.
    /// </summary>
    internal static bool NeedsCliConsole(string[] args)
    {
        if (args == null || args.Length == 0)
            return false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--hub-info", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--hub-command", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--refresh-assets", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--buildmode", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-build", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-b", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--import-android-toolchain", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--resolve-android-toolchain", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--verify-android-toolchain-lock", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--import-harmonyos-bundle", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--resolve-harmonyos-sdk", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--install-platform-tools", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static void EnsureCliConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;
        EnsureWindowsCliConsole();
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureWindowsCliConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
            return;

        const uint attachParentProcess = 0xFFFFFFFF;
        if (!AttachConsole(attachParentProcess))
            AllocConsole();

        Console.OutputEncoding = Encoding.UTF8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public static void Main(string[] args)
    {
        BundledDotnet.Configure();
        RegisterMiniAudioExResolver();

        if (NeedsCliConsole(args))
            EnsureCliConsole();

        ReadArguments(args);

        if (HubSessionId != null)
            Projects.HubSession.Activate(HubSessionId);

        // §6 launcher replacement: a project-less GUI launch hands off to XHub. Explicit tool
        // modes (--buildmode / --refresh-assets / --serve / toolchain CLIs / --project / the
        // hub CLI probes --hub-info & --hub-command) keep their existing paths and are never
        // redirected — with XHub installed, the handoff would launch the hub GUI and exit
        // before the machine-readable JSON ever prints.
        if (ShouldHandOffToHub())
        {
            if (TryHandOffToHub())
            {
                Core.EditorLog.Shutdown();
                return;
            }
        }

        if (HubInfo)
        {
            PrintHubInfo();
            return;
        }

        if (HubCommand)
        {
            EditorHubCommands.RunOnce();
            Core.EditorLog.Shutdown();
            return;
        }

        if (RestartParentProcessId > 0)
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(RestartParentProcessId);
                if (!parent.WaitForExit(30000))
                {
                    Console.Error.WriteLine("原编辑器尚未关闭，已取消重新打开项目。请保存并关闭后重试。");
                    Environment.ExitCode = 1;
                    return;
                }
            }
            catch (ArgumentException) { } // Parent already exited; its OS lock is released.
        }

        if (!string.IsNullOrWhiteSpace(AndroidToolchainImportSource))
        {
            RunAndroidToolchainImport();
            return;
        }

        if (!string.IsNullOrWhiteSpace(AndroidToolchainResolveLockPath))
        {
            RunAndroidToolchainResolve();
            return;
        }

        if (!string.IsNullOrWhiteSpace(AndroidToolchainVerifyLockPath))
        {
            RunAndroidToolchainLockVerification();
            return;
        }

        if (!string.IsNullOrWhiteSpace(HarmonyOSBundleImportPath))
        {
            RunHarmonyOSBundleImport();
            return;
        }

        if (!string.IsNullOrWhiteSpace(HarmonyOSResolveLockPath))
        {
            RunHarmonyOSSdkResolve();
            return;
        }

        if (!string.IsNullOrWhiteSpace(PlatformToolchainInstallPlatform))
        {
            RunPlatformToolchainInstall();
            return;
        }

        // If no --graphics / -g argument was passed on the command line, honour the saved editor
        // preference. GraphicsBackendSelection.Preferred is still null until something sets it;
        // only the preference path runs here so the CLI argument always wins.
        if (!Runtime.RHI.GraphicsBackendSelection.Preferred.HasValue)
        {
            Runtime.RHI.GraphicsBackendSelection.Preferred = ResolveSavedGraphicsBackend(
                Theming.EditorSettings.Instance.PreferredGraphicsBackend,
                Environment.GetEnvironmentVariable("XENGINE_GRAPHICS_BACKEND"),
                OperatingSystem.IsMacOS(),
                () => BuildMode || RefreshAssetsMode || IsGlfwVulkanAvailable());
        }

        if (RefreshAssetsMode)
        {
            if (string.IsNullOrWhiteSpace(StartupProjectPath))
                throw new Exception("--refresh-assets requires --project <path>.");
            Environment.ExitCode = RunRefreshAssets(StartupProjectPath);
            return;
        }

        if (BuildMode)
        {
            EditorRegistries.Initialize();

            var project = Project.Open(StartupProjectPath, exclusive: true);
            project.SetActive();

            // Load user script assemblies before registry scanning
            ScriptAssemblyManager.LoadAssemblies(project);
            // Re-scan now that package Editor assemblies (e.g. optional importers like
            // com.xengine.spine) are loaded — otherwise they never claim their extensions.
            EditorRegistries.Reinitialize();

            // Initialize asset database for the already-opened project
            var db = new EditorAssetBackend(Project.Current!);
            db.Initialize();

            // Load project settings
            EditorRegistries.OnProjectOpened();

            BuildSettings buildSettings = EditorRegistries.GetSettings<BuildSettings>()
                ?? throw new InvalidOperationException("BuildSettings not found.");
            ApplyBuildModeOverrides(buildSettings, EditorSettings.Instance);


            // Null -o means "use the active profile's output, or the shared BuildSettings default".
            // Do not invent a ../Builds path: that would ignore OverrideOutputDirectory.
            Build.ProjectBuilder.StartBuildAsync(BuildAndRun, BuildOutputPath);
            return;
        }

        // Paper compiles its Metal UI shader before the first editor frame. Resolve the same
        // mounted Metal Toolchain that Player packaging uses and expose its bin directory to
        // Slang before the runtime shader compiler falls back to xcrun.
        Runtime.RHI.GraphicsBackend editorBackend = Runtime.RHI.GraphicsBackendSelection.ResolveWindowedAuto(
            Runtime.RHI.GraphicsBackendSelection.Preferred
            ?? Runtime.RHI.GraphicsBackendSelection.Parse(Environment.GetCommandLineArgs()));
        if (OperatingSystem.IsMacOS() && editorBackend == Runtime.RHI.GraphicsBackend.Metal)
        {
            if (!MacMetalToolchain.TryPrepare(out string metalToolchainDiagnostic))
                throw new PlatformNotSupportedException(
                    "The editor selected Metal but its Metal compiler could not be prepared. " +
                    metalToolchainDiagnostic);
            MetalToolchainDiagnostic = metalToolchainDiagnostic;
        }

        var editor = new EditorApplication();
        // ★ Layer 2 (B3-cont): --serve starts the in-process MCP server so an AI agent can drive a
        // LIVE editor instance (runtime_state/logs/playmode/live_scene tools). Actual start happens
        // inside EditorApplication.Initialize() AFTER project script assemblies load, so package
        // runtime tools (McpServerToolType scan) are registered from the first moment.
        editor.Run("XEngine Editor", 1920, 1080);

        Projects.HubSession.Emit(Projects.HubSession.Closing, new { exitCode = Environment.ExitCode });

        // Flush and close the persistent editor log (Unity Editor.log counterpart).
        Core.EditorLog.Shutdown();

        //Runtime.Window.InternalWindow.WindowState = EditorSettings.Instance.WindowMaximized ? Silk.NET.Windowing.WindowState.Maximized : Silk.NET.Windowing.WindowState.Normal;
    }

    internal static Runtime.RHI.GraphicsBackend? ResolveSavedGraphicsBackend(
        string? saved, string? environment, bool isMacOS, Func<bool> vulkanAvailable)
    {
        // CLI has already set Preferred before this path. An explicit environment choice also
        // takes precedence over persisted preferences, including a request for Auto.
        if (Runtime.RHI.GraphicsBackendSelection.TryParseName(environment, out var environmentBackend))
            return environmentBackend;
        if (!Runtime.RHI.GraphicsBackendSelection.TryParseName(saved, out var savedBackend))
            return null;
        if (isMacOS && savedBackend == Runtime.RHI.GraphicsBackend.Vulkan && !vulkanAvailable())
        {
            Console.Error.WriteLine("Saved Vulkan preference is unavailable to GLFW; using Metal for this editor session.");
            return Runtime.RHI.GraphicsBackend.Metal;
        }
        return savedBackend;
    }

    private static bool IsGlfwVulkanAvailable()
    {
        // Run before any window exists, on Main's thread. Probe the same loader GLFW uses for
        // window surfaces rather than merely checking for a Homebrew Vulkan library on disk.
        // GLFW's macOS Init changes the working directory for app bundles; undo that side effect.
        string workingDirectory = Directory.GetCurrentDirectory();
        try
        {
            // The provider initializes GLFW once and caches it for window creation. Keep that
            // lifecycle intact: terminating here leaves Silk holding an uninitialized instance.
            return Silk.NET.GLFW.GlfwProvider.GLFW.Value.VulkanSupported();
        }
        finally
        {
            Directory.SetCurrentDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// ★ Gen-P3.1 — Open a project, load scripts, scan+import dirty/new assets into
    /// <c>Library/cache</c>, print a JSON summary, return 0 on success.
    /// </summary>
    internal static int RunRefreshAssets(string projectPath)
    {
        EditorAssetBackend? db = null;
        try
        {
            var project = Project.Open(projectPath, exclusive: true);
            project.SetActive();

            ScriptAssemblyManager.LoadAssemblies(project);
            // Project.Open/SetActive can initialize the registries before the project's script
            // assemblies (e.g. optional package importers like com.xengine.spine) are loaded, so
            // re-scan here — otherwise package importers never claim their extensions headlessly.
            EditorRegistries.Reinitialize();

            db = new EditorAssetBackend(project);
            db.Initialize();
            EditorRegistries.OnProjectOpened();

            var result = new RefreshAssetsCliResult(
                true, db.AssetCount, project.CachePath, project.RootPath, null);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.WriteLine(JsonSerializer.Serialize(
                new RefreshAssetsCliResult(false, 0, "", projectPath, ex.Message)));
            return 1;
        }
        finally
        {
            db?.Dispose();
        }
    }

    private static void RunAndroidToolchainImport()
    {
        var options = new AndroidToolchainImportOptions
        {
            SourceRoot = AndroidToolchainImportSource!,
            DestinationRoot = string.IsNullOrWhiteSpace(AndroidToolchainImportDestination)
                ? AndroidToolchainBootstrapper.GetDefaultDestinationRoot()
                : AndroidToolchainImportDestination,
        };
        var progress = new ConsoleImportProgress();
        AndroidToolchainImportResult result = new AndroidToolchainBootstrapper()
            .ImportAsync(options, progress)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.CachePath,
            result.ManifestPath,
            result.Reused,
            result.Manifest.Fingerprint,
            result.Manifest.TotalBytes,
            FileCount = result.Manifest.Files.Count,
            Components = result.Manifest.Components,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunAndroidToolchainResolve()
    {
        EditorSettings settings = EditorSettings.Instance;
        var options = new AndroidToolchainResolveOptions
        {
            CommandLineOverrides = new AndroidToolchainPathOverrides
            {
                DotNetRoot = AndroidDotNetRoot,
                JavaSdkRoot = AndroidJavaSdkRoot,
                AndroidSdkRoot = AndroidSdkRoot,
                AndroidNdkRoot = AndroidNdkRoot,
                BundletoolPath = AndroidBundletoolPath,
            },
            UserPreferences = new AndroidToolchainPathOverrides
            {
                DotNetRoot = settings.AndroidDotNetRoot,
                JavaSdkRoot = settings.AndroidJavaSdkRoot,
                AndroidSdkRoot = settings.AndroidSdkRoot,
                AndroidNdkRoot = settings.AndroidNdkRoot,
                BundletoolPath = settings.AndroidBundletoolPath,
            },
            ImportedToolchainRoot = string.IsNullOrWhiteSpace(AndroidToolchainImportedRoot)
                ? settings.AndroidToolchainResolvedPath
                : AndroidToolchainImportedRoot,
            ToolchainCacheRoot = string.IsNullOrWhiteSpace(AndroidToolchainImportDestination)
                ? settings.AndroidToolchainCacheRoot
                : AndroidToolchainImportDestination,
            LockFilePath = AndroidToolchainResolveLockPath,
        };
        var progress = new ConsoleResolveProgress();
        AndroidToolchainSnapshot snapshot = new AndroidToolchainResolver()
            .ResolveAsync(options, progress)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            LockPath = Path.GetFullPath(AndroidToolchainResolveLockPath!),
            snapshot.Fingerprint,
            Host = snapshot.Host,
            DryRun = snapshot.DryRun,
            Components = snapshot.Components,
            SdkPackages = snapshot.SdkPackages,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunAndroidToolchainLockVerification()
    {
        AndroidToolchainSnapshot snapshot = AndroidToolchainLockFile
            .ReadAndValidateAsync(AndroidToolchainVerifyLockPath!)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            LockPath = Path.GetFullPath(AndroidToolchainVerifyLockPath!),
            snapshot.Fingerprint,
            Valid = true,
            snapshot.Host,
            snapshot.DryRun,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RunHarmonyOSBundleImport()
    {
        try
        {
            HarmonyOSSdkBundleImportResult result = new HarmonyOSSdkBundleImporter()
                .ImportAsync(new HarmonyOSSdkBundleImportRequest(HarmonyOSProjectRoot!, HarmonyOSBundleImportPath!))
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                releaseId = result.Lock.ReleaseId,
                bundleRelativePath = result.Lock.BundleRelativePath,
                fingerprint = result.Lock.Fingerprint,
                reused = result.Reused,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (HarmonyOSBundleException exception)
        {
            WriteHarmonyOSError(exception);
        }
    }

    private static void RunPlatformToolchainInstall()
    {
        if (!ToolchainManifest.TryMapPlatform(PlatformToolchainInstallPlatform!.Trim().ToLowerInvariant(),
                out PlatformToolchainKind platform))
        {
            Console.Error.WriteLine($"Unknown platform '{PlatformToolchainInstallPlatform}' (expected android|harmonyos|ios).");
            Environment.ExitCode = 1;
            return;
        }

        // Editor-level module first: when neither the per-user module store nor the Editor bundle
        // carries the platform module, install it from npm into the store — no project required.
        // A --project remains a valid module source (legacy manifest pin) and the bundle-import
        // target for HarmonyOS.
        if (!ToolchainManifest.IsEditorModulePresent(platform))
        {
            string moduleName = ToolchainManifest.ModulePackageName(platform);
            Console.WriteLine($"[module] '{moduleName}' not present at editor level — installing from npm…");
            try
            {
                var consoleModuleProgress = new ConsoleEditorModuleProgress();
                Projects.Packages.EditorModules.EditorModuleInstaller.InstallAsync(
                    moduleName, progress: consoleModuleProgress).GetAwaiter().GetResult();
                Console.WriteLine($"[module] installed '{moduleName}' into the editor module store.");
            }
            catch (Exception moduleException)
            {
                Console.Error.WriteLine($"[module] npm install failed: {moduleException.Message}");
            }
        }
        if (!ToolchainManifest.IsEditorModulePresent(platform) && string.IsNullOrWhiteSpace(StartupProjectPath))
        {
            Console.Error.WriteLine(
                $"Platform module '{ToolchainManifest.ModulePackageName(platform)}' is unavailable at editor level and no --project was given to resolve it from.\n" +
                "Fix the module install above (check network / XENGINE_NPM_REGISTRY), or pass --project <path>.");
            Environment.ExitCode = 1;
            return;
        }
        Projects.Project? project = string.IsNullOrWhiteSpace(StartupProjectPath)
            ? null
            : Projects.Project.Open(StartupProjectPath!);

        try
        {
            if (platform == PlatformToolchainKind.Android)
            {
                var progress = new ConsoleToolchainProgress();
                AndroidChannelResult channel = AndroidToolchainChannel.InstallAsync(new AndroidChannelOptions
                {
                    Project = project,
                    CacheRootOverride = PlatformToolchainCacheRoot,
                    AcceptLicenses = AndroidAcceptSdkLicenses,
                    Progress = progress,
                }).GetAwaiter().GetResult();
                if (channel.NeedsLicenseConsent)
                {
                    Console.Error.WriteLine(
                        "Google Android SDK licenses must be accepted before sdkmanager runs.\n" +
                        "Review the terms at " + AndroidToolchainChannel.LicenseTermsUrl +
                        " and re-run with --accept-sdk-licenses.");
                    Environment.ExitCode = 2;
                    return;
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = channel.Success,
                    error = channel.Error,
                    platform = "android",
                    channel.Core.InstallRoot,
                    sdkPackages = channel.InstalledPackages,
                    Components = channel.Core.Components.Select(c => new
                    {
                        c.Id,
                        status = c.Status.ToString().ToLowerInvariant(),
                        c.Root,
                        c.Error,
                    }),
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (!channel.Success) Environment.ExitCode = 1;
                return;
            }

            if (platform == PlatformToolchainKind.HarmonyOS)
            {
                var progressHos = new ConsoleToolchainProgress();
                HarmonyOSChannelResult hos = HarmonyOSToolchainChannel.InstallAsync(new HarmonyOSChannelOptions
                {
                    Project = project,
                    CacheRootOverride = PlatformToolchainCacheRoot,
                    LocalBundleArchivePath = HarmonyOSLocalBundleArchive,
                    Progress = progressHos,
                }).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = hos.Success,
                    error = hos.Error,
                    bundleError = hos.BundleImportError,
                    platform = "harmonyos",
                    hos.Core.InstallRoot,
                    officialSdkRoot = hos.OfficialSdkRoot,
                    hdc = hos.HdcPath,
                    Components = hos.Core.Components.Select(c => new
                    {
                        c.Id,
                        status = c.Status.ToString().ToLowerInvariant(),
                        c.Root,
                        c.Error,
                    }),
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (!hos.Success) Environment.ExitCode = 1;
                return;
            }

            var progress2 = new ConsoleToolchainProgress();
            PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
                    project, platform, PlatformToolchainCacheRoot, progress2)
                .GetAwaiter().GetResult();            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = result.Success,
                result.Platform,
                result.InstallRoot,
                Components = result.Components.Select(c => new
                {
                    c.Id,
                    status = c.Status.ToString().ToLowerInvariant(),
                    c.Root,
                    c.Sha256,
                    c.Error,
                }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!result.Success) Environment.ExitCode = 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
        }
    }

    /// <summary>Console adapter for editor-module installs: phase transitions only.</summary>
    private sealed class ConsoleEditorModuleProgress : IProgress<Projects.Packages.EditorModules.EditorModuleProgress>
    {
        private Projects.Packages.EditorModules.EditorModulePhase _lastPhase;

        public void Report(Projects.Packages.EditorModules.EditorModuleProgress value)
        {
            if (value.Phase == _lastPhase) return;
            _lastPhase = value.Phase;
            Console.WriteLine($"[module] {value.Phase.ToString().ToLowerInvariant()}: {value.Message}");
        }
    }

    /// <summary>Console adapter in the house style: stage transitions only, no per-byte spam.</summary>
    private sealed class ConsoleToolchainProgress : IProgress<PlatformToolchainProgress>
    {
        private string? _lastStage;
        private string? _lastComponent;

        public void Report(PlatformToolchainProgress value)
        {
            if (value.Stage == _lastStage && value.ComponentId == _lastComponent) return;
            _lastStage = value.Stage;
            _lastComponent = value.ComponentId;
            string detail = string.IsNullOrWhiteSpace(value.Detail) ? "" : " — " + value.Detail;
            Console.WriteLine($"[platform-tools] {value.Stage} {value.ComponentId}{detail}");
        }
    }

    private static void RunHarmonyOSSdkResolve()
    {
        try
        {
            EditorSettings settings = EditorSettings.Instance;
            HarmonyOSToolchainSnapshot snapshot = new HarmonyOSToolchainResolver()
                .ResolveAsync(new HarmonyOSToolchainResolveOptions
                {
                    ProjectRoot = HarmonyOSProjectRoot!,
                    LockFilePath = HarmonyOSResolveLockPath,
                    CommandLineSdkRoot = HarmonyOSOfficialSdkRoot,
                    UserPreferenceSdkRoot = settings.HarmonyOSOfficialSdkRoot,
                    EnvironmentSdkRoot = Environment.GetEnvironmentVariable("OHOS_SDK_HOME"),
                    TargetApi = HarmonyOSTargetApi,
                    TargetAbi = HarmonyOSTargetAbi,
                })
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                bundleFingerprint = snapshot.BundleFingerprint,
                bundleRelativePath = snapshot.BundleRelativePath,
                catalogSha256 = snapshot.CatalogSha256,
                sdkRootSource = snapshot.SdkRootSource,
                sdkRootSha256 = snapshot.SdkRootSha256,
                targetApi = snapshot.TargetApi,
                targetAbi = snapshot.TargetAbi,
                packageHashes = snapshot.PackageHashes,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (HarmonyOSBundleException exception)
        {
            WriteHarmonyOSError(exception);
        }
    }

    private static void WriteHarmonyOSError(HarmonyOSBundleException exception)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = false,
            errorCode = exception.Code.ToString(),
            message = exception.Message,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ConsoleImportProgress : IProgress<AndroidToolchainImportProgress>
    {
        private string _lastStage = string.Empty;

        public void Report(AndroidToolchainImportProgress value)
        {
            if (string.Equals(_lastStage, value.Stage, StringComparison.Ordinal))
                return;
            _lastStage = value.Stage;
            Console.WriteLine($"[Android toolchain] {value.Stage}: {value.TotalFiles} files, {value.TotalBytes} bytes");
        }
    }

    private sealed class ConsoleResolveProgress : IProgress<AndroidToolchainResolveProgress>
    {
        private string _last = string.Empty;

        public void Report(AndroidToolchainResolveProgress value)
        {
            string current = value.Stage + ":" + value.Detail;
            if (string.Equals(current, _last, StringComparison.Ordinal))
                return;
            _last = current;
            Console.WriteLine($"[Android toolchain] {value.Stage}: {value.Detail} ({value.Completed}/{value.Total})");
        }
    }

    /// <summary>
    /// "miniaudioex" is manually vendored (not a real NuGet native-asset package), so a self-contained
    /// Editor publish never flattens it to the app root and needs explicit resolving. Registered here
    /// (Editor-only), not in XEngine.Runtime.dll: exported Player builds already register their own
    /// resolver for that assembly, and only one resolver per assembly is allowed.
    /// </summary>
    private static void RegisterMiniAudioExResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(XEngine.Runtime.Game).Assembly, (libraryName, _, _) =>
        {
            if (libraryName != "miniaudioex")
                return IntPtr.Zero;

            string? os = OperatingSystem.IsWindows() ? "win"
                       : OperatingSystem.IsMacOS() ? "osx"
                       : OperatingSystem.IsLinux() ? "linux"
                       : null;

            // ProcessArchitecture (not OSArchitecture): a 32-bit process on a 64-bit Windows OS must
            // load the x86 native lib, not x64 - Libraries/win-x86 exists precisely for that case.
            string? arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                Architecture.Arm => "arm",
                _ => null,
            };

            if (os == null || arch == null)
                return IntPtr.Zero;

            string fileName = OperatingSystem.IsWindows() ? "miniaudioex.dll"
                             : OperatingSystem.IsMacOS() ? "libminiaudioex.dylib"
                             : "libminiaudioex.so";

            string path = Path.Combine(AppContext.BaseDirectory, "runtimes", $"{os}-{arch}", "native", fileName);
            return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
        });
    }
}

/// <summary>★ Gen-P3.1 — JSON stdout from <c>--refresh-assets</c>.</summary>
public sealed record RefreshAssetsCliResult(
    bool Success,
    int AssetCount,
    string CachePath,
    string ProjectRoot,
    string? Error);
