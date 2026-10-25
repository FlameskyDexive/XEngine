// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ScreenSpaceShadowTests : RuntimeTestBase
{
    private readonly SoftShadowQuality _softShadowQuality = QualitySettings.SoftShadowQuality;

    public override void Dispose()
    {
        QualitySettings.SoftShadowQuality = _softShadowQuality;
        base.Dispose();
    }

    [Fact]
    public void TapCount_LowMediumHigh_Are4_8_16()
    {
        Assert.Equal(4, SoftShadowQualityUtil.TapCount(SoftShadowQuality.Low));
        Assert.Equal(8, SoftShadowQualityUtil.TapCount(SoftShadowQuality.Medium));
        Assert.Equal(16, SoftShadowQualityUtil.TapCount(SoftShadowQuality.High));
        Assert.Equal(0.65f, SoftShadowQualityUtil.RadiusScale(SoftShadowQuality.Low));
        Assert.Equal(1f, SoftShadowQualityUtil.RadiusScale(SoftShadowQuality.Medium));
        Assert.Equal(1.35f, SoftShadowQualityUtil.RadiusScale(SoftShadowQuality.High));
    }

    [Fact]
    public void DisabledFeature_LeavesSevenPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceShadowsRendererFeature { Enabled = false });

        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);
        Assert.False(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, UniversalRenderPipeline.MapPassInputs(pipeline.RequestedInputsForTest)));
    }

    [Fact]
    public void EnabledFeature_RequestsDepthAndCopyDepth()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceShadowsRendererFeature());

        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.Depth, pipeline.RequestedInputsForTest);
        Assert.True(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox,
            UniversalRenderPipeline.MapPassInputs(ScriptableRenderPassInput.Depth)));
    }

    [Fact]
    public void Combine_WhiteKeepsAtlas_BlackFullyShadows()
    {
        Assert.Equal(0.3f, CombineAtlas(0.3f, 1f), 5);
        Assert.Equal(1f, CombineAtlas(0.3f, 0f), 5);
        Assert.Equal(0.7f, LitFactor(0.3f, 1f), 5);
        Assert.Equal(0f, LitFactor(0.3f, 0f), 5);
    }

    [Fact]
    public void ScreenSpaceShadowsShader_HasGlslAndHlsl()
    {
        Shader shader = Shader.LoadDefault(DefaultShader.ScreenSpaceShadows);
        ShaderPass pass = shader.GetPass(0);
        Assert.True(pass.HasGlsl);
        Assert.True(pass.HasHlsl);

        string glsl = PassSource(pass, "_fragmentSource");
        string hlsl = PassSource(pass, "_hlslFragmentSource");
        Assert.Contains("_CameraDepthTexture", glsl);
        Assert.Contains("_DirectionalLightDirection", glsl);
        Assert.Contains("getViewPos", glsl);
        Assert.Contains("ScreenSpaceShadowsPS", hlsl);
        Assert.Contains("_CameraDepthTexture", hlsl);
    }

    private static float CombineAtlas(float atlasShadow, float screenSpaceLit)
        => 1f - (1f - atlasShadow) * screenSpaceLit;

    private static float LitFactor(float atlasShadow, float screenSpaceLit)
        => (1f - atlasShadow) * screenSpaceLit;

    private static string PassSource(ShaderPass pass, string field)
        => (string)typeof(ShaderPass)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
}
