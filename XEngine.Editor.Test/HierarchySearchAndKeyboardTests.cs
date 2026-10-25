using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HierarchySearchAndKeyboardTests
{
    [Fact]
    public void ParseTypeFilterAndName()
    {
        var query = HierarchySearch.Parse("t:Camera main");
        Assert.Equal("main", query.Name);
        Assert.Equal(new[] { "Camera" }, query.TypeFilters);
    }

    [Fact]
    public void MatchesCameraType()
    {
        using var cam = new GameObject("Main Camera");
        cam.AddComponent<Camera>();
        using var cube = new GameObject("Cube");
        cube.AddComponent<MeshRenderer>();

        var query = HierarchySearch.Parse("t:Camera");
        Assert.True(HierarchySearch.Matches(cam, query));
        Assert.False(HierarchySearch.Matches(cube, query));
    }

    [Fact]
    public void ParentOfTypeMatchIsKept()
    {
        using var parent = new GameObject("Group");
        using var child = new GameObject("Main Camera");
        child.AddComponent<Camera>();
        child.SetParent(parent);

        var query = HierarchySearch.Parse("t:Camera");
        Assert.False(HierarchySearch.Matches(parent, query));
        Assert.True(HierarchySearch.MatchesSelfOrDescendant(parent, query));
    }

    [Fact]
    public void VisibleIndicesSkipCollapsedChildren()
    {
        var nodes = new List<TreeNode>
        {
            new() { Id = "p", Depth = 0, HasChildren = true },
            new() { Id = "c", Depth = 1, HasChildren = false },
            new() { Id = "s", Depth = 0, HasChildren = false },
        };
        var expand = new Dictionary<string, bool> { ["p"] = false };
        var visible = new List<int>();
        HierarchyKeyboard.CollectVisibleIndices(nodes, expand, visible);
        Assert.Equal(new[] { 0, 2 }, visible);
    }

    [Fact]
    public void StepAndFoldActions()
    {
        Assert.Equal(1, HierarchyKeyboard.StepVisible(0, 1, 3));
        Assert.Equal(0, HierarchyKeyboard.StepVisible(0, -1, 3));
        Assert.Equal(2, HierarchyKeyboard.StepVisible(2, 1, 3));
        Assert.Equal(HierarchyFoldAction.Collapse,
            HierarchyKeyboard.ResolveFold(left: true, hasChildren: true, expanded: true, hasVisibleParent: true));
        Assert.Equal(HierarchyFoldAction.SelectParent,
            HierarchyKeyboard.ResolveFold(left: true, hasChildren: false, expanded: false, hasVisibleParent: true));
        Assert.Equal(HierarchyFoldAction.Expand,
            HierarchyKeyboard.ResolveFold(left: false, hasChildren: true, expanded: false, hasVisibleParent: false));
        Assert.Equal(HierarchyFoldAction.SelectFirstChild,
            HierarchyKeyboard.ResolveFold(left: false, hasChildren: true, expanded: true, hasVisibleParent: false));
    }
}
