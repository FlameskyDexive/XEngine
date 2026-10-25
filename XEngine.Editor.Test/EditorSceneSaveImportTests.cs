// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI.SceneView;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorSceneSaveImportTests : IDisposable
{
    public EditorSceneSaveImportTests() => EditorSceneManager.ClearWatcherImportSuppress();

    public void Dispose() => EditorSceneManager.ClearWatcherImportSuppress();

    [Fact]
    public void ShouldSkipWatcherImport_AfterSuppress_MatchesNormalizedPath()
    {
        EditorSceneManager.SuppressWatcherImport(@"Scenes\Empty.scene");

        Assert.True(EditorSceneManager.ShouldSkipWatcherImport("Scenes/Empty.scene"));
        Assert.True(EditorSceneManager.ShouldSkipWatcherImport(@"Scenes\Empty.scene"));
        Assert.True(EditorSceneManager.ShouldSkipWatcherImport("/Scenes/Empty.scene"));
        Assert.False(EditorSceneManager.ShouldSkipWatcherImport("Scenes/Other.scene"));
    }

    [Fact]
    public void ClearWatcherImportSuppress_StopsSkipping()
    {
        EditorSceneManager.SuppressWatcherImport("Scenes/Empty.scene");
        Assert.True(EditorSceneManager.ShouldSkipWatcherImport("Scenes/Empty.scene"));

        EditorSceneManager.ClearWatcherImportSuppress();
        Assert.False(EditorSceneManager.ShouldSkipWatcherImport("Scenes/Empty.scene"));
    }
}
