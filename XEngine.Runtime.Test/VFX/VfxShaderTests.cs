using System;
using System.Buffers.Binary;
using System.Text;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.VFX;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test.VFX;

[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class VfxShaderTests
{
    private readonly ITestOutputHelper _output;

    public VfxShaderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Parser_Accepts_Packaged_VfxParticles_Shader()
    {
        string source = VfxShaderLibrary.ReadShaderSource();
        Assert.True(ShaderParser.ParseShader(VfxShaderLibrary.ShaderAssetName, source, out Shader? shader));
        Assert.NotNull(shader);
        ShaderPass init = shader!.GetPass(VfxShaderLibrary.InitPass);
        ShaderPass update = shader.GetPass(VfxShaderLibrary.UpdatePass);
        ShaderPass writeArgs = shader.GetPass(VfxShaderLibrary.WriteArgsPass);
        ShaderPass sort = shader.GetPass(VfxShaderLibrary.SortPass);
        ShaderPass output = shader.GetPass(VfxShaderLibrary.OutputPass);
        Assert.True(init.HasCompute);
        Assert.False(init.HasHlslGraphics);
        Assert.True(update.HasCompute);
        Assert.True(writeArgs.HasCompute);
        Assert.True(sort.HasCompute);
        Assert.False(sort.HasHlslGraphics);
        Assert.True(output.HasHlslGraphics);
        Assert.False(output.HasCompute);
        Assert.True(output.HasTag("RenderOrder", "Transparent"));
        Assert.Contains(shader.PropertyArray, p => p.Name == "_MainTex");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_FlipbookColumns");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_FlipbookRows");
        Assert.Contains("collisionMode", source, StringComparison.Ordinal);
        Assert.Contains("collisionPlane", source, StringComparison.Ordinal);
        Assert.Contains("sdfMode", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleSdf", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleVolumeSdf", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleCylinder", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleCone", source, StringComparison.Ordinal);
        Assert.Contains("_SdfVolume", source, StringComparison.Ordinal);
        Assert.Contains("VfxCurl", source, StringComparison.Ordinal);
        Assert.Contains("turbulenceIntensity", source, StringComparison.Ordinal);
        Assert.Contains("eventKind", source, StringComparison.Ordinal);
        Assert.Contains("VfxEventEmitCount", source, StringComparison.Ordinal);
        Assert.Contains("VfxEmitGpuEvent", source, StringComparison.Ordinal);
        Assert.Contains("VfxDepthProject", source, StringComparison.Ordinal);
        Assert.Contains("_VfxCameraDepth", source, StringComparison.Ordinal);
        Assert.Contains("conformAttraction", source, StringComparison.Ordinal);
        Assert.Contains("vectorFieldIntensity", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleVectorField", source, StringComparison.Ordinal);
        Assert.Contains("_VectorField", source, StringComparison.Ordinal);
        Assert.DoesNotContain(shader.PropertyArray, p => p.Name == "_VectorField");
        Assert.Contains("VfxApplyAttributeMap", source, StringComparison.Ordinal);
        Assert.Contains("_AttributeMap", source, StringComparison.Ordinal);
        Assert.Contains("attributeMapMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain(shader.PropertyArray, p => p.Name == "_AttributeMap");
        Assert.Contains("VfxDistortUv", source, StringComparison.Ordinal);
        Assert.Contains("_VfxSceneColor", source, StringComparison.Ordinal);
        Assert.Contains(shader.PropertyArray, p => p.Name == "_DistortionIntensity");
        Assert.DoesNotContain(shader.PropertyArray, p => p.Name == "_VfxSceneColor");
        Assert.Contains("VfxStripExtrude", source, StringComparison.Ordinal);
        Assert.Contains("VfxDecalProject", source, StringComparison.Ordinal);
        Assert.Contains("VfxSortDist2", source, StringComparison.Ordinal);
        Assert.Contains("Pass \"Sort\"", source, StringComparison.Ordinal);
        Assert.Contains("VfxCameraFade", source, StringComparison.Ordinal);
        Assert.Contains(shader.PropertyArray, p => p.Name == "_CameraFadeNear");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_CameraFadeFar");
        Assert.Contains("VfxFlipbookBlend", source, StringComparison.Ordinal);
        Assert.Contains("VfxFlipbookCell", source, StringComparison.Ordinal);
        Assert.Contains(shader.PropertyArray, p => p.Name == "_FlipbookBlend");
        Assert.Contains("VfxMotionNdcDelta", source, StringComparison.Ordinal);
        Assert.Contains("VfxMotionEncode", source, StringComparison.Ordinal);
        Assert.Contains(shader.PropertyArray, p => p.Name == "_MotionVectorMode");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_MotionVectorScale");
        Assert.Contains("VfxOrientAlongVelocity", source, StringComparison.Ordinal);
        Assert.Contains("vfxOrientAlong", source, StringComparison.Ordinal);
        Assert.Contains("// VFXGRAPH_OUTPUT_VS", source, StringComparison.Ordinal);
        Assert.Contains(shader.PropertyArray, p => p.Name == "_OrientMode");
        Assert.Contains("VfxPositionShape", source, StringComparison.Ordinal);
        Assert.Contains("VfxSequential", source, StringComparison.Ordinal);
        Assert.Contains("VfxDrag", source, StringComparison.Ordinal);
        Assert.Contains("VfxForce", source, StringComparison.Ordinal);
        Assert.Contains("VfxAngularVelocity", source, StringComparison.Ordinal);
        Assert.Contains("VfxAttract", source, StringComparison.Ordinal);
        Assert.Contains("VfxConformToSphere", source, StringComparison.Ordinal);
        Assert.Contains("VfxVortex", source, StringComparison.Ordinal);
        Assert.Contains("VfxConformToCylinder", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplySize", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyColor", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddSize", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddLifetime", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyLifetime", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddAge", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyAge", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetAge", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddColor", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddVelocity", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddPosition", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetPosition", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyVelocity", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetSize", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetColor", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetVelocity", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetLifetime", source, StringComparison.Ordinal);
        Assert.Contains("VfxOrientLookAt", source, StringComparison.Ordinal);
        Assert.Contains("VfxFlipbookFrame", source, StringComparison.Ordinal);
        Assert.Contains("vfxFlipbookStart", source, StringComparison.Ordinal);
        Assert.Contains("VfxKillPlane", source, StringComparison.Ordinal);
        Assert.Contains("VfxSpinLocal", source, StringComparison.Ordinal);
        Assert.Contains("_DecalPlane", source, StringComparison.Ordinal);
        Assert.DoesNotContain(shader.PropertyArray, p => p.Name == "_DecalPlane");
        Assert.Contains("indexCount", source, StringComparison.Ordinal);
        Assert.Contains(shader.PropertyArray, p => p.Name == "_OutputMode");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_LitMode");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_SoftParticleFade");
        Assert.Contains(shader.PropertyArray, p => p.Name == "_CameraDepthTexture");
        Assert.Contains("_LightDirection", source, StringComparison.Ordinal);
        Assert.Contains("nDotL", source, StringComparison.Ordinal);
        Assert.Contains("VfxSoftFade", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    public void Compiles_Init_Update_WriteArgs_Compute(GraphicsBackend backend, ShaderBytecodeFormat expectedFormat)
    {
        // DXIL emission requires the DXC downstream compiler (Windows-only vendor).
        if (backend == GraphicsBackend.Direct3D12 && !OperatingSystem.IsWindows())
            return;

        Shader shader = VfxShaderLibrary.LoadParticlesShader();
        CompileAndAssert(shader.GetPass(VfxShaderLibrary.InitPass), backend, expectedFormat);
        CompileAndAssert(shader.GetPass(VfxShaderLibrary.UpdatePass), backend, expectedFormat);
        CompileAndAssert(shader.GetPass(VfxShaderLibrary.WriteArgsPass), backend, expectedFormat);
        CompileAndAssert(shader.GetPass(VfxShaderLibrary.SortPass), backend, expectedFormat);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    public void Compiles_Output_Graphics(GraphicsBackend backend, ShaderBytecodeFormat expectedFormat)
    {
        // DXIL emission requires the DXC downstream compiler (Windows-only vendor).
        if (backend == GraphicsBackend.Direct3D12 && !OperatingSystem.IsWindows())
            return;

        Shader shader = VfxShaderLibrary.LoadParticlesShader();
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(
            shader.GetPass(VfxShaderLibrary.OutputPass).CreateHlslCompileRequest(backend, null, ""));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expectedFormat, result.Format);
        Assert.NotEmpty(result.VertexBytecode ?? []);
        Assert.NotEmpty(result.FragmentBytecode ?? []);
        Assert.NotNull(result.BindingLayout);
        Assert.Contains(result.BindingLayout!.Textures, t => t.Name == "_MainTex");
        Assert.Contains(result.BindingLayout.Textures, t => t.Name == "_CameraDepthTexture");
        Assert.Contains(result.BindingLayout.Samplers, s => s.Name == "_MainTexSampler");
        int mainSlot = -1;
        int depthSlot = -1;
        foreach (ShaderBindingSlot texture in result.BindingLayout.Textures)
        {
            if (texture.Name == "_MainTex")
                mainSlot = texture.Slot;
            else if (texture.Name == "_CameraDepthTexture")
                depthSlot = texture.Slot;
        }
        Assert.True(mainSlot >= 2, $"_MainTex must sit after particle SRVs t0/t1, got t{mainSlot}");
        Assert.True(depthSlot >= 3, $"_CameraDepthTexture must sit after _MainTex t2, got t{depthSlot}");
        _output.WriteLine($"Output {backend}: VS {result.VertexBytecode!.Length} FS {result.FragmentBytecode!.Length} _MainTex t{mainSlot} depth t{depthSlot}");
    }

    private void CompileAndAssert(ShaderPass pass, GraphicsBackend backend, ShaderBytecodeFormat expectedFormat)
    {
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(
            pass.CreateHlslCompileRequest(backend, null, ""));
        Assert.True(result.Success, $"{pass.Name}: {result.ErrorMessage}");
        Assert.Equal(expectedFormat, result.Format);
        Assert.NotEmpty(result.ComputeBytecode ?? []);
        if (expectedFormat == ShaderBytecodeFormat.SpirV)
        {
            Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(result.ComputeBytecode!));
        }
        else
        {
            Assert.Equal("DXBC", Encoding.ASCII.GetString(result.ComputeBytecode!, 0, 4));
        }

        _output.WriteLine($"{pass.Name} {backend}: {result.ComputeBytecode!.Length} B");
    }
}
