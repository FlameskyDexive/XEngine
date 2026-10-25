using Xunit;

using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

/// <summary>
/// Headless-safe text-path tests: only the pure math (block alignment shift, corner gradient
/// multiply, AppendQuadCorners triangulation). The Scribe layout/DrawLayout path touches the
/// shared glyph atlas (GPU handles) and is verified by live-editor screenshot acceptance.
/// </summary>
public sealed class HudTextTests
{
    [Fact]
    public void AlignShift_MatchesAlignment()
    {
        Assert.Equal(0f, HudTextWidget.AlignShift(HudAlignment.Left));
        Assert.Equal(0.5f, HudTextWidget.AlignShift(HudAlignment.Middle));
        Assert.Equal(1f, HudTextWidget.AlignShift(HudAlignment.Right));
    }

    [Fact]
    public void MultiplyColor_BlendsBaseWithCornerGradient()
    {
        Color32 red = HudFontRenderer.MultiplyColor(new Color(255f, 255f, 255f, 255f), new Color32(255, 0, 0, 255));
        Assert.Equal((byte)255, red.R);
        Assert.Equal((byte)0, red.G);

        // Half-alpha white gradient over half-alpha blue base.
        Color32 half = HudFontRenderer.MultiplyColor(new Color(0f, 0f, 255f, 128f), new Color32(255, 255, 255, 128));
        Assert.Equal((byte)0, half.R);
        Assert.Equal((byte)255, half.B);
        Assert.Equal((byte)64, half.A);
    }

    [Fact]
    public void AppendQuadCorners_WritesQuadWithBlBrTlTrTriangulation()
    {
        HudMeshData data = new();
        data.BeginFrame();
        data.AppendQuadCorners(
            new Float3(0f, 0f, 0f), new Float3(1f, 0f, 0f), new Float3(0f, 1f, 0f), new Float3(1f, 1f, 0f),
            new Float2(0f, 0f), new Float2(1f, 0f), new Float2(0f, 1f), new Float2(1f, 1f),
            new Color32(1, 2, 3, 255), new Color32(4, 5, 6, 255), new Color32(7, 8, 9, 255), new Color32(10, 11, 12, 255));

        Assert.Equal(4, data.VertexCount);
        Assert.Equal(6, data.IndexCount);
        Assert.Equal(new Float3(1f, 1f, 0f), data.VertexAt(3)); // TR last
        data.FillPadding();
    }
}
