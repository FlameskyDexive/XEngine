// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShaderVariantStripperTests
{
    [Fact]
    public void NoDecalFeature_DropsDecalShaderAndKeyword()
    {
        ShaderVariantStripper stripper = ShaderVariantStripper.FromFeatures([]);
        Assert.Equal(ShaderStripFeature.None, stripper.EnabledFeatures);
        Assert.False(stripper.KeepDefaultShader(DefaultShader.ScreenSpaceDecal));
        Assert.False(stripper.KeepKeyword(ShaderStripKeywords.Decal));
        Assert.False(stripper.KeepCanonicalKeywordSet("DECAL;"));
        Assert.False(stripper.KeepCanonicalKeywordSet("HAS_TANGENTS;DECAL;"));
        Assert.True(stripper.KeepDefaultShader(DefaultShader.Standard));
        Assert.True(stripper.KeepCanonicalKeywordSet(""));
        Assert.True(stripper.KeepCanonicalKeywordSet("HAS_TANGENTS;"));
        Assert.True(stripper.KeepDefaultShader(DefaultShader.GTAO));
        Assert.True(stripper.KeepDefaultShader(DefaultShader.SSR));
        Assert.False(stripper.KeepDefaultShader(DefaultShader.DeferredLighting));
        Assert.False(stripper.KeepDefaultShader(DefaultShader.SpriteLit));
        Assert.False(stripper.KeepDefaultShader(DefaultShader.Light2D));
        Assert.False(stripper.KeepDefaultShader(DefaultShader.Shadow2D));
    }

    [Fact]
    public void EnabledDecalFeature_KeepsDecalShaderAndKeyword()
    {
        ShaderVariantStripper stripper = ShaderVariantStripper.FromFeatures(
            [new ScreenSpaceDecalRendererFeature()]);
        Assert.True(stripper.KeepDefaultShader(DefaultShader.ScreenSpaceDecal));
        Assert.True(stripper.KeepKeyword(ShaderStripKeywords.Decal));
        Assert.True(stripper.KeepCanonicalKeywordSet("DECAL;"));
        Assert.False(stripper.KeepDefaultShader(DefaultShader.ScreenSpaceShadows));
    }

    [Fact]
    public void DisabledDecalFeature_IsStripped()
    {
        ShaderVariantStripper stripper = ShaderVariantStripper.FromFeatures(
            [new ScreenSpaceDecalRendererFeature { Enabled = false }]);
        Assert.False(stripper.KeepDefaultShader(DefaultShader.ScreenSpaceDecal));
        Assert.False(stripper.KeepKeyword(ShaderStripKeywords.Decal));
    }

    [Fact]
    public void ScreenSpaceShadowsFeature_GatesShadowShader()
    {
        ShaderVariantStripper none = ShaderVariantStripper.FromFeatures([]);
        Assert.False(none.KeepDefaultShader(DefaultShader.ScreenSpaceShadows));
        Assert.False(none.KeepKeyword(ShaderStripKeywords.ScreenSpaceShadows));

        ShaderVariantStripper enabled = ShaderVariantStripper.FromFeatures(
            [new ScreenSpaceShadowsRendererFeature()]);
        Assert.True(enabled.KeepDefaultShader(DefaultShader.ScreenSpaceShadows));
        Assert.True(enabled.KeepKeyword(ShaderStripKeywords.ScreenSpaceShadows));
        Assert.False(enabled.KeepDefaultShader(DefaultShader.ScreenSpaceDecal));
    }

    [Fact]
    public void Unfiltered_KeepsFeatureShaders()
    {
        Assert.True(ShaderVariantStripper.Unfiltered.KeepDefaultShader(DefaultShader.ScreenSpaceDecal));
        Assert.True(ShaderVariantStripper.Unfiltered.KeepKeyword(ShaderStripKeywords.Decal));
        Assert.True(ShaderVariantStripper.Unfiltered.KeepDefaultShader(DefaultShader.ScreenSpaceShadows));
        Assert.True(ShaderVariantStripper.Unfiltered.KeepDefaultShader(DefaultShader.DeferredLighting));
        Assert.True(ShaderVariantStripper.Unfiltered.KeepDefaultShader(DefaultShader.SpriteLit));
    }

    [Fact]
    public void FromPipeline_ReadsEnabledFeatures()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.False(ShaderVariantStripper.FromPipeline(pipeline).KeepDefaultShader(DefaultShader.ScreenSpaceDecal));

        pipeline.AddRendererFeature(new ScreenSpaceDecalRendererFeature());
        Assert.True(ShaderVariantStripper.FromPipeline(pipeline).KeepDefaultShader(DefaultShader.ScreenSpaceDecal));
        Assert.False(ShaderVariantStripper.FromPipeline(pipeline).KeepDefaultShader(DefaultShader.DeferredLighting));

        pipeline.RenderingPath = RenderingPath.Deferred;
        Assert.True(ShaderVariantStripper.FromPipeline(pipeline).KeepDefaultShader(DefaultShader.DeferredLighting));

        pipeline.RenderingPath = RenderingPath.DeferredPlus;
        Assert.True(ShaderVariantStripper.FromPipeline(pipeline).KeepDefaultShader(DefaultShader.DeferredLighting));

        pipeline.RenderingPath = RenderingPath.ForwardPlus;
        Assert.False(ShaderVariantStripper.FromPipeline(pipeline).KeepDefaultShader(DefaultShader.DeferredLighting));

        var twoD = new UniversalRenderPipeline { RendererKind = RendererKind.Renderer2D };
        Assert.True(ShaderVariantStripper.FromPipeline(twoD).KeepDefaultShader(DefaultShader.SpriteLit));
        Assert.True(ShaderVariantStripper.FromPipeline(twoD).KeepDefaultShader(DefaultShader.Light2D));
        Assert.False(ShaderVariantStripper.FromPipeline(twoD).KeepDefaultShader(DefaultShader.DeferredLighting));
    }

    [Fact]
    public void KeepShader_MatchesDefaultArtifactNames()
    {
        ShaderVariantStripper stripper = ShaderVariantStripper.FromFeatures([]);
        Assert.False(stripper.KeepShader(Shader.LoadDefault(DefaultShader.ScreenSpaceDecal)));
        Assert.True(stripper.KeepShader(Shader.LoadDefault(DefaultShader.Standard)));
        Assert.False(stripper.KeepShader(Shader.LoadDefault(DefaultShader.DeferredLighting)));
        Assert.False(stripper.KeepShader(Shader.LoadDefault(DefaultShader.SpriteLit)));
    }
}
