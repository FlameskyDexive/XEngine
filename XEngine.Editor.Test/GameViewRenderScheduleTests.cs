// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class GameViewRenderScheduleTests
{
    [Fact]
    public void EditMode_RendersOnlyForInvalidationSignals()
    {
        var schedule = new GameViewRenderSchedule();
        var scene = new object();

        Assert.True(schedule.ShouldRender(false, scene, 0, 0, 1280, 720));
        Assert.False(schedule.ShouldRender(false, scene, 0, 0, 1280, 720));
        Assert.True(schedule.ShouldRender(false, scene, 1, 0, 1280, 720));
        Assert.False(schedule.ShouldRender(false, scene, 1, 0, 1280, 720));
        Assert.True(schedule.ShouldRender(false, scene, 1, 1, 1280, 720));
        Assert.False(schedule.ShouldRender(false, scene, 1, 1, 1280, 720));
        Assert.True(schedule.ShouldRender(false, scene, 1, 1, 1920, 1080));
        Assert.False(schedule.ShouldRender(false, scene, 1, 1, 1920, 1080));
        Assert.True(schedule.ShouldRender(false, scene, 1, 1, 1920, 1080, 1));
        Assert.False(schedule.ShouldRender(false, scene, 1, 1, 1920, 1080, 1));
        var otherScene = new object();
        Assert.True(schedule.ShouldRender(false, otherScene, 1, 1, 1920, 1080));
        Assert.False(schedule.ShouldRender(false, otherScene, 1, 1, 1920, 1080));
    }

    [Fact]
    public void PlayModeAndExplicitInvalidation_AlwaysRequestRender()
    {
        var schedule = new GameViewRenderSchedule();
        var scene = new object();

        Assert.True(schedule.ShouldRender(true, scene, 0, 0, 640, 480));
        Assert.True(schedule.ShouldRender(true, scene, 0, 0, 640, 480));
        Assert.True(schedule.ShouldRender(false, scene, 0, 0, 640, 480));
        Assert.False(schedule.ShouldRender(false, scene, 0, 0, 640, 480));

        schedule.Invalidate();

        Assert.True(schedule.ShouldRender(false, scene, 0, 0, 640, 480));
    }

    [Fact]
    public void SelectionChangeVersion_AdvancesForEverySelectionMutation()
    {
        long before = Selection.ChangeVersion;

        Selection.Clear();
        long first = Selection.ChangeVersion;
        Selection.Clear();
        long second = Selection.ChangeVersion;

        Assert.True(first > before);
        Assert.True(second > first);
    }

    [Fact]
    public void EditorSceneDirtyVersion_AdvancesForEveryMutation()
    {
        long before = EditorSceneManager.ChangeVersion;

        EditorSceneManager.MarkDirty();
        long first = EditorSceneManager.ChangeVersion;
        EditorSceneManager.MarkDirty();
        long second = EditorSceneManager.ChangeVersion;
        EditorSceneManager.IsDirty = false;

        Assert.True(first > before);
        Assert.True(second > first);
    }

    [Fact]
    public void DisposedRenderTexture_IsIgnoredByDeferredGameViewDraw()
    {
        var renderTexture = new RenderTexture();
        renderTexture.Dispose();

        Assert.Null(GameViewPanel.GetDisplayTexture(renderTexture));
    }
}
