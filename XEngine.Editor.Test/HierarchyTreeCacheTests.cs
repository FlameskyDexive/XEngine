using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HierarchyTreeCacheTests
{
    [Fact]
    public void IndexedTreeKeyKeepsSelectionSeparateFromModelInvalidation()
    {
        Selection.Clear();
        var initial = HierarchyPanel.ComputeTreeKey("", true, selectionDependent: false);
        try
        {
            Selection.Select(new object());
            Assert.Equal(initial, HierarchyPanel.ComputeTreeKey("", true, selectionDependent: false));
            EditorSceneManager.MarkDirty();
            Assert.NotEqual(initial, HierarchyPanel.ComputeTreeKey("", true, selectionDependent: false));
        }
        finally { Selection.Clear(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PanelCleanupDropsActiveAndPooledModelReferences(bool close)
    {
        var panel = new HierarchyPanel();
        using var go = new GameObject("Cached");
        var node = new XEngine.OrigamiUI.TreeNode { UserData = go };
        foreach (string name in new[] { "_treeNodes", "_treeNodePool" })
            ((System.Collections.IList)Field(name).GetValue(panel)!).Add(node);
        foreach (string name in new[] { "_flatObjects", "_displayRoots" })
            ((System.Collections.IList)Field(name).GetValue(panel)!).Add(go);
        Field("_treeCacheKey").SetValue(panel, new HierarchyTreeKey(new Scene(), 1, 2, 3, "", false, 0, Guid.Empty, true, false, 0));
        Field("_hasTreeCache").SetValue(panel, true);

        if (close) panel.OnClosed(); else panel.OnScriptReloadCleanup();

        foreach (string name in new[] { "_treeNodes", "_treeNodePool", "_flatObjects", "_displayRoots" })
            Assert.Empty((System.Collections.IList)Field(name).GetValue(panel)!);
        Assert.Equal(default(HierarchyTreeKey), (HierarchyTreeKey)Field("_treeCacheKey").GetValue(panel)!);
        Assert.False((bool)Field("_hasTreeCache").GetValue(panel)!);

        static System.Reflection.FieldInfo Field(string name) => typeof(HierarchyPanel).GetField(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    }

    [Fact]
    public void TreeKey_DistinguishesPreviouslyCollidingPingAndCutValues()
    {
        var ping = new Guid(1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(Guid.Empty.GetHashCode(), ping.GetHashCode());
        var baseline = new HierarchyTreeKey(null, 0, 0, 0, "", false, 0, Guid.Empty, true, false, 0);
        Assert.NotEqual(baseline, new HierarchyTreeKey(null, 0, 0, 0, "", false, 0, ping, true, false, 0));
        // The old cutVersion << 50 stamp discarded the upper 18 bits.
        Assert.Equal(0L, (long)(1 << 14) << 50);
        Assert.NotEqual(baseline, new HierarchyTreeKey(null, 0, 0, 0, "", false, 0, Guid.Empty, true, false, 1 << 14));
    }

    [Fact]
    public void TreeKey_UsesSceneIdentityAndSearchContentAndSortMode()
    {
        var sceneA = new Scene(); var sceneB = new Scene();
        var a = new HierarchyTreeKey(sceneA, 1, 2, 3, "cube", false, 4, Guid.Empty, true, false, 5);
        var same = new HierarchyTreeKey(sceneA, 1, 2, 3, new string(['c', 'u', 'b', 'e']), false, 4, Guid.Empty, true, false, 5);
        Assert.Equal(a, same);
        Assert.NotEqual(a, new HierarchyTreeKey(sceneB, 1, 2, 3, "cube", false, 4, Guid.Empty, true, false, 5));
        Assert.NotEqual(a, new HierarchyTreeKey(sceneA, 1, 2, 3, "Cube", false, 4, Guid.Empty, true, false, 5));
        Assert.NotEqual(a, new HierarchyTreeKey(sceneA, 1, 2, 3, "cube", false, 4, Guid.Empty, true, true, 5));
    }

    [Fact]
    public void HasVisibleHierarchyChildren_IgnoresHiddenAndHideAndDontSave()
    {
        using var parent = new GameObject("Parent");
        using var hidden = new GameObject("Hidden");
        hidden.SetParent(parent);
        hidden.HideFlags = HideFlags.Hide;
        Assert.False(HierarchyPanel.HasVisibleHierarchyChildren(parent));

        using var dontSave = new GameObject("DontSave");
        dontSave.SetParent(parent);
        dontSave.HideFlags = HideFlags.HideAndDontSave;
        Assert.False(HierarchyPanel.HasVisibleHierarchyChildren(parent));

        using var child = new GameObject("Child");
        child.SetParent(parent);
        Assert.True(HierarchyPanel.HasVisibleHierarchyChildren(parent));
    }

    [Fact]
    public void ComputeTreeKey_IsStableUntilSearchOrSceneDirtyChanges()
    {
        var a = HierarchyPanel.ComputeTreeKey("", true);
        var b = HierarchyPanel.ComputeTreeKey("", true);
        Assert.Equal(a, b);
        Assert.NotEqual(a, HierarchyPanel.ComputeTreeKey("cube", true));
        Assert.NotEqual(a, HierarchyPanel.ComputeTreeKey("", false));

        EditorSceneManager.MarkDirty();
        Assert.NotEqual(a, HierarchyPanel.ComputeTreeKey("", true));
    }

    // Regression: the stamp cache introduced in a7b613fc had no topology term, so objects created
    // without an explicit MarkDirty (scripts, MCP tools, play mode) never appeared in the tree.
    [Fact]
    public void ComputeTreeKey_ChangesWhenSceneTopologyChanges()
    {
        var scene = new Scene();
        Scene.Load(scene);
        try
        {
            var a = HierarchyPanel.ComputeTreeKey("", true);

            var go = new GameObject("Created");
            scene.Add(go);
            var afterAdd = HierarchyPanel.ComputeTreeKey("", true);
            Assert.NotEqual(a, afterAdd);

            scene.Remove(go);
            go.Dispose();
            Assert.NotEqual(afterAdd, HierarchyPanel.ComputeTreeKey("", true));
        }
        finally
        {
            Scene.Unload();
        }
    }

    // Selection drives reveal-on-selection expansion, so the stamp must change on selection
    // changes - and ONLY then (a stable selection must not rebuild the tree every frame).
    [Fact]
    public void ComputeTreeKey_ChangesOnSelectionChangeOnly()
    {
        Selection.Clear();
        var a = HierarchyPanel.ComputeTreeKey("", true);
        Assert.Equal(a, HierarchyPanel.ComputeTreeKey("", true));

        var marker = new object();
        Selection.Select(marker);
        try
        {
            var b = HierarchyPanel.ComputeTreeKey("", true);
            Assert.NotEqual(a, b);
            Assert.Equal(b, HierarchyPanel.ComputeTreeKey("", true));
        }
        finally
        {
            Selection.Clear();
        }
    }

    // Regression: CreateGameObject/DeleteGameObject never marked the scene dirty (only their
    // undo/redo lambdas did), so the hierarchy's stamp cache never saw newly created UI objects.
    [Fact]
    public void CreateAndDeleteGameObject_MarkSceneDirtyAndBumpStructureVersion()
    {
        var scene = new Scene();
        Scene.Load(scene);
        try
        {
            EditorSceneManager.IsDirty = false;
            long structureBefore = scene.StructureVersion;

            var go = HierarchyPanel.CreateGameObject("Created", parent: null, select: false, beginRename: false);
            Assert.True(EditorSceneManager.IsDirty, "CreateGameObject must mark the scene dirty.");
            Assert.True(scene.StructureVersion > structureBefore,
                "CreateGameObject must advance the scene structure version.");

            EditorSceneManager.IsDirty = false;
            HierarchyPanel.DeleteGameObject(go);
            Assert.True(EditorSceneManager.IsDirty, "DeleteGameObject must mark the scene dirty.");
        }
        finally
        {
            Undo.Clear();
            Scene.Unload();
        }
    }
}
