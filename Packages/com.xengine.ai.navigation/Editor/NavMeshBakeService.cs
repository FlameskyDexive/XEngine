using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.AI.Navigation;
using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;

namespace XEngine.AI.Navigation.Editor;

/// <summary>
/// Editor bake: collect on the main thread, Recast-build on a worker, then write
/// <c>&lt;SceneName&gt;_navmesh/</c> and assign the asset to the selected surface.
/// </summary>
public sealed class NavMeshBakeService
{
    private CancellationTokenSource? _cts;
    private Task<NavMeshData>? _task;
    private Scene? _scene;
    private NavMeshSurface? _surface;
    private Exception? _error;

    public bool IsBaking => _task != null && !_task.IsCompleted;
    public float Progress { get; private set; }
    public string Status { get; private set; } = "Idle";
    public Scene? TargetScene => _scene;

    public bool Start(Scene scene, NavMeshSurface? surface)
    {
        if (IsBaking) return false;
        _scene = scene;
        _surface = surface;
        _error = null;
        Progress = 0.05f;
        Status = "Collecting geometry…";

        NavMeshBuildSettings settings = surface?.Settings ?? new NavMeshBuildSettings();
        NavMeshInputGeometry geom;
        try
        {
            geom = surface != null
                ? surface.CollectGeometry(scene)
                : NavMeshBuilder.Collect(scene, settings, LayerMask.Everything);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Runtime.Debug.LogError($"[NavMesh] Collect failed: {ex.Message}");
            Cleanup();
            return false;
        }

        Progress = 0.15f;
        Status = "Baking…";
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _task = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return NavMeshBuilder.Build(geom, settings);
        }, token);
        return true;
    }

    public void Poll()
    {
        if (_task == null) return;
        if (!_task.IsCompleted)
        {
            Progress = Math.Min(0.9f, Progress + 0.01f);
            return;
        }

        if (_task.IsCanceled)
        {
            Status = "Cancelled";
            Cleanup();
            return;
        }

        if (_task.IsFaulted)
        {
            _error = _task.Exception?.GetBaseException();
            Status = _error?.Message ?? "Bake failed";
            Runtime.Debug.LogError($"[NavMesh] Bake failed: {Status}");
            Cleanup();
            return;
        }

        FinalizeBake(_task.Result);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        Status = "Cancelled";
        Cleanup();
    }

    public void Clear(Scene scene, NavMeshSurface? surface)
    {
        if (IsBaking) return;
        surface?.ClearBaked();
        string? relFolder = RelativeFolder(scene);
        if (relFolder != null && Project.Current != null)
        {
            string abs = Path.Combine(Project.Current.AssetsPath, relFolder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(abs))
                Directory.Delete(abs, recursive: true);
        }

        EditorSceneManager.MarkDirty("Clear NavMesh", "editor");
        if (EditorSceneManager.CurrentScenePath != null)
            EditorSceneManager.Save();
        Status = "Cleared";
    }

    private void FinalizeBake(NavMeshData data)
    {
        try
        {
            Progress = 0.95f;
            Status = "Writing asset…";
            NavMeshSurface? surface = _surface;
            Scene? scene = _scene;
            string? rel = RelativeAssetPath(scene);
            var db = EditorAssetBackend.Instance;
            if (db != null && rel != null && Project.Current != null)
            {
                string abs = Path.Combine(Project.Current.AssetsPath, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                EchoObject echo = Serializer.Serialize(typeof(object), data);
                File.WriteAllText(abs, echo.WriteToString());
                Guid guid = db.ImportFile(rel);
                data.AssetID = guid;
                data.AssetPath = rel;
                if (surface != null)
                    surface.NavMeshData = new AssetRef<NavMeshData>(guid);
            }

            surface?.ApplyBaked(data, register: true);
            EditorSceneManager.MarkDirty("Bake NavMesh", "editor");
            if (EditorSceneManager.CurrentScenePath != null)
                EditorSceneManager.Save();
            Progress = 1f;
            Status = $"Baked {data.Tiles.Count} tile(s), {data.TileData.Length} bytes";
            Runtime.Debug.Log($"[NavMesh] {Status}");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Runtime.Debug.LogError($"[NavMesh] Finalize failed: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _task = null;
        _cts?.Dispose();
        _cts = null;
        _scene = null;
        _surface = null;
        Progress = 0f;
    }

    private static string? RelativeFolder(Scene? scene)
    {
        string? scenePath = EditorSceneManager.CurrentScenePath;
        if (string.IsNullOrEmpty(scenePath)) return null;
        string sceneDir = Path.GetDirectoryName(scenePath)?.Replace('\\', '/') ?? "";
        string sceneName = Path.GetFileNameWithoutExtension(scenePath);
        return string.IsNullOrEmpty(sceneDir) ? $"{sceneName}_navmesh" : $"{sceneDir}/{sceneName}_navmesh";
    }

    private static string? RelativeAssetPath(Scene? scene)
    {
        string? folder = RelativeFolder(scene);
        if (folder == null) return null;
        string sceneName = Path.GetFileNameWithoutExtension(EditorSceneManager.CurrentScenePath ?? scene?.Name ?? "Scene");
        return $"{folder}/{sceneName}.navmesh";
    }
}
