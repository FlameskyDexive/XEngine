using Xunit;

using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

public sealed class HudMeshDataTests
{
    private static HudQuad MakeQuad(float size = 10f)
        => new()
        {
            OffsetBL = new Float2(0f, 0f),
            OffsetBR = new Float2(size, 0f),
            OffsetTR = new Float2(size, size),
            OffsetTL = new Float2(0f, size),
            UvBL = new Float2(0f, 0f),
            UvBR = new Float2(1f, 0f),
            UvTR = new Float2(1f, 1f),
            UvTL = new Float2(0f, 1f),
        };

    [Fact]
    public void AppendQuad_WritesFourVerticesSixIndices()
    {
        HudMeshData data = new();
        data.BeginFrame();
        data.AppendQuad(Float3.Zero, new Float3(0.01f, 0f, 0f), new Float3(0f, 0.01f, 0f), MakeQuad(), 0f, 0f, 1f);

        Assert.Equal(4, data.VertexCount);
        Assert.Equal(6, data.IndexCount);
        Assert.True(data.HasContent);
    }

    [Fact]
    public void FillPadding_ExtendsLastVertexWithDegenerateIndices()
    {
        HudMeshData data = new();
        data.BeginFrame();
        data.AppendQuad(Float3.Zero, new Float3(0.01f, 0f, 0f), new Float3(0f, 0.01f, 0f), MakeQuad(), 0f, 0f, 1f);
        data.FillPadding();

        // Counts unchanged — padding only fills the tail.
        Assert.Equal(4, data.VertexCount);
        Assert.Equal(6, data.IndexCount);
    }

    [Fact]
    public void EnsureCapacity_GrowsGeometricallyAndKeepsCounts()
    {
        HudMeshData data = new();
        data.BeginFrame();
        int quads = HudMeshData.InitialVertexCapacity / 4 + 5; // force at least one growth
        for (int i = 0; i < quads; i++)
            data.AppendQuad(Float3.Zero, new Float3(1f, 0f, 0f), new Float3(0f, 1f, 0f), MakeQuad(1f), 0f, 0f, 1f);

        Assert.True(data.VertexCapacity >= quads * 4);
        Assert.Equal(quads * 4, data.VertexCount);
        Assert.Equal(quads * 6, data.IndexCount);
    }

    [Fact]
    public void BeginFrame_ClearsContentButKeepsCapacity()
    {
        HudMeshData data = new();
        data.BeginFrame();
        data.AppendQuad(Float3.Zero, new Float3(1f, 0f, 0f), new Float3(0f, 1f, 0f), MakeQuad(), 0f, 0f, 1f);
        int capacity = data.VertexCapacity;

        data.BeginFrame();
        Assert.Equal(0, data.VertexCount);
        Assert.Equal(0, data.IndexCount);
        Assert.False(data.HasContent);
        Assert.Equal(capacity, data.VertexCapacity);
    }
}
