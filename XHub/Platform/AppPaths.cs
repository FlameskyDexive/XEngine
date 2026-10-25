using System;
using System.IO;

namespace XHub.Platform;

/// <summary>
/// Resolves XHub's on-disk locations. Windows stores everything under
/// <c>%LOCALAPPDATA%\XEngine\XHub</c>; macOS and Linux follow the per-OS user application
/// data convention (planning doc §4.1). Overridable via <c>--data-dir</c> so tests and
/// screenshot runs never touch a real user profile.
/// </summary>
public static class AppPaths
{
    private static string? _root;
    private static string? _editorsRootOverride;
    private static string? _downloadsRootOverride;

    /// <summary>XHub data root, e.g. <c>%LOCALAPPDATA%\XEngine\XHub</c>.</summary>
    public static string Root => _root ??= ResolveDefaultRoot();

    public static string LogsDir => Path.Combine(Root, "logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ProjectsFile => Path.Combine(Root, "projects.json");
    public static string InstallationsFile => Path.Combine(Root, "installations.json");
    public static string OperationsFile => Path.Combine(Root, "operations.json");

    /// <summary>Download cache for editor packages (partial files live here, §5).</summary>
    public static string DownloadCache =>
        _downloadsRootOverride ?? Path.Combine(Root, "downloads");

    /// <summary>Root of hub-managed editor installs — uninstall ownership is validated against it.</summary>
    public static string EditorsRoot =>
        _editorsRootOverride ?? Path.Combine(Root, "editors");

    /// <summary>Default editors root — the ownership check covers it even when a custom root is
    /// configured, so installs registered before the change stay uninstallable afterwards.</summary>
    public static string DefaultEditorsRoot => Path.Combine(Root, "editors");

    /// <summary>
    /// Applies the user-configured install locations (settings §4.1). Call once at startup after
    /// settings load and before any service consumes <see cref="EditorsRoot"/> or
    /// <see cref="DownloadCache"/>. Empty values keep the defaults; configured paths are
    /// normalized to absolute form and created on demand.
    /// </summary>
    public static void ConfigureLocationOverrides(string? editorsRoot, string? downloadsRoot)
    {
        _editorsRootOverride = NormalizeOverride(editorsRoot);
        _downloadsRootOverride = NormalizeOverride(downloadsRoot);
    }

    private static string? NormalizeOverride(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            Directory.CreateDirectory(full);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Hub-install ownership: under the configured editors root OR the default one, so installs
    /// registered before a location change remain uninstallable after it.
    /// </summary>
    public static bool IsUnderEditorsRoot(string fullPath)
    {
        return IsUnder(fullPath, EditorsRoot) || IsUnder(fullPath, DefaultEditorsRoot);
    }

    private static bool IsUnder(string fullPath, string root)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The editor's roaming recent-projects list, the one-time migration source (§4.1). When a
    /// data-dir override is active the file is resolved inside the sandbox, so acceptance runs
    /// never read real user data.
    /// </summary>
    public static string LegacyRecentFile =>
        IsOverridden
            ? Path.Combine(Root, "legacy", "RecentProjects.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XEngine", "RecentProjects.json");

    public static bool IsOverridden => _root != null;

    /// <summary>Test/CLI seam: pin every path under one directory. Must run before first use.</summary>
    public static void Initialize(string? overrideRoot)
    {
        if (_root != null)
            throw new InvalidOperationException("AppPaths is already initialized.");
        if (string.IsNullOrWhiteSpace(overrideRoot))
            return;
        _root = Path.GetFullPath(overrideRoot);
        Directory.CreateDirectory(_root);
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }

    private static string ResolveDefaultRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XEngine", "XHub");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "XEngine", "XHub");

        // Linux and everything else: XDG_DATA_HOME or ~/.local/share.
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        return Path.Combine(dataHome, "XEngine", "XHub");
    }
}
