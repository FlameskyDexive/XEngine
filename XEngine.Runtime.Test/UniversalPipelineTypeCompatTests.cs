// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Pre-rename Echo <c>$type</c> data ("...Scriptable.PipelineAsset" / "...Scriptable.RendererData")
/// must resolve and deserialize into the Unity 6.7-renamed asset classes with fields intact.
/// </summary>
public sealed class UniversalPipelineTypeCompatTests
{
    [Theory]
    [InlineData("XEngine.Runtime.Rendering.Scriptable.PipelineAsset, XEngine.Runtime", typeof(UniversalRenderPipelineAsset))]
    [InlineData("XEngine.Runtime.Rendering.Scriptable.PipelineAsset", typeof(UniversalRenderPipelineAsset))]
    [InlineData("XEngine.Runtime.Rendering.Scriptable.RendererData, XEngine.Runtime", typeof(UniversalRenderData))]
    [InlineData("XEngine.Runtime.Rendering.Scriptable.RendererData", typeof(UniversalRenderData))]
    public void LegacyTypeNames_ResolveToRenamedTypes(string legacyName, Type expected)
    {
        Assert.Same(expected, TypeNameRegistry.ResolveFullTypeName(legacyName));
    }

    [Fact]
    public void LegacyPipelineAssetEcho_RoundTripsWithFieldsIntact()
    {
        var original = new UniversalRenderPipelineAsset
        {
            Hdr = true,
            RenderScale = 0.75f,
            ShadowAtlasSize = 2048,
            ShadowDistance = 80f,
            Antialiasing = PipelineAntialiasingMode.MSAA,
            MsaaSamples = 8,
        };
        string text = Serializer.Serialize(typeof(object), original).WriteToJson();
        Assert.Contains("UniversalRenderPipelineAsset", text);

        string legacy = text.Replace(
            "XEngine.Runtime.Rendering.UniversalRenderPipelineAsset",
            "XEngine.Runtime.Rendering.Scriptable.PipelineAsset");
        var result = Serializer.Deserialize(EchoObject.ReadFromJson(legacy), typeof(object));

        var asset = Assert.IsType<UniversalRenderPipelineAsset>(result);
        Assert.True(asset.Hdr);
        Assert.Equal(0.75f, asset.RenderScale);
        Assert.Equal(2048, asset.ShadowAtlasSize);
        Assert.Equal(80f, asset.ShadowDistance);
        Assert.Equal(PipelineAntialiasingMode.MSAA, asset.Antialiasing);
        Assert.Equal(8, asset.MsaaSamples);
    }

    [Fact]
    public void LegacyRendererDataEcho_RoundTripsWithFieldsIntact()
    {
        var original = new UniversalRenderData
        {
            RenderingPath = RenderingPath.Deferred,
            Kind = RendererKind.Renderer2D,
            LodCrossFade = LodCrossFadeMode.Dither,
        };
        string text = Serializer.Serialize(typeof(object), original).WriteToJson();
        Assert.Contains("UniversalRenderData", text);

        string legacy = text.Replace(
            "XEngine.Runtime.Rendering.UniversalRenderData",
            "XEngine.Runtime.Rendering.Scriptable.RendererData");
        var result = Serializer.Deserialize(EchoObject.ReadFromJson(legacy), typeof(object));

        var renderer = Assert.IsType<UniversalRenderData>(result);
        Assert.Equal(RenderingPath.Deferred, renderer.RenderingPath);
        Assert.Equal(RendererKind.Renderer2D, renderer.Kind);
        Assert.Equal(LodCrossFadeMode.Dither, renderer.LodCrossFade);
    }
}
