using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using XEngine.Editor.Core;
using XEngine.Runtime;

namespace XEngine.Editor.Theming;

/// <summary>
/// Global editor settings. Saved to AppData/XEngine/EditorSettings.json.
/// Persists across projects. Chrome is a locked Unity Dark look; this file stores
/// preferences, shortcuts, and window placement.
/// </summary>
public class EditorSettings
{
    private static EditorSettings? _instance;
    public static EditorSettings Instance => _instance ??= Load();

    private static readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XEngine", "EditorSettings.json");

    // Preferences
    public string DefaultProjectsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XEngineProjects");
    public string Locale { get; set; } = "en";
    public bool AutoSaveLayout { get; set; } = true;
    public bool ReimportOnFocusOnly { get; set; } = true;
    public int ThumbnailSize { get; set; } = 32;

    /// <summary>Editor on-demand rendering: present only when a dirty reason (input, scene,
    /// selection, camera…) invalidates the frame. Off (default) = classic behavior — every
    /// frame renders and presents, so live content changes (e.g. Inspector transform edits)
    /// are always visible immediately.</summary>
    public bool OnDemandRendering { get; set; }

    /// <summary>Per-frame time budget in ms for asset work slices: the frame-sliced project-open
    /// import pass and the watcher's file-change batch each process assets until the budget is
    /// spent and continue next frame, so a big import never freezes the editor window.</summary>
    public int AssetWorkSliceBudgetMs { get; set; } = 12;

    /// <summary>Editor interface scale multiplier on top of the OS DPI content scale
    /// (applied to <see cref="EditorTheme.UserScale"/>). 1 = only OS DPI scaling.</summary>
    public float UiScale { get; set; } = 1f;

    public string AndroidToolchainUnitySourceRoot { get; set; } =
        @"F:\Unity\Unity2022.3.62f2_DS_20251014_Full\Editor\Data\PlaybackEngines\AndroidPlayer";

    public string AndroidToolchainCacheRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XEngine", "AndroidToolchains");

    public string AndroidToolchainResolvedPath { get; set; } = string.Empty;

    public string AndroidDotNetRoot { get; set; } = string.Empty;

    public string AndroidJavaSdkRoot { get; set; } = string.Empty;

    public string AndroidSdkRoot { get; set; } = string.Empty;

    public string AndroidNdkRoot { get; set; } = string.Empty;

    public string AndroidBundletoolPath { get; set; } = string.Empty;

    public string AndroidToolchainLockPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XEngine", "AndroidToolchains", "android-toolchain-lock.json");

    public string HarmonyOSSdkBundleSourcePath { get; set; } = string.Empty;

    public string HarmonyOSOfficialSdkRoot { get; set; } = string.Empty;

    public string HarmonyOSLastResolvedLockPath { get; set; } = string.Empty;

    /// <summary>User-selected root for managed platform SDK downloads (launcher Settings →
    /// General). Empty = the per-user default under %LOCALAPPDATA%. Big toolchains — Android
    /// JDK/SDK/NDK, the OpenHarmony SDK and its resumable download staging — land here, so users
    /// short on system-drive space can point them at another drive. Existing installs are not
    /// migrated.</summary>
    public string PlatformSdkCacheRoot { get; set; } = string.Empty;

    /// <summary>Preferred graphics backend name ("auto", "metal", "opengl", "vulkan", "d3d12", "null").
    /// Applied on next editor restart. "auto" selects Metal on macOS and Vulkan on other desktop hosts.
    /// Stored as a string so unknown/future values deserialize gracefully.</summary>
    public string PreferredGraphicsBackend { get; set; } = "auto";

    /// <summary>Which application opens source files ("auto", "system", "vscode", "rider", "vs",
    /// "custom"). Stored as a string so unknown/future values deserialize gracefully.</summary>
    public string ExternalCodeEditor { get; set; } = "auto";

    /// <summary>Executable used when <see cref="ExternalCodeEditor"/> is "custom".</summary>
    public string ExternalCodeEditorCustomPath { get; set; } = "";

    /// <summary>Argument template for the custom editor; {file} {line} {column} are substituted.</summary>
    public string ExternalCodeEditorCustomArgs { get; set; } = "\"{file}\"";

    public int WindowX { get; set; } = -1;

    public int WindowY { get; set; } = -1;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 800;

    public bool WindowMaximized { get; set; } = false;

    // Shortcuts only user-overridden bindings are stored
    public Dictionary<string, ShortcutBinding> ShortcutOverrides { get; set; } = new();

    // IDs of interactive guides/tutorials the user has already completed or skipped.
    public List<string> SeenGuides { get; set; } = new();

    /// <summary>Last Model/Prefab GUID dropped onto the Inspector animation preview (Unity last-used avatar).</summary>
    public string LastAnimationPreviewAvatarGuid { get; set; } = "";

    /// <summary>Animator window left-sidebar width (M27, persisted across sessions).</summary>
    public float AnimatorSidebarWidth { get; set; } = 190f;

    /// <summary>Animator window bottom-inspector height (M27, persisted across sessions).</summary>
    public float AnimatorInspectorHeight { get; set; } = 140f;

    // Bug Reporter (Help/Report a Bug). The SMTP account sends reports to the engine
    // author's mailbox. Defaults target Outlook/live.com STARTTLS; System.Net.Mail only
    // supports explicit TLS, so use port 587 (not 465). Store an app password, not the
    // account's main password — this file is plain JSON in AppData.
    public string BugReportSmtpHost { get; set; } = "smtp-mail.outlook.com";
    public int BugReportSmtpPort { get; set; } = 587;
    public bool BugReportSmtpEnableSsl { get; set; } = true;
    public string BugReportSmtpUserName { get; set; } = "";
    /// <summary>App password / authorization token for the SMTP account (stored locally, never committed).</summary>
    public string BugReportSmtpPassword { get; set; } = "";
    /// <summary>Sender address; empty falls back to the SMTP user name.</summary>
    public string BugReportFromAddress { get; set; } = "";
    public string BugReportToAddress { get; set; } = "flamesky@live.com";
    /// <summary>Reporter's reply-to address, remembered from the last sent report.</summary>
    public string BugReportUserEmail { get; set; } = "";

    /// <summary>Animation window property-list width (M28).</summary>
    public float AnimationSidebarWidth { get; set; } = 190f;

    /// <summary>When the Animation window shows curves, fraction of the sheet given to the dopesheet strip (M28).</summary>
    public float AnimationCurveSplit { get; set; } = 0.35f;

    // Theme
    public EditorThemeData Theme { get; set; } = EditorThemeData.CreateDefault();

    // Added after the original theme schema. Null means this file predates the explicit policy;
    // ApplyTheme then derives a policy from the legacy effect fields without changing them.
    public EditorPerformanceSettings? Performance { get; set; }

    public EditorPerformanceSettings EffectivePerformance =>
        Performance ?? EditorPerformanceSettings.FromLegacy(Theme);

    public EditorPerformanceSettings EnsurePerformance()
        => Performance ??= EditorPerformanceSettings.FromLegacy(Theme);

    /// <summary>Apply locked Unity Dark chrome. Visual customization is no longer user-editable.</summary>
    public void ApplyTheme()
    {
        Theme = EditorThemeData.CreateDefault();

        // User interface scale rides on top of the locked chrome density and applies live
        // (PreparePaperFrame re-reads EditorTheme.UserScale every render frame).
        EditorTheme.UserScale = Math.Clamp(UiScale, 0.75f, 2f);

        EditorPerformanceSettings performance = EffectivePerformance;
        performance.BackdropBlurMode = BackdropBlurMode.Off;
        performance.AnimatedBackground = false;
        performance.DropShadows = false;

        EditorTheme.PerformanceProfile = performance.Profile;
        EditorTheme.BackdropBlurMode = BackdropBlurMode.Off;
        EditorTheme.BlurAmount = 0f;
        EditorTheme.DropShadows = false;
        EditorTheme.AccentGlow = false;
        EditorTheme.AntiAliasing = performance.UiAntialiasing;
        EditorTheme.AnimatedBackground = false;
        EditorTheme.BackgroundSpeed = 1f;
        EditorTheme.BackgroundStyle = EditorBackgroundStyle.Color;
        EditorTheme.BackgroundColorA = ColorRamp.ParseHex("#383838");
        EditorTheme.BackgroundColorB = ColorRamp.ParseHex("#383838");
        EditorTheme.BgShowGradients = false;
        EditorTheme.BgShowStars = false;
        EditorTheme.BgShowComets = false;
        EditorTheme.BackgroundVoidColor = ColorRamp.ParseHex("#191919");

        EditorTheme.SyncOrigami(0f);
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to save editor settings: {ex.Message}");
        }
    }

    private static EditorSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize<EditorSettings>(json);
                if (settings != null)
                {
                    settings.Theme.InitRamps();
                    settings.ApplyTheme();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to load editor settings: {ex.Message}");
        }

        var def = new EditorSettings();
        def.ApplyTheme();
        return def;
    }

    public void ResetTheme()
    {
        Theme = EditorThemeData.CreateDefault();
        Performance = EditorPerformanceSettings.CreateDefault();
        ApplyTheme();
        Save();
    }
}
