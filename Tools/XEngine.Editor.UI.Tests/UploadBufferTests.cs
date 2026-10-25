using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class UploadBufferTests
{
    [Fact]
    public void WholeSnapshotWriteDirtiesOnlyChangedPagesIncludingPartialEdges()
    {
        using var buffer = new UiUploadBuffer<int>(14, 4);
        Drain(buffer);
        int[] snapshot = new int[14]; snapshot[5] = 7; snapshot[13] = 9;
        buffer.Write(0, snapshot);
        Assert.Equal(2, buffer.DirtyPageCount);
        buffer.TryGetPending(out var first); Assert.Equal(4, first.Offset); Assert.Equal(4, first.Count);
        buffer.Acknowledge(first);
        buffer.TryGetPending(out var tail); Assert.Equal(12, tail.Offset); Assert.Equal(2, tail.Count);
        buffer.Acknowledge(tail);
        buffer.Write(3, [0, 0, 7, 0, 0, 8]);
        Assert.Equal(1, buffer.DirtyPageCount);
        buffer.TryGetPending(out var changed); Assert.Equal(8, changed.Offset); Assert.Equal(4, changed.Count);
    }

    [Fact]
    public void OverlappingSnapshotWritePreservesMemmoveSemanticsAcrossPages()
    {
        using var buffer = new UiUploadBuffer<int>(10, 2);
        buffer.Write(0, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]); Drain(buffer);
        buffer.Write(1, buffer.Read(0, 9));
        Assert.Equal(new[] { 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, buffer.Read(0, 10).ToArray());
    }

    [Fact]
    public void SeveralDisjointRangesCanBeEncodedBeforeAnyAcknowledgment()
    {
        using var buffer = new UiUploadBuffer<int>(13, 4);
        Drain(buffer);
        buffer.Write(0, [1]); buffer.Write(12, [2]);
        Assert.True(buffer.TryGetPending(out var first));
        Assert.True(buffer.TryGetPending(out var second, startOffset: first.Offset + first.Count));
        Assert.Equal(12, second.Offset); Assert.Equal(1, second.Count);
        Assert.False(buffer.TryGetPending(out _, startOffset: second.Offset + second.Count));
        Assert.Equal(2, buffer.DirtyPageCount);
        buffer.Acknowledge(first); buffer.Acknowledge(second);
        Assert.Equal(0, buffer.DirtyPageCount);
        Assert.Throws<ArgumentException>(() => buffer.TryGetPending(out _, startOffset: 1));
    }

    [Fact]
    public void WritesBeforeCachedDirtyHeadAndTailOnlyWritesRemainDiscoverable()
    {
        using var buffer = new UiUploadBuffer<int>(10000, 1);
        Drain(buffer);
        buffer.Write(9999, [9]);
        buffer.TryGetPending(out var tail);
        Assert.Equal(9999, tail.Offset);
        buffer.Write(3, [3]);
        buffer.TryGetPending(out var head);
        Assert.Equal(3, head.Offset);
        buffer.Acknowledge(head);
        buffer.TryGetPending(out tail);
        Assert.Equal(9999, tail.Offset);
        buffer.Acknowledge(tail);
        Assert.Equal(0, buffer.DirtyPageCount);
        buffer.Write(9999, [10]);
        buffer.TryGetPending(out tail);
        Assert.Equal(9999, tail.Offset);
    }

    [Fact]
    public void InitialUploadIncludesZerosThenUnchangedWritesStayClean()
    {
        using var buffer = new UiUploadBuffer<int>(10, 4);
        Assert.True(buffer.TryGetPending(out var initial));
        Assert.Equal(10, initial.Count);
        Assert.Equal(new int[10], buffer.ReadPending(initial).ToArray());
        Assert.True(buffer.Acknowledge(initial));
        Assert.False(buffer.TryGetPending(out _));
        Assert.False(buffer.Write(0, new int[10]));
        Assert.Equal(0, buffer.DirtyPageCount);
    }

    [Fact]
    public void UpdatesCoalesceOnlyAdjacentPagesAndRespectTailAndChunkLimit()
    {
        using var buffer = new UiUploadBuffer<int>(19, 4);
        Drain(buffer);
        buffer.Write(3, [12, 13]); // Pages 0 and 1.
        buffer.Write(18, [99]); // Last partial page, separated by clean pages.
        Assert.True(buffer.TryGetPending(out var first, 1));
        Assert.Equal(0, first.Offset); Assert.Equal(4, first.Count);
        buffer.Acknowledge(first);
        Assert.True(buffer.TryGetPending(out var second));
        Assert.Equal(4, second.Offset); Assert.Equal(4, second.Count);
        buffer.Acknowledge(second);
        Assert.True(buffer.TryGetPending(out var tail));
        Assert.Equal(16, tail.Offset); Assert.Equal(3, tail.Count);
        Assert.Equal(99, buffer.ReadPending(tail)[2]);
        buffer.Acknowledge(tail);
        Assert.False(buffer.TryGetPending(out _));
    }

    [Fact]
    public void DelayedAcknowledgmentCannotEraseNewDataAndForeignRangesFail()
    {
        using var buffer = new UiUploadBuffer<int>(16, 4);
        using var other = new UiUploadBuffer<int>(16, 4);
        buffer.TryGetPending(out var old);
        buffer.Write(1, [42]);
        Assert.False(buffer.Acknowledge(old));
        Assert.Throws<InvalidOperationException>(() => ReadOld(buffer, old));
        Assert.Throws<ArgumentException>(() => other.Acknowledge(old));
        Assert.Equal(4, buffer.DirtyPageCount);
        Drain(buffer);
        buffer.InvalidateAll();
        Assert.Equal(4, buffer.DirtyPageCount);
        Assert.False(buffer.Acknowledge(old));
    }

    [Fact]
    public void InvalidRangesAreAtomicAndDisposalDropsStorage()
    {
        var buffer = new UiUploadBuffer<int>(4);
        Drain(buffer);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(4, new[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Write(-1, new[] { 1 }));
        Assert.Throws<ArgumentException>(() => buffer.Acknowledge(default));
        Assert.Equal(0, buffer.DirtyPageCount);
        buffer.Dispose(); buffer.Dispose();
        Assert.Equal(0, buffer.Capacity);
        Assert.Throws<ObjectDisposedException>(() => buffer.TryGetPending(out _));
    }

    [Fact]
    public void WarmWriteAndDrainDoNotAllocate()
    {
        using var buffer = new UiUploadBuffer<int>(128, 16);
        Drain(buffer);
        for (int i = 0; i < 10000; i++) Step(buffer, i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Step(buffer, i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static void Step(UiUploadBuffer<int> buffer, int value)
    { buffer.Write(63, [value]); Drain(buffer); }
    private static void Drain(UiUploadBuffer<int> buffer)
    { while (buffer.TryGetPending(out var range)) buffer.Acknowledge(range); }
    private static void ReadOld(UiUploadBuffer<int> buffer, UiUploadRange range) => buffer.ReadPending(range);
}
