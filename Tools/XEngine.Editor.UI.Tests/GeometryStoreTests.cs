using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class GeometryStoreTests
{
    [Fact]
    public void LocalChangePreservesNeighborsAndOnlyDirtiesItsPage()
    {
        using var nodes = new UiNodeStore();
        using var store = new UiGeometryStore<int>(nodes, 256, 8, 4);
        var a = nodes.Create(); var b = nodes.Create(); var c = nodes.Create();
        Assert.True(store.TrySet(a, [1, 2, 3, 4], out var ar));
        Assert.True(store.TrySet(b, [5, 6], out var br));
        Assert.True(store.TrySet(c, [7, 8, 9], out var cr));
        Drain(store);
        Assert.True(store.TrySet(b, [10, 11], out var changed));
        Assert.Equal(br, changed);
        Assert.Equal(new[] { 1, 2, 3, 4 }, store.Read(ar).ToArray());
        Assert.Equal(new[] { 7, 8, 9 }, store.Read(cr).ToArray());
        Assert.True(store.TryGetPending(out var upload));
        Assert.Equal(br.Offset, upload.Offset); Assert.Equal(4, upload.Count);
        Assert.Equal(1, store.DirtyPageCount);
        Drain(store);
        Assert.True(store.TrySet(b, [10, 11], out _));
        Assert.Equal(0, store.DirtyPageCount);
        Assert.Equal(3, store.UsedPages);
    }

    [Fact]
    public void GrowthFailurePreservesOldGeometryAndOtherEntries()
    {
        using var nodes = new UiNodeStore();
        using var store = new UiGeometryStore<int>(nodes, 256, 4, 2);
        var a = nodes.Create(); var b = nodes.Create();
        store.TrySet(a, [1, 2], out var original);
        store.TrySet(b, [3, 4], out var neighbor);
        Assert.True(store.TrySet(a, [5, 6, 7], out var grown));
        Assert.NotEqual(original.Offset, grown.Offset);
        Assert.Equal(new[] { 3, 4 }, store.Read(neighbor).ToArray());
        Assert.False(store.TrySet(a, [9, 10, 11, 12, 13], out _));
        Assert.True(store.TryGet(a, out var still)); Assert.Equal(grown, still);
        Assert.Equal(new[] { 5, 6, 7 }, store.Read(still).ToArray());
        Assert.Equal(3, store.UsedPages);
        Assert.True(store.TrySet(a, [], out var empty));
        Assert.Equal(0, empty.Count); Assert.True(store.TryGet(a, out _));
        Assert.Equal(1, store.UsedPages); Assert.Equal(2, store.Count);
    }

    [Fact]
    public void RemovedAndReusedNodeSlotsDoNotResurrectGeometry()
    {
        using var nodes = new UiNodeStore();
        using var foreign = new UiNodeStore();
        using var store = new UiGeometryStore<int>(nodes, 256, 2, 2);
        var old = nodes.Create(); store.TrySet(old, [1], out _);
        nodes.Remove(old);
        var replacement = nodes.Create();
        Assert.False(store.TryGet(replacement, out _));
        Assert.True(store.TrySet(replacement, [2], out var current));
        Assert.False(store.Remove(old)); Assert.Equal(1, store.Count);
        Assert.Equal(new[] { 2 }, store.Read(current).ToArray());
        Assert.Throws<ArgumentException>(() => store.TryGet(old, out _));
        Assert.Throws<ArgumentException>(() => store.TrySet(foreign.Create(), [3], out _));
        nodes.Remove(replacement);
        Assert.Equal(1, store.PruneRemovedNodes());
        Assert.Equal(0, store.UsedPages); Assert.Equal(0, store.Count);
        Assert.Equal(0, store.PruneRemovedNodes());
    }

    [Fact]
    public void NewWriteProtectsPendingUploadAndPaintAcknowledgmentBelongsToCaller()
    {
        using var nodes = new UiNodeStore();
        using var store = new UiGeometryStore<int>(nodes, 256, 2, 2);
        var node = nodes.Create();
        store.TrySet(node, [1], out _); store.TryGetPending(out var old);
        store.TrySet(node, [2], out _);
        Assert.False(store.Acknowledge(old));
        Assert.NotEqual(UiInvalidation.None, nodes.GetInvalidation(node) & UiInvalidation.Paint);
        Drain(store); Assert.Equal(0, store.DirtyPageCount);
        store.InvalidateAll(); Assert.Equal(2, store.DirtyPageCount);
    }

    [Fact]
    public void WarmMutationLookupAndDrainAllocateNothing()
    {
        using var nodes = new UiNodeStore();
        using var store = new UiGeometryStore<int>(nodes, 256, 2, 2);
        var node = nodes.Create();
        for (int i = 0; i < 1000; i++) Step(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Step(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        void Step(int value)
        {
            store.TrySet(node, [value], out _); store.TryGet(node, out _); Drain(store);
        }
    }

    private static void Drain(UiGeometryStore<int> store)
    { while (store.TryGetPending(out var upload)) store.Acknowledge(upload); }

    [Fact]
    public void RandomGrowthRemovalAndFragmentationKeepAllLiveRangesDisjoint()
    {
        using var nodes = new UiNodeStore(16, 16);
        using var store = new UiGeometryStore<int>(nodes, 16, 32, 4);
        var handles = new UiNodeHandle[16];
        var expected = new int[16][];
        var random = new Random(7119);
        for (int step = 0; step < 4000; step++)
        {
            int slot = random.Next(16);
            if (handles[slot].IsNull) handles[slot] = nodes.Create();
            if (random.Next(5) == 0)
            {
                nodes.Remove(handles[slot]); store.Remove(handles[slot]);
                handles[slot] = default; expected[slot] = null!;
            }
            else
            {
                var data = new int[random.Next(20)];
                Array.Fill(data, step + 1);
                if (store.TrySet(handles[slot], data, out _)) expected[slot] = data;
            }
            var occupied = new bool[store.Capacity];
            int count = 0;
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i].IsNull || expected[i] == null) continue;
                count++;
                Assert.True(store.TryGet(handles[i], out var range));
                Assert.Equal(expected[i], store.Read(range).ToArray());
                for (int e = range.Offset; e < range.Offset + range.Count; e++)
                { Assert.False(occupied[e]); occupied[e] = true; }
            }
            Assert.Equal(count, store.Count);
            Assert.InRange(store.UsedPages, 0, 32);
            Drain(store);
        }
    }
}
