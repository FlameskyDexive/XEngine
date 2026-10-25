using System;
using System.IO;
using System.Runtime.InteropServices;
using XHub.Platform;
using Xunit;

namespace XHub.Test;

[Collection("Avalonia")]
public class AppPathsTests : IDisposable
{
    private readonly string _dir;

    public AppPathsTests()
    {
        // AppPaths is process-global: force-reset before pinning (a prior class may have left it).
        typeof(AppPaths)
            .GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, null);
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Initialize_OverrideRootsAllPaths()
    {
        AppPaths.Initialize(_dir);
        try
        {
            AppPaths.EnsureDirectories();
            Assert.StartsWith(_dir, AppPaths.Root, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(AppPaths.LogsDir));
            Assert.Equal(Path.Combine(_dir, "projects.json"), AppPaths.ProjectsFile);
            Assert.Equal(Path.Combine(_dir, "installations.json"), AppPaths.InstallationsFile);
            Assert.Equal(Path.Combine(_dir, "settings.json"), AppPaths.SettingsFile);
            Assert.Equal(Path.Combine(_dir, "operations.json"), AppPaths.OperationsFile);
            Assert.True(AppPaths.IsOverridden);
        }
        finally
        {
            ResetPaths();
        }
    }

    [Fact]
    public void Initialize_Twice_Throws()
    {
        AppPaths.Initialize(_dir);
        try
        {
            Assert.Throws<InvalidOperationException>(() => AppPaths.Initialize(_dir + "2"));
        }
        finally
        {
            ResetPaths();
        }
    }

    [Fact]
    public void ConfigureLocationOverrides_CustomRoots_UsedAndOwned()
    {
        AppPaths.Initialize(_dir);
        string editors = Path.Combine(_dir, "custom-editors");
        string downloads = Path.Combine(_dir, "custom-downloads");
        try
        {
            AppPaths.ConfigureLocationOverrides(editors, downloads);

            Assert.Equal(editors, AppPaths.EditorsRoot);
            Assert.Equal(downloads, AppPaths.DownloadCache);
            Assert.True(Directory.Exists(editors)); // created on apply
            Assert.True(Directory.Exists(downloads));

            // Ownership: install under either the configured or the default editors root.
            Assert.True(AppPaths.IsUnderEditorsRoot(Path.Combine(editors, "1.0.0-a1-x")));
            Assert.True(AppPaths.IsUnderEditorsRoot(Path.Combine(AppPaths.DefaultEditorsRoot, "1.0.0-a1-x")));
            Assert.False(AppPaths.IsUnderEditorsRoot(Path.Combine(_dir, "elsewhere")));
        }
        finally
        {
            ResetLocationOverrides();
        }
    }

    [Fact]
    public void ConfigureLocationOverrides_Empty_KeepsDefaults()
    {
        AppPaths.Initialize(_dir);
        try
        {
            AppPaths.ConfigureLocationOverrides("", null);

            Assert.Equal(Path.Combine(_dir, "editors"), AppPaths.EditorsRoot);
            Assert.Equal(Path.Combine(_dir, "downloads"), AppPaths.DownloadCache);
        }
        finally
        {
            ResetLocationOverrides();
        }
    }

    private static void ResetLocationOverrides()
    {
        var type = typeof(AppPaths);
        type.GetField("_editorsRootOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, null);
        type.GetField("_downloadsRootOverride", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, null);
    }

    private static void ResetPaths()
    {
        // AppPaths has no public reset; tests that pinned the root must not leak. Use reflection
        // against the internal field — the test assembly runs in the same process as XHub.dll.
        typeof(AppPaths)
            .GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, null);
    }
}
