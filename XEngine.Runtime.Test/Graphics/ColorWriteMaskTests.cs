using XEngine.Echo;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ColorWriteMaskTests
{
    [Theory]
    [InlineData("RGB", RasterizerState.ColorChannels.RGB)]
    [InlineData("A", RasterizerState.ColorChannels.Alpha)]
    [InlineData("0", RasterizerState.ColorChannels.None)]
    [InlineData("RGBA", RasterizerState.ColorChannels.All)]
    public void ShaderCacheRoundTrip_PreservesAuthoredMask(string mask, RasterizerState.ColorChannels expected)
    {
        using Shader shader = CreateShader(mask);
        Assert.Equal(expected, Assert.Single(shader.Passes).State.ColorMask);
        EchoObject data = Serializer.Serialize(shader, TypeMode.Aggressive);
        using Shader reloaded = Serializer.Deserialize<Shader>(data)!;
        Assert.Equal(expected, Assert.Single(reloaded.Passes).State.ColorMask);
    }

    [Fact]
    public void LegacyAndZeroInitializedStates_WriteAllChannels()
    {
        Assert.Equal(RasterizerState.ColorChannels.All, default(RasterizerState).ColorMask);
        Assert.Equal(RasterizerState.ColorChannels.All, new RasterizerState().ColorMask);
        RasterizerState legacy = Serializer.Deserialize<RasterizerState>(EchoObject.NewCompound());
        Assert.Equal(RasterizerState.ColorChannels.All, legacy.ColorMask);
    }

    [Fact]
    public void PipelineCache_DistinguishesColorMaskForTheSameShaderAndGeometry()
    {
        var bytecode = new CompiledShaderBytecode(ShaderLanguage.Hlsl, ShaderBytecodeFormat.SpirV,
            [0x03, 0x02, 0x23, 0x07], [0x03, 0x02, 0x23, 0x07]);
        using var variant = new ShaderVariant(bytecode);
        var keys = new HashSet<GraphicsPipelineKey>();
        foreach (RasterizerState.ColorChannels mask in new[] { RasterizerState.ColorChannels.All,
            RasterizerState.ColorChannels.RGB, RasterizerState.ColorChannels.Alpha, RasterizerState.ColorChannels.None })
        {
            var raster = new RasterizerState { ColorMask = mask };
            Assert.True(keys.Add(new GraphicsPipelineKey(variant, 1, Topology.Triangles, raster, false)));
        }
        Assert.Equal(4, keys.Count);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void GpuColorMask_PreservesDisabledChannelsDuringAdditiveBlending(GraphicsBackend backend)
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            Graphics.Initialize(debug: false);
            Mesh.GetFullscreenQuad().Dispose();
            Assert.Equal(backend, Graphics.Device!.Backend);
            using var texture = new Texture2D(1, 1);
            byte[] source = [32, 64, 96, 128];
            byte[] background = [16, 24, 32, 64];
            texture.SetData<byte>(source.AsMemory());
            foreach (string mask in new[] { "RGB", "A", "0", "RGBA" })
            {
                using Shader shader = CreateShader(mask);
                using var material = new Material(shader);
                using var target = new RenderTexture(1, 1, false, [TextureImageFormat.Color4b]);
                target.MainTexture.SetData<byte>(background.AsMemory());
                Graphics.BeginFrame();
                using (CommandBuffer cmd = Graphics.GetCommandBuffer("ColorMask additive pixel regression"))
                {
                    cmd.Blit(texture, target, material);
                    Graphics.Submit(cmd);
                }
                Graphics.EndFrameAndWait();
                Graphics.Device.WaitIdle();
                byte[] result = new byte[4];
                target.MainTexture.GetData<byte>(result.AsMemory());
                for (int channel = 0; channel < 4; channel++)
                {
                    bool enabled = mask.Contains("RGBA"[channel]);
                    int expected = background[channel] + (enabled ? source[channel] : 0);
                    Assert.InRange((int)result[channel], Math.Max(0, expected - 1), Math.Min(255, expected + 1));
                }
            }
        }
        catch (Exception ex) when (IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader/DXC) —
            // same availability contract as the RhiContractTests optional GPU suites.
            return;
        }
        finally
        {
            Mesh.GetFullscreenQuad().Dispose();
            Graphics.Dispose();
            GraphicsBackendSelection.Preferred = previous;
        }
    }

    private static bool IsBackendUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException
                or FileNotFoundException)
            {
                return true;
            }

            if (current is InvalidOperationException &&
                (current.Message.StartsWith("Failed to create Vulkan instance. Vulkan is unavailable", StringComparison.Ordinal) ||
                 current.Message.StartsWith("Failed to create Direct3D 12 device. D3D12 is unavailable", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static Shader CreateShader(string mask)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string source = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Assets", "Defaults", "Blit.shader"))
            // Blit.shader ships with "Blend Off" (see 8a1ddb50b); anchor the rewrite there so the
            // ColorMask probe actually lands in the pass state regardless of the default blend.
            .Replace("Blend Off", "Blend Additive\n    ColorMask " + mask);
        Assert.True(ShaderParser.ParseShader("ColorMask.shader", source, _ => null, out Shader? shader));
        return shader!;
    }
}
