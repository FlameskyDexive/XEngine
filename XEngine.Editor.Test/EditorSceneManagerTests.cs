// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Tests that the editor reopens the last-opened scene on startup. The path is cached in the
/// git-ignored <c>Library/LastScene.txt</c> of the project (session state, not a setting).
/// </summary>
public class EditorSceneManagerTests : EditorTestHarness
{
    public EditorSceneManagerTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    private string CacheFile => Path.Combine(Project.LibraryPath, "LastScene.txt");

    // The exact startup sequence of a real editor relaunch: the Library cache carries the last
    // scene, script-assembly load reinitializes the registries, then EnsureSceneLoaded must
    // reopen the persisted scene instead of a fresh default one.
    [Fact]
    public void EnsureSceneLoaded_AfterRegistryReinit_ReopensLastScene()
    {
        // Session 1: author and open a scene - OpenScene persists the cache file.
        AuthorEmptyScene("Scenes/LastOpened.scene");
        Assert.True(EditorSceneManager.OpenScene("Scenes/LastOpened.scene"));
        Assert.Equal("Scenes/LastOpened.scene", EditorSceneManager.CurrentScenePath);
        Assert.Equal("Scenes/LastOpened.scene", File.ReadAllText(CacheFile));

        // Session 2: simulate the editor restarting. The OnAssemblyLoad path reinitializes the
        // registries (fresh settings instances) and no scene is loaded yet — but the Library
        // cache survives (it is outside settings).
        EditorRegistries.Reinitialize();
        Scene.Unload();
        EditorSceneManager.CurrentScenePath = null;

        EditorSceneManager.EnsureSceneLoaded();

        Assert.Equal("Scenes/LastOpened.scene", EditorSceneManager.CurrentScenePath);
        Assert.NotNull(Scene.Current);
        Assert.Equal("LastOpened", Scene.Current.Name);
    }

    // OpenScene persists the path it was given, normalized to forward slashes so a scene opened
    // through a Windows path round-trips identically on the next launch.
    [Fact]
    public void OpenScene_PersistsLastScenePath_InLibraryCache()
    {
        AuthorEmptyScene("Scenes/Norm.scene");
        Assert.True(EditorSceneManager.OpenScene(@"Scenes\Norm.scene"));

        Assert.Equal("Scenes/Norm.scene", EditorSceneManager.CurrentScenePath);
        Assert.Equal("Scenes/Norm.scene", File.ReadAllText(CacheFile));
        Assert.False(File.Exists(Path.Combine(Project.ProjectSettingsPath, "General.yaml")) &&
                     File.ReadAllText(Path.Combine(Project.ProjectSettingsPath, "General.yaml"))
                         .Contains("LastScene"),
            "scene path must not leak into project settings");
    }

    // NewScene is an explicit user action - it must clear the remembered path so the next launch
    // starts fresh rather than resurrecting the scene the user just closed.
    [Fact]
    public void NewScene_ClearsLastScenePath()
    {
        AuthorEmptyScene("Scenes/ForgetMe.scene");
        Assert.True(EditorSceneManager.OpenScene("Scenes/ForgetMe.scene"));
        Assert.True(File.Exists(CacheFile));

        EditorSceneManager.NewScene();

        Assert.False(File.Exists(CacheFile));
    }

    // A stale cache entry (folder renamed/deleted while closed) is dropped instead of retrying
    // forever; EnsureSceneLoaded falls back to creating the default scene.
    [Fact]
    public void StaleCacheEntry_IsCleared_AndDefaultSceneCreated()
    {
        AuthorEmptyScene("Scenes/Gone.scene");
        Assert.True(EditorSceneManager.OpenScene("Scenes/Gone.scene"));
        Directory.Delete(Path.Combine(Project.AssetsPath, "Scenes"), recursive: true);

        Scene.Unload();
        EditorSceneManager.CurrentScenePath = null;
        EditorSceneManager.EnsureSceneLoaded();

        Assert.False(File.Exists(CacheFile));
        Assert.NotNull(Scene.Current); // default fallback scene
    }
}
