using System;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.Importers;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Manages scene loading, saving, and tracking for the editor.
/// </summary>
public static class EditorSceneManager
{
    /// <summary>Path to the currently open scene file (relative to Assets/). Null for unsaved scenes.</summary>
    public static string? CurrentScenePath { get; internal set; }

    private static string? s_suppressWatcherImportPath;
    private static long s_suppressWatcherImportUntilMs;

    /// <summary>Whether the current scene has unsaved changes.</summary>
    private static bool s_isDirty;
    private static long s_changeVersion;

    public static bool IsDirty
    {
        get => s_isDirty;
        set
        {
            s_isDirty = value;
            if (value)
            {
                s_changeVersion++;
                // Scene edits also affect idle/detached Inspectors and Hierarchies. Input
                // only wakes its own surface, and an already-dirty scene can change again.
                EditorApplication.Instance?.InvalidateSurface(EditorSurfaceDirtyReason.ExplicitRepaint);
            }
        }
    }

    /// <summary>Monotonic editor-scene version used by cached preview views.</summary>
    public static long ChangeVersion => s_changeVersion;

    /// <summary>Marks the current scene as having unsaved changes (no explicit reason).</summary>
    public static void MarkDirty() => IsDirty = true;

    /// <summary>
    /// ★ Layer 0 (A0-cont) — Marks the scene dirty AND stamps its <see cref="Scene.SceneProvenance"/>
    /// with the reason/source of the change. The gizmo/inspector path passes a human label; AI write
    /// tools (B2) pass their tool name + intent. Keeping the no-arg overload preserves every existing
    /// call site unchanged.
    /// </summary>
    public static void MarkDirty(string? reason, string source = "editor")
    {
        IsDirty = true;
        if (Scene.Current is Scene scene)
        {
            scene.Provenance.Intent = reason;
            scene.Provenance.Source = source;
            scene.Provenance.LastEditAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        }
    }

    /// <summary>Fired after the scene is saved. Use for auto-saving dependent assets.</summary>
    public static event Action? OnSceneSaved;

    [OnAssemblyUnload]
    internal static void ClearSceneSavedEvent() => OnSceneSaved = null;

    /// <summary>
    /// Create and load a new empty default scene.
    /// </summary>
    public static void NewScene()
    {
        if (Application.IsPlaying) { Debug.LogWarning("Cannot create new scene during play mode."); return; }
        SceneViewPanel.CreateAndLoadDefaultScene();
        CurrentScenePath = null;
        IsDirty = false;
        Undo.Clear();
        Selection.Clear(); // drop references to the now-unloaded scene's objects
        SaveLastScenePath(null);
        SceneVisibilityManager.BindScene(null);
    }

    /// <summary>
    /// Open a scene from a project-relative path.
    /// </summary>
    public static bool OpenScene(string relativePath)
    {
        if (Application.IsPlaying) { Debug.LogWarning("Cannot open scenes during play mode."); return false; }
        if (Project.Current == null) return false;

        // Normalize Windows-authored separators so scene paths round-trip identically on
        // macOS/Linux (Path.Combine keeps '\' as a literal character in the file name there).
        relativePath = relativePath.Replace('\\', '/');
        string absolutePath = Path.Combine(Project.Current.AssetsPath, relativePath);
        if (!File.Exists(absolutePath))
        {
            Debug.LogError($"Scene file not found: {absolutePath}");
            return false;
        }

        try
        {
            string text = File.ReadAllText(absolutePath);
            var echo = EchoObject.ReadFromString(text);

            var ctx = ImportHelper.CreateTrackingContext();
            var scene = Serializer.Deserialize<Scene>(echo, ctx);

            if (scene == null)
            {
                Debug.LogError($"Failed to deserialize scene: {relativePath}");
                return false;
            }

            scene.Name = Path.GetFileNameWithoutExtension(relativePath);
            Scene.Load(scene);
            EditorAssetBackend.Instance?.PrepareSceneAnimationAssets(scene);
            CurrentScenePath = NormalizeScenePath(relativePath);
            IsDirty = false;
            Undo.Clear();
            Selection.Clear(); // drop references to the previous scene's (now disposed) objects
            SceneVisibilityManager.BindScene(relativePath);

            SaveLastScenePath(relativePath);
            Debug.Log($"Opened scene: {relativePath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to open scene: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Save the current scene to its existing path. Returns false if no path set (use SaveAs).
    /// </summary>
    public static bool Save()
    {
        if (Application.IsPlaying) { Debug.LogWarning("Cannot save scenes during play mode."); return false; }
        if (CurrentScenePath == null) return false;
        return SaveTo(CurrentScenePath);
    }

    /// <summary>Save the current scene to a specific path.</summary>
    public static bool SaveAs(string relativePath)
    {
        if (SaveTo(relativePath))
        {
            CurrentScenePath = NormalizeScenePath(relativePath);
            SaveLastScenePath(relativePath);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ensure a scene is loaded. If Scene.Current is null, restore the last scene or create a default.
    /// Called after project open.
    /// </summary>
    public static void EnsureSceneLoaded()
    {
        if (Scene.Current != null) return;

        // Try to restore the last scene from the Library cache (git-ignored, per-checkout).
        string? lastScene = LoadLastScenePath();
        if (!string.IsNullOrEmpty(lastScene))
        {
            if (OpenScene(lastScene))
                return;

            // Path was invalid — drop the stale cache entry so we stop retrying on every launch.
            ClearLastSceneCache();
        }

        // No saved scene or failed to load create default
        NewScene();
    }

    /// <summary>
    /// Handle double-clicking an asset in the project panel. Dispatches to a handler
    /// registered via <see cref="AssetDoubleClickHandlerAttribute"/>. Returns true if the
    /// asset was handled.
    /// </summary>
    public static bool HandleAssetDoubleClick(string relativePath, Guid guid)
        => EditorRegistries.DispatchDoubleClick(relativePath, guid);

    [AssetDoubleClickHandler(".scene")]
    private static bool OpenSceneHandler(string relativePath, Guid guid) => OpenScene(relativePath);

    [AssetDoubleClickHandler(".prefab")]
    private static bool OpenPrefabHandler(string relativePath, Guid guid)
    {
        PrefabEditingMode.Enter(guid);
        return true;
    }

    private static bool SaveTo(string relativePath)
    {
        if (Project.Current == null || Scene.Current == null) return false;
        Undo.CommitNumericEdits();

        string absolutePath = Path.Combine(Project.Current.AssetsPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        try
        {
            var echo = Serializer.Serialize(typeof(object), Scene.Current);
            if (echo == null)
            {
                Debug.LogError("Failed to serialize scene.");
                return false;
            }

            File.WriteAllText(absolutePath, echo.WriteToString());

            // Ensure .meta exists
            MetaFile.EnsureMeta(absolutePath, "SceneImporter");

            // The live scene is the source of truth we just wrote. The asset watcher
            // would otherwise Dispose+re-deserialize that same file on the next
            // ProcessFileChanges tick (and rewrite metadata.db).
            SuppressWatcherImport(relativePath);

            Scene.Current.Name = Path.GetFileNameWithoutExtension(relativePath);
            IsDirty = false;

            // Notify listeners (e.g. terrain editor saves TerrainData assets)
            OnSceneSaved?.Invoke();

            Debug.Log($"Saved scene: {relativePath}");
            // Label reported via SaveManager.OnSave handler
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to save scene: {ex.Message}");
            return false;
        }
    }

    private static void SaveLastScenePath(string? path)
    {
        try
        {
            var cache = LastSceneCachePath;
            if (cache == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            // Normalize separators so a Windows-style path handed in through OpenScene/SaveAs
            // persists identically to the forward-slash form the restore path compares against.
            string normalized = path is null ? "" : path.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0)
                File.Delete(cache);
            else
                File.WriteAllText(cache, normalized);
        }
        catch { }
    }

    /// <summary>
    /// <see cref="Project.LibraryPath"/>-backed cache of the last opened scene's project-relative
    /// path. Session state — deliberately not a project setting (it churns ProjectSettings/General.yaml
    /// on every scene open/save and its value is meaningless outside this checkout).
    /// </summary>
    private static string? LastSceneCachePath
        => Project.Current == null ? null : Path.Combine(Project.Current.LibraryPath, "LastScene.txt");

    private static string? LoadLastScenePath()
    {
        try
        {
            var cache = LastSceneCachePath;
            if (cache == null || !File.Exists(cache)) return null;
            string text = File.ReadAllText(cache).Trim();
            return text.Length > 0 ? text : null;
        }
        catch { return null; }
    }

    private static void ClearLastSceneCache()
    {
        try
        {
            var cache = LastSceneCachePath;
            if (cache != null && File.Exists(cache)) File.Delete(cache);
        }
        catch { }
    }

    /// <summary>Ignore asset-watcher imports of <paramref name="relativePath"/> for a short window.</summary>
    public static void SuppressWatcherImport(string relativePath)
    {
        s_suppressWatcherImportPath = NormalizeScenePath(relativePath);
        s_suppressWatcherImportUntilMs = Environment.TickCount64 + 2000;
    }

    /// <summary>True when the watcher is looking at the scene file the editor just wrote.</summary>
    public static bool ShouldSkipWatcherImport(string relativePath)
    {
        if (s_suppressWatcherImportPath == null) return false;
        if (Environment.TickCount64 > s_suppressWatcherImportUntilMs)
        {
            s_suppressWatcherImportPath = null;
            return false;
        }

        return string.Equals(NormalizeScenePath(relativePath), s_suppressWatcherImportPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Test hook so static suppress state cannot leak across cases.</summary>
    public static void ClearWatcherImportSuppress()
    {
        s_suppressWatcherImportPath = null;
        s_suppressWatcherImportUntilMs = 0;
    }

    private static string NormalizeScenePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
