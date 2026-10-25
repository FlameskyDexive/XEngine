using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>UGUI-shaped name for the pooled XEngine mesh builder.</summary>
public sealed class VertexHelper
{
    private readonly UIMeshBuilder _builder;
    public VertexHelper(UIMeshBuilder builder) => _builder = builder;
    public VertexHelper() => _builder = UIMeshBuilder.Rent();
    public int currentVertCount => _builder.VertexCount;
    public int currentIndexCount => _builder.IndexCount;
    public bool IsEmpty => _builder.IsEmpty;

    public void Clear() => _builder.ResetForPackage();
    public void AddQuad(Rect rect, Color color) => _builder.AddQuad(rect, color, Float2.Zero, Float2.One);
    public void AddUIVertexStream(Rect rect, Color color) => AddQuad(rect, color);
    public void AddShadow(Float2 offset, Color color, bool useGraphicAlpha = true)
        => _builder.AddShadow(offset, color, useGraphicAlpha);
    public void AddOutline(Float2 distance, Color color, bool useGraphicAlpha = true)
        => _builder.AddOutline(distance, color, useGraphicAlpha);
    public void SetPositionAsUV1() => _builder.SetPositionAsUV1();
    public UIMeshBuilder Builder => _builder;
}
