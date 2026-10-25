// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Rendering.VirtualGeometry;

using Xunit;

using static XEngine.Runtime.VertexFormat;

namespace XEngine.Runtime.Test;

/// <summary>Tests for the virtual geometry residency budget policy (LRU over idle entries).</summary>
public class VirtualGeometryStreamingTests
{
    private static VirtualGeometrySystem.Entry MakeEntry(int refCount, long lastUse, int vertices)
        => new()
        {
            RefCount = refCount,
            LastUseFrame = lastUse,
            Resources = new VirtualGeometryGpuResources
            {
                VertexCount = vertices,
                Layout = new VertexFormat(new[] { new Element(VertexSemantic.Position, VertexType.Float, 3) }),
            },
        };

    // 12 bytes per vertex (position-only layout): 1000 vertices = 12KB per entry.
    private static Dictionary<int, VirtualGeometrySystem.Entry> MakeEntries()
        => new()
        {
            { 1, MakeEntry(0, 100, 1000) }, // idle, oldest
            { 2, MakeEntry(0, 200, 1000) }, // idle, newer
            { 3, MakeEntry(1, 50, 1000) },  // ACTIVE — never evict
        };

    [Fact]
    public void SelectEvictions_NoPressure_EvictsNothing()
    {
        var entries = MakeEntries();

        var evictions = VirtualGeometrySystem.SelectEvictions(entries, budgetBytes: 100_000, currentFrame: 300, graceFrames: 120);

        Assert.Empty(evictions);
    }

    [Fact]
    public void SelectEvictions_OverBudget_EvictsIdleInLruOrder_KeepsActive()
    {
        var entries = MakeEntries();

        // 36KB total, 12KB budget: both idle entries go (oldest first); the active entry
        // (id 3) survives even though it is technically the least recently used.
        var evictions = VirtualGeometrySystem.SelectEvictions(entries, budgetBytes: 12_000, currentFrame: 1000, graceFrames: 120);

        Assert.Equal(2, evictions.Count);
        Assert.Equal(1, evictions[0].Key);
        Assert.Equal(2, evictions[1].Key);
        Assert.Contains(3, entries.Keys); // active entry untouched
    }

    [Fact]
    public void SelectEvictions_RespectsGraceWindow()
    {
        var entries = new Dictionary<int, VirtualGeometrySystem.Entry>
        {
            { 1, MakeEntry(0, 950, 1000) }, // released only 50 frames ago (grace = 120)
        };

        var evictions = VirtualGeometrySystem.SelectEvictions(entries, budgetBytes: 0, currentFrame: 1000, graceFrames: 120);

        Assert.Empty(evictions);
    }

    [Fact]
    public void SelectEvictions_EvictsEnoughToMeetBudget()
    {
        var entries = new Dictionary<int, VirtualGeometrySystem.Entry>
        {
            { 1, MakeEntry(0, 100, 3000) }, // 36KB
            { 2, MakeEntry(0, 200, 3000) }, // 36KB
            { 3, MakeEntry(0, 300, 3000) }, // 36KB
        };

        // 108KB total, 50KB budget: two LRU entries must go.
        var evictions = VirtualGeometrySystem.SelectEvictions(entries, budgetBytes: 50_000, currentFrame: 1000, graceFrames: 120);

        Assert.Equal(2, evictions.Count);
        Assert.Equal(1, evictions[0].Key);
        Assert.Equal(2, evictions[1].Key);
    }
}
