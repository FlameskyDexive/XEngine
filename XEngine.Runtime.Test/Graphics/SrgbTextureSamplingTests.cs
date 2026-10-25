using ImageMagick;
using XEngine.Echo;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class SrgbTextureSamplingTests
{
    [Theory]
    [InlineData(true, TextureImageFormat.Color4bSrgb)]
    [InlineData(false, TextureImageFormat.Color4b)]
    public void ImportedTexture_CacheRoundTripPreservesSamplingFormatAndColorIntent(bool sampling, TextureImageFormat format)
    {
        using var image = new MagickImage(MagickColors.Gray, 2, 1);
        using Texture2D imported = Texture2D.FromImage(image, srgbSampling: sampling);
        imported.Srgb = sampling;
        Assert.Equal(format, imported.ImageFormat);
        EchoObject data = Serializer.Serialize(imported, TypeMode.Aggressive);
        using Texture2D reloaded = Serializer.Deserialize<Texture2D>(data)!;
        Assert.Equal(format, reloaded.ImageFormat);
        Assert.Equal(sampling, reloaded.Srgb);
        Assert.Equal(8, reloaded.GetSize());
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void GpuSrgbSampling_DecodesRgbBeforeFilteringAndLeavesAlphaLinear(GraphicsBackend backend)
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            Graphics.Initialize(debug: false);
            // The process-wide fullscreen mesh belongs to the previous test's graphics device.
            Mesh.GetFullscreenQuad().Dispose();
            Assert.Equal(backend, Graphics.Device!.Backend);
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string source = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Assets", "Defaults", "Blit.shader"))
                .Replace("Blend Alpha", "Blend Off");
            Assert.True(ShaderParser.ParseShader("SrgbSampling.shader", source, _ => null, out Shader? parsed));
            using Shader shader = parsed!;
            using var material = new Material(shader);
            using var texture = new Texture2D(2, 1, false, TextureImageFormat.Color4bSrgb);
            byte[] pixels = [128, 128, 128, 64, 255, 255, 255, 192];
            texture.SetData<byte>(pixels.AsMemory());
            texture.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
            using var target = new RenderTexture(1, 1, false, [TextureImageFormat.Color4b]);
            Graphics.BeginFrame();
            using (CommandBuffer cmd = Graphics.GetCommandBuffer("sRGB sample and filter regression"))
            {
                cmd.Blit(texture, target, material);
                Graphics.Submit(cmd);
            }
            Graphics.EndFrameAndWait();
            Graphics.Device.WaitIdle();
            byte[] result = new byte[4];
            target.MainTexture.GetData<byte>(result.AsMemory());
            // ((128/255 decoded to linear) + 1) / 2 = .60793 => 155/255.
            // Decoding after filtering would yield 134, and missing decoding would yield 192.
            for (int channel = 0; channel < 3; channel++) Assert.InRange(result[channel], (byte)154, (byte)156);
            Assert.InRange(result[3], (byte)127, (byte)129);
            VerifyNeutralTonemapperRows();
            VerifyNeutralTonemapperLowIntensities();
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            Mesh.GetFullscreenQuad().Dispose();
            Graphics.Dispose();
            GraphicsBackendSelection.Preferred = previous;
        }
    }

    private static void VerifyNeutralTonemapperRows()
    {
        using var input = new RenderTexture(2, 2, false, [TextureImageFormat.Color4b]);
        byte[] pixels = [64, 0, 0, 255, 0, 64, 0, 255, 0, 0, 64, 255, 64, 64, 0, 255];
        input.MainTexture.SetData<byte>(pixels.AsMemory());
        using var context = new RenderContext { SceneColor = input, Width = 2, Height = 2, CurrentStage = RenderStage.PostProcess };
        var effect = new TonemapperEffect { Type = TonemapperType.None, Contrast = 1, Saturation = 1, UseVolumeOverrides = false };
        try
        {
            Graphics.BeginFrame();
            effect.OnRenderEffect(context);
            Graphics.EndFrameAndWait();
            Graphics.Device!.WaitIdle();
            byte[] result = new byte[16];
            context.SceneColor.MainTexture.GetData<byte>(result.AsMemory());
            for (int i = 0; i < pixels.Length; i++)
            {
                int expected = pixels[i] == 64 ? 137 : pixels[i];
                Assert.InRange((int)result[i], Math.Max(0, expected - 1), Math.Min(255, expected + 1));
            }
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            effect.OnDisable();
            RenderTexture.ReleaseTemporaryRT(context.SceneColor);
            RenderTexture.DisposeTemporaryPool();
        }
    }

    private static void VerifyNeutralTonemapperLowIntensities()
    {
        using var input = new RenderTexture(2, 1, false, [TextureImageFormat.Short4]);
        Half[] pixels = [(Half).002321982f, (Half).002708978f, (Half).00393594f, (Half)1,
            (Half).001f, (Half).0001f, (Half)0f, (Half)1];
        input.MainTexture.SetData<Half>(pixels.AsMemory());
        using var context = new RenderContext { SceneColor = input, Width = 2, Height = 1, CurrentStage = RenderStage.PostProcess };
        var effect = new TonemapperEffect { Type = TonemapperType.None, Contrast = 1, Saturation = 1, UseVolumeOverrides = false };
        try
        {
            Graphics.BeginFrame();
            effect.OnRenderEffect(context);
            Graphics.EndFrameAndWait();
            Graphics.Device!.WaitIdle();
            byte[] result = new byte[8];
            context.SceneColor.MainTexture.GetData<byte>(result.AsMemory());
            Assert.Equal(new byte[] { 8, 9, 13, 255, 3, 0, 0, 255 }, result);
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            effect.OnDisable();
            RenderTexture.ReleaseTemporaryRT(context.SceneColor);
            RenderTexture.DisposeTemporaryPool();
        }
    }
}
