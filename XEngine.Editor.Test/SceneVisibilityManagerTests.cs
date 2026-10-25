using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class SceneVisibilityManagerTests : IDisposable
{
    public SceneVisibilityManagerTests() => SceneVisibilityManager.Reset();

    public void Dispose() => SceneVisibilityManager.Reset();

    [Fact]
    public void DefaultsToVisibleAndPickable()
    {
        using var go = new GameObject("Cube");
        Assert.False(SceneVisibilityManager.IsHidden(go));
        Assert.True(SceneVisibilityManager.IsVisibleInHierarchy(go));
        Assert.True(SceneVisibilityManager.IsPickingEnabled(go));
        Assert.True(SceneVisibilityManager.IsPickingEnabledInHierarchy(go));
    }

    [Fact]
    public void HideParentHidesChildrenInSceneView()
    {
        using var parent = new GameObject("Parent");
        using var child = new GameObject("Child");
        child.SetParent(parent);

        SceneVisibilityManager.SetHidden(parent, true);

        Assert.True(SceneVisibilityManager.IsHidden(parent));
        Assert.False(SceneVisibilityManager.IsHidden(child));
        Assert.False(SceneVisibilityManager.IsVisibleInHierarchy(child));
        Assert.False(SceneVisibilityManager.IsPickingEnabledInHierarchy(child));
        Assert.True(child.Enabled);
        Assert.True(parent.Enabled);
    }

    [Fact]
    public void UnpickableDoesNotHide()
    {
        using var go = new GameObject("Cube");
        SceneVisibilityManager.SetPickingEnabled(go, false);
        Assert.False(SceneVisibilityManager.IsPickingEnabled(go));
        Assert.True(SceneVisibilityManager.IsVisibleInHierarchy(go));
        Assert.False(SceneVisibilityManager.IsPickingEnabledInHierarchy(go));
    }

    [Fact]
    public void SerializeRoundtripPreservesIds()
    {
        using var hidden = new GameObject("Hidden");
        using var blocked = new GameObject("Blocked");
        SceneVisibilityManager.SetHidden(hidden, true);
        SceneVisibilityManager.SetPickingEnabled(blocked, false);

        string json = SceneVisibilityManager.SerializeState();
        SceneVisibilityManager.Reset();
        Assert.False(SceneVisibilityManager.IsHidden(hidden));

        SceneVisibilityManager.DeserializeState(json);
        Assert.True(SceneVisibilityManager.IsHidden(hidden));
        Assert.False(SceneVisibilityManager.IsPickingEnabled(blocked));
    }

    [Fact]
    public void AllowRenderCollectPassesWhenNoSceneViewCamera()
    {
        using var go = new GameObject("Cube");
        SceneVisibilityManager.SetHidden(go, true);
        using var camGo = new GameObject("Cam");
        Camera camera = camGo.AddComponent<Camera>();
        Assert.True(SceneVisibilityManager.AllowRenderCollect(go, camera));
    }
}
