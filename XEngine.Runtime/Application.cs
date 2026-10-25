// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime.Platforms.iOS;

namespace XEngine.Runtime;

/// <summary>Platform of the process currently running XEngine.</summary>
public enum ApplicationRuntimePlatform
{
    Unknown = 0,
    Windows,
    Linux,
    MacOS,
    Android,
    iOS,
    HarmonyOS,
    Browser,
}

/// <summary>
/// Provides global application state flags and standard engine path roots.
///
/// Path semantics mirror Unity's Application API:
/// <list type="bullet">
/// <item><see cref="DataPath"/> - the directory of the running executable (standalone) or project
/// root (editor).</item>
/// <item><see cref="StreamingAssetsPath"/> - read-only assets shipped inside the app package. On
/// desktop this is <c>{DataPath}/Content</c>; on Android it maps to the APK's <c>assets/</c> VFS.</item>
    /// <item><see cref="PersistentDataPath"/> - a writable directory that survives app updates. Per-OS
    /// convention (Windows %LOCALAPPDATA%, macOS ~/Library/Application Support, iOS Documents,
    /// Linux ~/.local/share).</item>
/// <item><see cref="TemporaryCachePath"/> - a writable scratch directory the OS may evict. Used for
/// transient bundle downloads.</item>
/// </list>
/// </summary>
public static class Application
{
    private static ApplicationRuntimePlatform? _runtimePlatformOverride;

    /// <summary>
    /// Platform of the current runtime process. This deliberately reports the host platform, not
    /// the editor's selected build target; use it for runtime branching in player code.
    /// </summary>
    public static ApplicationRuntimePlatform RuntimePlatform =>
        _runtimePlatformOverride ?? DetectRuntimePlatform();

    /// <summary>Called by platform player hosts when their runtime cannot be identified by .NET.</summary>
    public static void ConfigureRuntimePlatform(ApplicationRuntimePlatform platform)
    {
        if (platform == ApplicationRuntimePlatform.Unknown)
            throw new ArgumentOutOfRangeException(nameof(platform));
        _runtimePlatformOverride = platform;
    }

    /// <summary>
    /// True when the game is actively running (play mode in editor, or standalone player).
    /// </summary>
    public static bool IsPlaying { get; set; }

    /// <summary>
    /// True only in a generated standalone player process. Unlike <see cref="IsPlaying"/> (also
    /// set by Game.Run in the editor launcher) this never flips inside editor tooling, so
    /// backend code can select player-only behavior (e.g. the Vulkan positive-viewport-height
    /// convention on Intel Arc / Maleoon) without touching the editor's presentation paths.
    /// </summary>
    public static bool IsPlayerBuild { get; set; }

    /// <summary>
    /// True when running inside the editor (false in standalone builds).
    /// </summary>
    public static bool IsEditor { get; set; }

    /// <summary>Desktop player frame limit; zero or negative means unlimited.
    /// Ignored by the editor, including Play mode. Mobile/browser hosts retain platform pacing.</summary>
    public static int TargetFrameRate { get; set; }

    /// <summary>
    /// True when running without a window or graphics device (e.g. a dedicated server, or a build
    /// launched with --headless). Gameplay, physics and scripts still run; rendering does not.
    /// </summary>
    public static bool IsHeadless { get; set; }

    /// <summary>
    /// True when play mode is paused. Update/FixedUpdate/LateUpdate stop, but rendering continues.
    /// </summary>
    public static bool IsPaused { get; set; }

    /// <summary>
    /// When true, one frame of gameplay executes then IsPaused reasserts.
    /// Set by the editor Step button, consumed by the game loop.
    /// </summary>
    internal static bool StepRequested { get; set; }

    /// <summary>Whether gameplay should execute this frame (playing and not paused, or stepping).</summary>
    public static bool ShouldRunGameplay => IsPlaying && (!IsPaused || StepRequested);

    /// <summary>
    /// True while gameplay code (Update/FixedUpdate) is executing.
    /// Used by the editor's input filtering to distinguish gameplay input from editor input.
    /// </summary>
    public static bool IsGameplayExecuting { get; set; }

    // ================================================================
    //  Paths
    // ================================================================

    private static string? _dataPath;
    private static string? _streamingAssetsPath;
    private static string? _persistentDataPathOverride;
    private static string? _temporaryCachePathOverride;
    private static string? _noBackupDataPathOverride;
    private static string? _noBackupDataPath;
    private static string? _productName;
    private static string? _companyName;

    /// <summary>
    /// Directory containing the running executable (standalone) or project root (editor).
    /// Used by PlayerAssetBackend to locate assets relative to the executable.
    /// </summary>
    public static string DataPath
    {
        get => _dataPath ??= System.AppContext.BaseDirectory;
        set => _dataPath = value;
    }

    /// <summary>
    /// The product (game) name, used to derive per-app subdirectory names under
    /// <see cref="PersistentDataPath"/>. Set at player startup from exported GeneralSettings; defaults
    /// to "XEngine" when unset.
    /// </summary>
    public static string ProductName
    {
        get => _productName ??= "XEngine";
        set { _productName = value; _persistentDataPath = null; _temporaryCachePath = null; _noBackupDataPath = null; }
    }

    /// <summary>
    /// The company/developer name, used alongside <see cref="ProductName"/> for the persistent-data
    /// subdirectory on platforms that nest company/product (e.g. macOS). Set at player startup.
    /// </summary>
    public static string CompanyName
    {
        get => _companyName ??= "XEngine";
        set { _companyName = value; _persistentDataPath = null; _temporaryCachePath = null; _noBackupDataPath = null; }
    }

    /// <summary>
    /// Read-only assets shipped inside the app package. On desktop this is <c>{DataPath}/Content</c>;
    /// the build pipeline copies loose files / paks / bundles here. On Android it conceptually maps
    /// to the APK's <c>assets/</c> VFS (the Android host translates this path).
    /// </summary>
    public static string StreamingAssetsPath => _streamingAssetsPath ?? Path.Combine(DataPath, "Content");

    private static string? _persistentDataPath;
    /// <summary>
    /// A writable directory that survives app updates and is backed up by the OS. Per-OS convention:
    /// <list type="bullet">
    /// <item>Windows: <c>%LOCALAPPDATA%\{CompanyName}\{ProductName}</c></item>
    /// <item>macOS: <c>~/Library/Application Support/{CompanyName}/{ProductName}</c></item>
    /// <item>iOS: the app <c>Documents</c> folder</item>
    /// <item>Linux: <c>~/.local/share/{CompanyName}/{ProductName}</c></item>
    /// </list>
    /// Used for downloaded bundle updates (SandboxBundleStore) and per-player save data.
    /// </summary>
    public static string PersistentDataPath
    {
        get
        {
            if (_persistentDataPathOverride != null) return _persistentDataPathOverride;
            if (_persistentDataPath != null) return _persistentDataPath;
            _persistentDataPath = ResolvePersistentDataPath();
            try { Directory.CreateDirectory(_persistentDataPath); } catch { }
            return _persistentDataPath;
        }
    }

    private static string? _temporaryCachePath;
    /// <summary>
    /// A writable scratch directory the OS may evict under disk pressure. Used for transient bundle
    /// downloads and unpacking. Per-OS: Windows %TEMP%\{CompanyName}\{ProductName}, macOS
    /// ~/Library/Caches/{CompanyName}/{ProductName}, iOS Library/Caches, Linux
    /// ~/.cache/{CompanyName}/{ProductName}.
    /// </summary>
    public static string TemporaryCachePath
    {
        get
        {
            if (_temporaryCachePathOverride != null) return _temporaryCachePathOverride;
            if (_temporaryCachePath != null) return _temporaryCachePath;
            _temporaryCachePath = ResolveTemporaryCachePath();
            try { Directory.CreateDirectory(_temporaryCachePath); } catch { }
            return _temporaryCachePath;
        }
    }

    /// <summary>Persistent app-internal data excluded from cloud backup.</summary>
    public static string NoBackupDataPath
    {
        get
        {
            if (_noBackupDataPathOverride != null) return _noBackupDataPathOverride;
            if (_noBackupDataPath != null) return _noBackupDataPath;
            _noBackupDataPath = Path.Combine(PersistentDataPath, "NoBackup");
            try { Directory.CreateDirectory(_noBackupDataPath); } catch { }
            return _noBackupDataPath;
        }
    }

    /// <summary>Configures package and sandbox paths supplied by a platform host.</summary>
    public static void ConfigurePlatformPaths(
        string dataPath,
        string streamingAssetsPath,
        string persistentDataPath,
        string temporaryCachePath,
        string noBackupDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamingAssetsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentDataPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryCachePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(noBackupDataPath);

        DataPath = Path.GetFullPath(dataPath);
        _streamingAssetsPath = streamingAssetsPath;
        _persistentDataPathOverride = Path.GetFullPath(persistentDataPath);
        _temporaryCachePathOverride = Path.GetFullPath(temporaryCachePath);
        _noBackupDataPathOverride = Path.GetFullPath(noBackupDataPath);
        Directory.CreateDirectory(_persistentDataPathOverride);
        Directory.CreateDirectory(_temporaryCachePathOverride);
        Directory.CreateDirectory(_noBackupDataPathOverride);
    }

    /// <summary>Clears host-supplied path overrides. Tests restore the process-wide Application paths.</summary>
    internal static void ResetPlatformPathOverrides()
    {
        _dataPath = null;
        _streamingAssetsPath = null;
        _persistentDataPathOverride = null;
        _temporaryCachePathOverride = null;
        _noBackupDataPathOverride = null;
        _persistentDataPath = null;
        _temporaryCachePath = null;
        _noBackupDataPath = null;
    }

    /// <summary>
    /// The console (stdout) encoding the engine initialized with. Tests / headless hosts use this to
    /// force UTF-8 for log capture.
    /// </summary>
    public static System.Text.Encoding ConsoleEncoding => Console.OutputEncoding;

    // ----------------------------------------------------------------
    //  Per-OS path resolution
    // ----------------------------------------------------------------

    private static string ResolvePersistentDataPath()
    {
        string product = SanitizePathSegment(ProductName);
        string company = SanitizePathSegment(CompanyName);

        if (OperatingSystem.IsWindows())
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, company, product);
        }
        // iOS must be classified before macOS: Catalyst reports IsMacOS() as well.
        if (OperatingSystem.IsIOS())
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                documents = Path.Combine(DataPath, "Documents");
            return IosApplicationPaths.PersistentDataPath(documents);
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", company, product);
        }
        if (OperatingSystem.IsAndroid())
        {
            // Android: the host sets DataPath to the app's files dir; per the Android platform plan,
            // persistent data lives under {files}/{ProductName}. The native host should override
            // PersistentDataPath directly via a setter once a real Android VFS lands; this is the
            // desktop-test fallback.
            return Path.Combine(DataPath, product);
        }
        // Linux and other Unix-likes.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, company, product);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", company, product);
        }
        // Unknown platform fallback: next to the executable.
        return Path.Combine(DataPath, "PersistentData", product);
    }

    private static string ResolveTemporaryCachePath()
    {
        string product = SanitizePathSegment(ProductName);
        string company = SanitizePathSegment(CompanyName);

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Path.GetTempPath(), company, product);
        }
        if (OperatingSystem.IsIOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                return Path.Combine(DataPath, "Library", "Caches");
            return IosApplicationPaths.TemporaryCachePath(Path.Combine(home, "Library", "Caches"));
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Caches", company, product);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        {
            string xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, company, product);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".cache", company, product);
        }
        if (OperatingSystem.IsAndroid())
        {
            return Path.Combine(DataPath, "cache", product);
        }
        return Path.Combine(Path.GetTempPath(), company, product);
    }

    /// <summary>Strip characters illegal in path segments (used to build per-app subdirs from the
    /// user-supplied product/company names).</summary>
    private static string SanitizePathSegment(string name)
    {
        if (string.IsNullOrEmpty(name)) return "XEngine";
        foreach (char c in Path.GetInvalidPathChars())
            name = name.Replace(c.ToString(), "");
        return name;
    }

    private static ApplicationRuntimePlatform DetectRuntimePlatform()
    {
        if (OperatingSystem.IsBrowser()) return ApplicationRuntimePlatform.Browser;
        // iOS (and iOS-on-Mac Catalyst when reported as iOS) must be classified before macOS.
        // OperatingSystem.IsMacOS() is true for Catalyst; checking Mac first would mis-report iOS.
        if (OperatingSystem.IsIOS()) return ApplicationRuntimePlatform.iOS;
        if (OperatingSystem.IsAndroid()) return ApplicationRuntimePlatform.Android;
        if (OperatingSystem.IsWindows()) return ApplicationRuntimePlatform.Windows;
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) return ApplicationRuntimePlatform.MacOS;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) return ApplicationRuntimePlatform.Linux;
        return ApplicationRuntimePlatform.Unknown;
    }
}
