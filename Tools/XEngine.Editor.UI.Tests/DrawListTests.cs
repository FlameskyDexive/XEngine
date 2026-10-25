using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class DrawListTests
{
    [Fact]
    public void PreservesTreeAndLocalCommandOrderAndSkipsHiddenAncestors()
    {
        using var nodes = new UiNodeStore();
        using var draws = new UiDrawList(nodes, 256, 32);
        var root = nodes.Create(); var a = nodes.Create(root); var child = nodes.Create(a); var b = nodes.Create(root);
        foreach (var node in new[] { root, a, child, b })
            nodes.SetStyle(node, UiStyle.Default with { Width = UiLength.Fixed(100), Height = UiLength.Fixed(100), Flow = UiFlow.Overlay });
        nodes.SetRootBounds(root, new(0, 0, 100, 100)); nodes.Layout();
        draws.TrySet(root, [new(0, 6, 0, 5)]);
        draws.TrySet(a, [new(6, 6, 0, 9), new(12, 6, 0, 1)]);
        draws.TrySet(child, [new(18, 6, 0, 4)]); draws.TrySet(b, [new(24, 6, 0, 2)]);
        Assert.Equal(new uint[] { 5, 9, 1, 4, 2 }, Materials(draws));
        nodes.SetStyle(a, nodes.GetStyle(a) with { Visible = false }); nodes.Layout();
        Assert.Equal(new uint[] { 5, 2 }, Materials(draws));
        Assert.Equal(3, draws.VisitedNodes); // Child of hidden a was never visited.
        nodes.SetStyle(a, nodes.GetStyle(a) with { Visible = true }); nodes.Layout();
        nodes.Reparent(a, root); nodes.Layout();
        Assert.Equal(new uint[] { 5, 2, 9, 1, 4 }, Materials(draws));
        nodes.Remove(a); nodes.Layout();
        Assert.Equal(new uint[] { 5, 2 }, Materials(draws));
        Assert.Equal(2, draws.PruneRemovedNodes());
    }

    [Fact]
    public void ClipAndEmptyCommandsSuppressDrawsWithoutDiscardingRetainedCommands()
    {
        using var nodes = new UiNodeStore();
        using var draws = new UiDrawList(nodes, 256, 8);
        var root = nodes.Create(); var child = nodes.Create(root);
        nodes.SetRootBounds(root, new(0, 0, 100, 100));
        nodes.SetStyle(child, UiStyle.Default with { Position = UiPosition.Absolute, Left = 90,
            Width = UiLength.Fixed(30), Height = UiLength.Fixed(20) });
        nodes.Layout();
        draws.TrySet(child, [new(0, 0, 0, 1), new(0, 6, 0, 2)]);
        var list = draws.Prepare(); Assert.Equal(1, list.Length);
        Assert.Equal(new UiRect(90, 0, 10, 20), list[0].Clip);
        nodes.SetStyle(child, nodes.GetStyle(child) with { Left = 110 }); nodes.Layout();
        Assert.Equal(0, draws.Prepare().Length);
        nodes.SetStyle(child, nodes.GetStyle(child) with { Left = 90 }); nodes.Layout();
        Assert.Equal(1, draws.Prepare().Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => draws.TrySet(child, [new(-1, 6, 0, 2)]));
        Assert.Equal(1, draws.Prepare().Length);
    }

    [Fact]
    public void IdlePrepareDoesNotTraverseOrAllocateAndUnchangedCommandsKeepCache()
    {
        using var nodes = new UiNodeStore();
        using var draws = new UiDrawList(nodes, 256, 8);
        var root = nodes.Create(); nodes.SetRootBounds(root, new(0, 0, 100, 100)); nodes.Layout();
        draws.TrySet(root, [new(0, 6, 0, 1)]);
        for (int i = 0; i < 1000; i++) draws.Prepare();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) draws.Prepare();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes); Assert.Equal(0, draws.VisitedNodes);
        nodes.Invalidate(root, UiInvalidation.Paint); draws.Prepare();
        Assert.Equal(0, draws.VisitedNodes); // Color/geometry updates keep order and clips.
        draws.TrySet(root, [new(0, 6, 0, 1)]); draws.Prepare();
        Assert.Equal(0, draws.VisitedNodes);
        draws.TrySet(root, [new(0, 6, 0, 2)]);
        Assert.Equal(2u, draws.Prepare()[0].Command.MaterialKey);
        nodes.Remove(root); Assert.Equal(0, draws.Prepare().Length);
    }

    private static uint[] Materials(UiDrawList draws)
    {
        var commands = draws.Prepare(); var result = new uint[commands.Length];
        for (int i = 0; i < result.Length; i++) result[i] = commands[i].Command.MaterialKey;
        return result;
    }
}
