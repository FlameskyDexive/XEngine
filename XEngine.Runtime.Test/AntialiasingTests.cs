// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class AntialiasingTests : RuntimeTestBase
{
    private readonly QualitySettings.AntiAliasingMode _antiAliasing = QualitySettings.AntiAliasing;
    private readonly bool _imageEffects = QualitySettings.ImageEffectsEnabled;

    public override void Dispose()
    {
        QualitySettings.AntiAliasing = _antiAliasing;
        QualitySettings.ImageEffectsEnabled = _imageEffects;
        base.Dispose();
    }

    [Fact]
    public void OnValidate_SnapsMsaaSamplesAndClampsMode()
    {
        var asset = new UniversalRenderPipelineAsset
        {
            Antialiasing = (PipelineAntialiasingMode)9,
            MsaaSamples = 3,
        };
        asset.OnValidate();
        Assert.Equal(PipelineAntialiasingMode.None, asset.Antialiasing);
        Assert.Equal(4, asset.MsaaSamples);

        asset.MsaaSamples = 5;
        asset.OnValidate();
        Assert.Equal(8, asset.MsaaSamples);

        Assert.Equal(2, UniversalRenderPipelineAsset.SnapMsaaSamples(1));
        Assert.Equal(4, UniversalRenderPipelineAsset.SnapMsaaSamples(4));
        Assert.Equal(8, UniversalRenderPipelineAsset.SnapMsaaSamples(16));
    }

    [Fact]
    public void None_KeepsNativeScaleAndSevenPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        var camera = new Camera { RenderScale = 1f };
        Assert.Equal(PipelineAntialiasingMode.None, pipeline.AntialiasingMode);
        Assert.Equal(1, pipeline.MsaaSampleCount);
        Assert.Equal(1f, pipeline.GetMsaaInternalScale());
        Assert.Equal(1f, pipeline.GetEffectiveRenderScale(camera));
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    // P3.3: MSAA 4/8 used to render at 2x internal scale (2x SSAA = 4x pixel-shading cost) and
    // resolve with a downsampling blit. Real RHI multisample targets replaced that, so every MSAA
    // tier now renders at native resolution and the internal scale is always 1.
    [Fact]
    public void Msaa4_KeepsNativeScale_StillSevenPasses()
    {
        var pipeline = new UniversalRenderPipeline
        {
            AntialiasingMode = PipelineAntialiasingMode.MSAA,
            MsaaSampleCount = 4,
        };
        var camera = new Camera { RenderScale = 1f };
        Assert.Equal(4, pipeline.MsaaSampleCount);
        Assert.Equal(1f, pipeline.GetMsaaInternalScale());
        Assert.Equal(1f, pipeline.GetEffectiveRenderScale(camera));
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void Msaa8_KeepsNativeScale()
    {
        var pipeline = new UniversalRenderPipeline
        {
            AntialiasingMode = PipelineAntialiasingMode.MSAA,
            MsaaSampleCount = 8,
        };
        Assert.Equal(8, pipeline.MsaaSampleCount);
        Assert.Equal(1f, pipeline.GetMsaaInternalScale());
        Assert.Equal(1f, pipeline.GetEffectiveRenderScale(new Camera { RenderScale = 1f }));
    }

    [Fact]
    public void Msaa2_KeepsNativeScale()
    {
        var pipeline = new UniversalRenderPipeline
        {
            AntialiasingMode = PipelineAntialiasingMode.MSAA,
            MsaaSampleCount = 2,
        };
        Assert.Equal(2, pipeline.MsaaSampleCount);
        Assert.Equal(1f, pipeline.GetMsaaInternalScale());
        Assert.Equal(1f, pipeline.GetEffectiveRenderScale(new Camera { RenderScale = 1f }));
    }

    // The configurations whose single-sample interop the RHI cannot serve fall back to 1x rather
    // than producing an illegal target. These hold with no graphics device attached, which is
    // itself the "device reports no MSAA support" case.
    [Theory]
    [InlineData(CameraClearFlags.Depth, false)]
    [InlineData(CameraClearFlags.Nothing, false)]
    [InlineData(CameraClearFlags.Skybox, true)]
    [InlineData(CameraClearFlags.SolidColor, true)]
    public void Msaa_FallsBackToSingleSample_ForUnsupportedConfigurations(CameraClearFlags flags, bool editorDepthCopy)
    {
        var pipeline = new UniversalRenderPipeline
        {
            AntialiasingMode = PipelineAntialiasingMode.MSAA,
            MsaaSampleCount = 4,
        };
        Assert.Equal(1, pipeline.GetEffectiveMsaaSampleCount(flags, editorDepthCopy));
    }

    [Fact]
    public void Msaa_Off_AlwaysReportsSingleSample()
    {
        var pipeline = new UniversalRenderPipeline { AntialiasingMode = PipelineAntialiasingMode.None };
        Assert.Equal(1, pipeline.GetEffectiveMsaaSampleCount(CameraClearFlags.Skybox, needsEditorDepthCopy: false));
    }

    [Fact]
    public void Taa_InjectsEffectOnBase_SkipsOverlayAndCameraFxaa()
    {
        QualitySettings.ImageEffectsEnabled = true;
        QualitySettings.AntiAliasing = QualitySettings.AntiAliasingMode.Disabled;

        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Cam");
        scene.Add(go);
        var camera = go.AddComponent<Camera>();
        camera.RenderType = CameraRenderType.Base;

        var pipeline = new UniversalRenderPipeline { AntialiasingMode = PipelineAntialiasingMode.TAA };
        pipeline.GatherEffectsForTest(camera);
        Assert.True(pipeline.HasEffectForTest<TAAEffect>());
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());

        camera.RenderType = CameraRenderType.Overlay;
        pipeline.GatherEffectsForTest(camera);
        Assert.False(pipeline.HasEffectForTest<TAAEffect>());

        camera.RenderType = CameraRenderType.Base;
        camera.Effects.Add(new FXAAEffect());
        pipeline.GatherEffectsForTest(camera);
        Assert.False(pipeline.HasEffectForTest<TAAEffect>());
        Assert.True(pipeline.HasEffectForTest<FXAAEffect>());
    }

    [Fact]
    public void FallbackShader_ResolvesPipelineMode()
    {
        Assert.Null(UniversalRenderPipeline.ResolveFallbackAntiAliasingShader(
            PipelineAntialiasingMode.None, QualitySettings.AntiAliasingMode.Disabled, false));
        Assert.Equal(DefaultShader.FXAA, UniversalRenderPipeline.ResolveFallbackAntiAliasingShader(
            PipelineAntialiasingMode.None, QualitySettings.AntiAliasingMode.FXAA, false));
        Assert.Null(UniversalRenderPipeline.ResolveFallbackAntiAliasingShader(
            PipelineAntialiasingMode.MSAA, QualitySettings.AntiAliasingMode.FXAA, false));
        Assert.Equal(DefaultShader.TAA, UniversalRenderPipeline.ResolveFallbackAntiAliasingShader(
            PipelineAntialiasingMode.TAA, QualitySettings.AntiAliasingMode.FXAA, false));
        Assert.Null(UniversalRenderPipeline.ResolveFallbackAntiAliasingShader(
            PipelineAntialiasingMode.TAA, QualitySettings.AntiAliasingMode.Disabled, true));
    }

    [Fact]
    public void PixelPerfect_UpscaleUsesReferenceResolution()
    {
        var ppc = new PixelPerfectCamera
        {
            AssetsPPU = 100,
            RefResolutionX = 320,
            RefResolutionY = 180,
            Upscale = true,
            Snap = false,
        };
        Assert.Equal(0.5f, ppc.ComputeUpscaleRenderScale(640, 360));
        Assert.Equal(1.8f, ppc.ComputeOrthographicSize(180, 1), 3);

        var scene = CreateScene(enable: true);
        var go = CreateGameObject("Cam");
        scene.Add(go);
        var camera = go.AddComponent<Camera>();
        ppc.Apply(camera, 640, 360);
        Assert.Equal(2, ppc.Zoom);
        Assert.Equal(1.8f, camera.OrthographicSize, 3);
    }

    [Fact]
    public void BoxDownsample2x_SoftensHardEdges()
    {
        // 4×2 source: seam at x=0.5 so a 2×2 box mixes black and white (SSAA 4×).
        byte[] src = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            WritePixel(src, 4, 0, y, 0, 0, 0);
            WritePixel(src, 4, 1, y, 255, 255, 255);
            WritePixel(src, 4, 2, y, 255, 255, 255);
            WritePixel(src, 4, 3, y, 255, 255, 255);
        }

        Assert.Equal(2, CountHorizontalHardEdges(src, 4, 2, 200));

        byte[] dst = BoxDownsample2x(src, 4, 2);
        Assert.Equal(2, dst.Length / 4);
        Assert.Equal(0, CountHorizontalHardEdges(dst, 2, 1, 200));
        Assert.Equal(127, dst[0]);
        Assert.Equal(255, dst[4]);
    }

    [Fact]
    public void EchoRoundTrip_PreservesAntialiasingFields()
    {
        var original = new UniversalRenderPipelineAsset
        {
            Antialiasing = PipelineAntialiasingMode.MSAA,
            MsaaSamples = 2,
        };
        var deserialized = Serializer.Deserialize<UniversalRenderPipelineAsset>(Serializer.Serialize(typeof(object), original));
        Assert.NotNull(deserialized);
        Assert.Equal(PipelineAntialiasingMode.MSAA, deserialized!.Antialiasing);
        Assert.Equal(2, deserialized.MsaaSamples);
    }

    private static void WritePixel(byte[] rgba, int width, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * width + x) * 4;
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
        rgba[i + 3] = 255;
    }

    private static int CountHorizontalHardEdges(byte[] rgba, int width, int height, int threshold)
    {
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                int a = rgba[(y * width + x) * 4];
                int b = rgba[(y * width + x + 1) * 4];
                if (Math.Abs(a - b) > threshold)
                    count++;
            }
        }

        return count;
    }

    private static byte[] BoxDownsample2x(byte[] src, int width, int height)
    {
        int dw = width / 2;
        int dh = height / 2;
        byte[] dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                for (int oy = 0; oy < 2; oy++)
                {
                    for (int ox = 0; ox < 2; ox++)
                    {
                        int i = ((y * 2 + oy) * width + (x * 2 + ox)) * 4;
                        sumR += src[i];
                        sumG += src[i + 1];
                        sumB += src[i + 2];
                        sumA += src[i + 3];
                    }
                }

                int o = (y * dw + x) * 4;
                dst[o] = (byte)(sumR / 4);
                dst[o + 1] = (byte)(sumG / 4);
                dst[o + 2] = (byte)(sumB / 4);
                dst[o + 3] = (byte)(sumA / 4);
            }
        }

        return dst;
    }
}
