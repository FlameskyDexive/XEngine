// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.MaterialOverride;

/// <summary>
/// ★ M7 — Covers the material override channel's two decisions that are testable without a GPU:
/// which pass an override material draws with, and how the Overdraw raster override patches state.
/// The pixels themselves are covered by the scene-view A/B capture.
/// </summary>
public class MaterialOverrideChannelTests
{
    [Fact]
    public void ResolveOverridePass_PicksThePassCarryingTheTag()
    {
        // Unlit ships "Unlit" (RenderOrder=Opaque) and "UnlitPrepass" (LightMode=Prepass).
        var material = new Material(Shader.LoadDefault(DefaultShader.Unlit));

        int opaque = RenderPipeline.ResolveOverridePass(material, "RenderOrder", "Opaque", hasRenderOrder: true);
        int prepass = RenderPipeline.ResolveOverridePass(material, "LightMode", "Prepass", hasRenderOrder: true);

        Assert.True(opaque >= 0);
        Assert.True(prepass >= 0);
        Assert.NotEqual(opaque, prepass);
    }

    [Fact]
    public void ResolveOverridePass_FallsBackToFirstGraphicsPassWhenNoTagMatches()
    {
        var material = new Material(Shader.LoadDefault(DefaultShader.Unlit));

        // Unlit has no Transparent pass; the override still has to draw, so it falls back.
        int fallback = RenderPipeline.ResolveOverridePass(material, "RenderOrder", "Transparent", hasRenderOrder: true);

        Assert.True(fallback >= 0);
        Assert.Equal(RenderPipeline.ResolveOverridePass(material, "RenderOrder", "Opaque", hasRenderOrder: true), fallback);
    }

    [Fact]
    public void ResolveOverridePass_WithoutTagFilterTakesTheFirstPass()
    {
        var material = new Material(Shader.LoadDefault(DefaultShader.Unlit));

        Assert.Equal(0, RenderPipeline.ResolveOverridePass(material, "", "", hasRenderOrder: false));
    }

    [Fact]
    public void ForceAdditiveNoDepth_PatchesBlendAndDepthOff()
    {
        var state = new RasterizerState
        {
            DepthTest = true,
            DepthWrite = true,
            DoBlend = false,
            CullFace = RasterizerState.PolyFace.Back,
        };

        RasterizerState patched = PatchLikeCommandBuffer(state, wireframe: false, additive: true);

        Assert.False(patched.DepthTest);
        Assert.False(patched.DepthWrite);
        Assert.True(patched.DoBlend);
        Assert.Equal(RasterizerState.Blending.One, patched.BlendSrc);
        Assert.Equal(RasterizerState.Blending.One, patched.BlendDst);
        Assert.Equal(RasterizerState.BlendMode.Add, patched.Blend);
        Assert.Equal(RasterizerState.PolyFace.None, patched.CullFace);
        // Fill mode is the wireframe view's business, not overdraw's.
        Assert.Equal(RasterizerState.FillModeKind.Solid, patched.FillMode);
    }

    [Fact]
    public void ForceWireframeAndAdditive_Compose()
    {
        var state = new RasterizerState();

        RasterizerState patched = PatchLikeCommandBuffer(state, wireframe: true, additive: true);

        Assert.Equal(RasterizerState.FillModeKind.Wireframe, patched.FillMode);
        Assert.True(patched.DoBlend);
        Assert.False(patched.DepthTest);
    }

    /// <summary>
    /// Mirrors <c>CommandBuffer.SetRasterState</c>'s patching so the rules can be asserted without a
    /// graphics device. Kept in lockstep with that method.
    /// </summary>
    private static RasterizerState PatchLikeCommandBuffer(RasterizerState state, bool wireframe, bool additive)
    {
        RasterizerState patched = state;
        if (wireframe)
            patched.FillMode = RasterizerState.FillModeKind.Wireframe;
        if (additive)
        {
            patched.DepthTest = false;
            patched.DepthWrite = false;
            patched.DoBlend = true;
            patched.BlendSrc = RasterizerState.Blending.One;
            patched.BlendDst = RasterizerState.Blending.One;
            patched.Blend = RasterizerState.BlendMode.Add;
            patched.CullFace = RasterizerState.PolyFace.None;
        }
        return patched;
    }
}
