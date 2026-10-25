using System.Collections.Generic;

using Xunit;

using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

/// <summary>
/// End-to-end headless text test: PushText → Fill → glyph quads land in the slot-0 font mesh.
/// The shared Scribe atlas runs headless (CPU rasterization; GPU upload is lazy), so this
/// exercises the real shaping path.
/// </summary>
public sealed class HudTextFillTests
{
    [Fact]
    public void PushText_Fill_WritesGlyphQuadsIntoFontSlot()
    {
        HudMeshData fontTarget = new();
        List<HudMeshData?> targets = [fontTarget];

        HudTextWidget widget = new();
        widget.PushText("HUD", HudAlignment.Middle);
        Assert.True(widget.IsValid);
        Assert.True(widget.Size.X > 0f);

        widget.Anchor = Float3.Zero;
        widget.Fill(targets, new HudFillContext
        {
            RightStep = new Float3(0.01f, 0f, 0f),
            UpStep = new Float3(0f, 0.01f, 0f),
        });

        Assert.True(fontTarget.VertexCount > 0, "text produced no glyphs");
        Assert.Equal(0, fontTarget.VertexCount % 4);
        Assert.True(fontTarget.HasContent);

        // Empty text hides the widget.
        widget.PushText("", HudAlignment.Middle);
        Assert.False(widget.IsValid);
    }

    [Fact]
    public void Outline_WritesMoreQuadsThanFillAlone()
    {
        HudMeshData plain = new();
        HudMeshData outlined = new();
        List<HudMeshData?> plainTargets = [plain];
        List<HudMeshData?> outlinedTargets = [outlined];

        HudTextWidget widget = new();
        widget.PushText("12", HudAlignment.Left);
        widget.Anchor = Float3.Zero;

        widget.Fill(plainTargets, new HudFillContext
        {
            RightStep = Float3.UnitX,
            UpStep = Float3.UnitY,
        });

        widget.OutlineWidth = 2f;
        widget.Fill(outlinedTargets, new HudFillContext
        {
            RightStep = Float3.UnitX,
            UpStep = Float3.UnitY,
        });

        // 4 outline passes + 1 fill pass.
        Assert.Equal(plain.VertexCount * 5, outlined.VertexCount);
    }
}
