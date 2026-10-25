// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// <see cref="UniversalRenderPipelineGlobalSettings"/> gates <see cref="ShaderVariantStripper"/>:
/// a null settings asset keeps the legacy feature-mask behavior, StripUnusedVariants=false keeps
/// everything, and Strip2DUnusedVariants (Unity 6.7 default off) keeps the 2D shader family.
/// </summary>
public sealed class UniversalPipelineGlobalSettingsTests : IDisposable
{
    private readonly UniversalRenderPipelineGlobalSettings? _previous = UniversalRenderPipelineGlobalSettings.Active;

    public void Dispose()
    {
        UniversalRenderPipelineGlobalSettings.Active = _previous;
    }

    private static ShaderVariantStripper StripperFor(UniversalRenderPipeline? pipeline = null)
        => ShaderVariantStripper.FromPipeline(pipeline ?? new UniversalRenderPipeline());

    [Fact]
    public void NullSettings_KeepsLegacyFeatureMaskStripping()
    {
        UniversalRenderPipelineGlobalSettings.Active = null;
        var stripper = StripperFor();

        // Legacy behavior: no Renderer2D feature → 2D shaders strip.
        Assert.False(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.SpriteLit)));
        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.Standard)));
    }

    [Fact]
    public void StripUnusedVariantsFalse_KeepsEverything()
    {
        UniversalRenderPipelineGlobalSettings.Active = new UniversalRenderPipelineGlobalSettings
        {
            StripUnusedVariants = false,
        };
        var stripper = StripperFor();

        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.SpriteLit)));
        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.ScreenSpaceDecal)));
        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.DeferredLighting)));
        Assert.True(stripper.KeepKeyword(ShaderStripKeywords.Decal));
    }

    [Fact]
    public void Strip2DUnusedVariantsFalse_Keeps2DShaders()
    {
        UniversalRenderPipelineGlobalSettings.Active = new UniversalRenderPipelineGlobalSettings();
        var stripper = StripperFor();

        // Unity 6.7 default: 2D shaders stay even without an active Renderer2D.
        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.SpriteLit)));
        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.Light2D)));
        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.Shadow2D)));
        // Non-2D stripping is unaffected.
        Assert.False(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.ScreenSpaceDecal)));
    }

    [Fact]
    public void Strip2DUnusedVariantsTrue_Strips2DWhenInactive()
    {
        UniversalRenderPipelineGlobalSettings.Active = new UniversalRenderPipelineGlobalSettings
        {
            Strip2DUnusedVariants = true,
        };
        var stripper = StripperFor();

        Assert.False(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.SpriteLit)));
    }

    [Fact]
    public void Strip2DUnusedVariantsTrue_Keeps2DWhenRenderer2DActive()
    {
        var pipeline = new UniversalRenderPipeline { RendererKind = RendererKind.Renderer2D };
        UniversalRenderPipelineGlobalSettings.Active = new UniversalRenderPipelineGlobalSettings
        {
            Strip2DUnusedVariants = true,
        };
        var stripper = StripperFor(pipeline);

        Assert.True(stripper.KeepShaderAsset(BuiltInAssets.GuidFor(DefaultShader.SpriteLit)));
    }

    [Fact]
    public void SettingsClampShaderVariantLogLevel()
    {
        var settings = new UniversalRenderPipelineGlobalSettings { ShaderVariantLogLevel = 9 };
        settings.OnValidate();
        Assert.Equal(3, settings.ShaderVariantLogLevel);

        settings.ShaderVariantLogLevel = -1;
        settings.OnValidate();
        Assert.Equal(0, settings.ShaderVariantLogLevel);
    }

    [Fact]
    public void EchoRoundTrip_PreservesStrippingSwitches()
    {
        var original = new UniversalRenderPipelineGlobalSettings
        {
            ExportShaderVariants = false,
            StripUnusedVariants = false,
            StripDebugVariants = false,
            StripUnusedPostProcessingVariants = true,
            Strip2DUnusedVariants = true,
            ShaderVariantLogLevel = 2,
        };
        var echo = XEngine.Echo.Serializer.Serialize(typeof(object), original);
        var deserialized = XEngine.Echo.Serializer.Deserialize<UniversalRenderPipelineGlobalSettings>(echo);

        Assert.NotNull(deserialized);
        Assert.False(deserialized!.ExportShaderVariants);
        Assert.False(deserialized.StripUnusedVariants);
        Assert.False(deserialized.StripDebugVariants);
        Assert.True(deserialized.StripUnusedPostProcessingVariants);
        Assert.True(deserialized.Strip2DUnusedVariants);
        Assert.Equal(2, deserialized.ShaderVariantLogLevel);
    }
}
