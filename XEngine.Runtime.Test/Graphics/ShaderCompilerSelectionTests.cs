// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Terrain;

using Xunit;

namespace XEngine.Runtime.Test;

[CollectionDefinition(ShaderCompilerEnvironmentCollection.Name, DisableParallelization = true)]
public sealed class ShaderCompilerEnvironmentCollection
{
    public const string Name = "Shader compiler environment";
}

[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class ShaderCompilerSelectionTests : IDisposable
{
    private readonly string? _previousMode = Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");

    public void Dispose() => Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", _previousMode);

    [Theory]
    [InlineData(null, ShaderCompilerMode.Auto)]
    [InlineData("auto", ShaderCompilerMode.Auto)]
    [InlineData("DXC", ShaderCompilerMode.Dxc)]
    [InlineData(" slang ", ShaderCompilerMode.Slang)]
    public void ParsesConfiguredMode(string? value, ShaderCompilerMode expected)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", value);

        Assert.Equal(expected, ShaderCompilerFactory.GetConfiguredMode());
    }

    [Fact]
    public void RejectsUnknownConfiguredMode()
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "other");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ShaderCompilerFactory.GetCompiler(GraphicsBackend.Vulkan, "Default/Invalid"));
        Assert.Contains("auto, dxc, or slang", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void AutoUsesSlangForEveryModernShader(GraphicsBackend backend)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "auto");

        foreach (DefaultShader shaderId in Enum.GetValues<DefaultShader>())
        {
            Shader shader = Shader.LoadDefault(shaderId);
            Assert.Equal(ShaderCompilerKind.Slang, Selected(backend, shader.Name));
            Assert.False(ShaderCompilerFactory.IsDxcFallbackAllowed(shader.Name));
        }

        Assert.Equal(ShaderCompilerKind.Slang, Selected(backend, "Custom/Unlit"));
        Assert.Equal(ShaderCompilerKind.Slang, Selected(backend, null));
        Assert.False(ShaderCompilerFactory.IsDxcFallbackAllowed("Custom/Unlit"));
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ForcedSlangFailureDoesNotFallbackToDxc(GraphicsBackend backend)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, "Default/Invalid");

        ShaderCompileResult result = compiler.Compile(InvalidRequest(backend));

        Assert.False(result.Success);
        Assert.Equal(ShaderCompilerKind.Slang, result.CompilerKind);
        Assert.Null(result.FallbackFrom);
        Assert.Null(result.FallbackReason);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void AutoFailureDoesNotUseUnreviewedDxcFallback(GraphicsBackend backend)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "auto");
        ShaderCompilerFactory.ResetTelemetryForTests();
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, "Default/Invalid");

        ShaderCompileResult result = compiler.Compile(InvalidRequest(backend));

        Assert.False(result.Success);
        Assert.Equal(ShaderCompilerKind.Slang, result.CompilerKind);
        Assert.Null(result.FallbackFrom);
        Assert.Null(result.FallbackReason);
        Assert.Contains("Slang", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        ShaderCompilerTelemetry telemetry = ShaderCompilerFactory.GetTelemetry();
        Assert.Equal(1, telemetry.SlangCompileAttempts);
        Assert.Equal(0, telemetry.DxcCompileAttempts);
        Assert.Equal(0, telemetry.SuccessfulRequests);
        Assert.Equal(1, telemetry.FailedRequests);
        Assert.Equal(0, telemetry.FallbackCount);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ForcedDxcRemainsAvailableAsExplicitFallback(GraphicsBackend backend)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "dxc");
        ShaderCompilerFactory.ResetTelemetryForTests();

        ShaderCompileResult result = ShaderCompilerFactory
            .GetCompiler(backend, "Custom/ExplicitDxc")
            .Compile(InvalidRequest(backend));

        Assert.False(result.Success);
        Assert.Equal(ShaderCompilerKind.Dxc, result.CompilerKind);
        Assert.Null(result.FallbackFrom);
        ShaderCompilerTelemetry telemetry = ShaderCompilerFactory.GetTelemetry();
        Assert.Equal(0, telemetry.SlangCompileAttempts);
        Assert.Equal(1, telemetry.DxcCompileAttempts);
        Assert.Equal(0, telemetry.FallbackCount);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Invalid)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Blit)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Unlit)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Standard)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.StandardTransparent)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Grid)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.UI)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.DefaultUI)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.DefaultText)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.DefaultTextMesh)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.ProceduralSkybox)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.GradientSkybox)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.CubemapSkybox)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Tonemapper)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.FXAA)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Particle)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Bloom)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.AutoExposure)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.GTAO)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.SSR)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.SMAA)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.BokehDoF)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.VolumetricFog)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.StandardAnisotropic)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.CinematicEffects)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.GizmoIcon)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Gizmos)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Line)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.MotionBlur)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Refraction)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.Sprite)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.TAA)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Invalid)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Blit)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Unlit)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Standard)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.StandardTransparent)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Grid)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.UI)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.DefaultUI)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.DefaultText)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.DefaultTextMesh)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.ProceduralSkybox)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.GradientSkybox)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.CubemapSkybox)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Tonemapper)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.FXAA)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Particle)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Bloom)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.AutoExposure)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.GTAO)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.SSR)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.SMAA)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.BokehDoF)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.VolumetricFog)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.StandardAnisotropic)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.CinematicEffects)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.GizmoIcon)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Gizmos)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Line)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.MotionBlur)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Refraction)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.Sprite)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.TAA)]
    public void AutoCompilesEveryDefaultShaderPassWithSlang(
        GraphicsBackend backend,
        DefaultShader shaderId)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "auto");
        Shader shader = Shader.LoadDefault(shaderId);

        foreach (ShaderPass pass in shader.Passes)
        {
            ShaderCompileResult result = ShaderCompilerFactory
                .GetCompiler(backend, shader.Name)
                .Compile(CreateRequest(backend, shader, pass));

            Assert.True(result.Success, $"{shader.Name}/{pass.Name}: {result.ErrorMessage}");
            Assert.Equal(ShaderCompilerKind.Slang, result.CompilerKind);
            Assert.Null(result.FallbackFrom);
            Assert.Equal(
                backend == GraphicsBackend.Vulkan ? ShaderBytecodeFormat.SpirV : ShaderBytecodeFormat.Dxil,
                result.Format);
            Assert.NotEmpty(result.VertexBytecode!);
            Assert.NotEmpty(result.FragmentBytecode!);
        }
    }

    [Theory]
    [InlineData(DefaultShader.Invalid)]
    [InlineData(DefaultShader.Blit)]
    [InlineData(DefaultShader.Unlit)]
    [InlineData(DefaultShader.Standard)]
    [InlineData(DefaultShader.StandardTransparent)]
    [InlineData(DefaultShader.Grid)]
    [InlineData(DefaultShader.UI)]
    [InlineData(DefaultShader.DefaultUI)]
    [InlineData(DefaultShader.DefaultText)]
    [InlineData(DefaultShader.DefaultTextMesh)]
    [InlineData(DefaultShader.ProceduralSkybox)]
    [InlineData(DefaultShader.GradientSkybox)]
    [InlineData(DefaultShader.CubemapSkybox)]
    [InlineData(DefaultShader.Tonemapper)]
    [InlineData(DefaultShader.FXAA)]
    [InlineData(DefaultShader.Bloom)]
    [InlineData(DefaultShader.AutoExposure)]
    [InlineData(DefaultShader.GTAO)]
    [InlineData(DefaultShader.SSR)]
    [InlineData(DefaultShader.SMAA)]
    [InlineData(DefaultShader.BokehDoF)]
    [InlineData(DefaultShader.VolumetricFog)]
    [InlineData(DefaultShader.StandardAnisotropic)]
    [InlineData(DefaultShader.CinematicEffects)]
    [InlineData(DefaultShader.GizmoIcon)]
    [InlineData(DefaultShader.Gizmos)]
    [InlineData(DefaultShader.Line)]
    [InlineData(DefaultShader.MotionBlur)]
    [InlineData(DefaultShader.Refraction)]
    [InlineData(DefaultShader.Sprite)]
    [InlineData(DefaultShader.TAA)]
    public void D3D12ReviewedDefaultsHaveDxcSlangRootLayoutParity(DefaultShader shaderId)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Shader shader = Shader.LoadDefault(shaderId);
        foreach (ShaderPass pass in shader.Passes)
        {
            ShaderCompileRequest request = CreateRequest(GraphicsBackend.Direct3D12, shader, pass);
            ShaderCompileResult dxc = new DxcShaderCompiler().Compile(request);
            ShaderCompileResult slang = new RuntimeSlangShaderCompiler().Compile(request);

            Assert.True(dxc.Success, $"DXC {shader.Name}/{pass.Name}: {dxc.ErrorMessage}");
            Assert.True(slang.Success, $"Slang {shader.Name}/{pass.Name}: {slang.ErrorMessage}");
            Assert.Equal(string.Empty, ShaderReflectionContractComparer.CompareLogical(dxc, slang));
        }
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ForcedSlangCompilesTonemapperRuntimeKeywords(GraphicsBackend backend)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");
        string[] keywords =
        [
            "TONEMAP_MELON",
            "TONEMAP_ACES",
            "TONEMAP_ACES_SIMPLE",
            "TONEMAP_AGX",
            "TONEMAP_REINHARD_SIMPLE",
            "TONEMAP_REINHARD_LUMA",
            "TONEMAP_REINHARD_WHITE",
            "TONEMAP_ROMBINDAHOUSE",
            "TONEMAP_UNCHARTED2",
        ];
        Shader shader = Shader.LoadDefault(DefaultShader.Tonemapper);
        ShaderPass pass = Assert.Single(shader.Passes);
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, shader.Name);
        byte[]? firstFragmentBytecode = null;
        foreach (string keyword in keywords)
        {
            var enabledKeywords = new Dictionary<string, bool>(StringComparer.Ordinal) { [keyword] = true };
            ShaderCompileResult result = compiler.Compile(CreateRequest(backend, shader, pass, enabledKeywords));
            Assert.True(result.Success, $"{backend} {keyword}: {result.ErrorMessage}");
            Assert.Equal(ShaderCompilerKind.Slang, result.CompilerKind);
            Assert.NotNull(result.FragmentBytecode);

            if (firstFragmentBytecode == null)
                firstFragmentBytecode = result.FragmentBytecode;
            else
                Assert.False(firstFragmentBytecode.AsSpan().SequenceEqual(result.FragmentBytecode), $"{backend} {keyword} compiled to the same fragment bytecode as {keywords[0]}.");
        }
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.DefaultText)]
    [InlineData(GraphicsBackend.Vulkan, DefaultShader.DefaultUI)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.DefaultText)]
    [InlineData(GraphicsBackend.Direct3D12, DefaultShader.DefaultUI)]
    public void ForcedSlangCompilesUiVertexColorShaders(GraphicsBackend backend, DefaultShader shaderId)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");

        Shader shader = Shader.LoadDefault(shaderId);
        ShaderPass pass = Assert.Single(shader.Passes);
        string vertexSource = (string)typeof(ShaderPass)
            .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
        Assert.Contains("vertexColor : COLOR0", vertexSource, StringComparison.Ordinal);

        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, shader.Name);
        ShaderCompileResult result = compiler.Compile(CreateRequest(backend, shader, pass));
        Assert.True(result.Success, $"{backend} {shader.Name}/{pass.Name}: {result.ErrorMessage}");
        Assert.Equal(ShaderCompilerKind.Slang, result.CompilerKind);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ForcedSlangCompilesTerrainRuntimeKeywords(GraphicsBackend backend)
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");
        Dictionary<string, bool>[] variants =
        [
            new(StringComparer.Ordinal) { ["GPU_INSTANCING"] = true },
            new(StringComparer.Ordinal) { ["TERRAIN_BICUBIC"] = true },
            new(StringComparer.Ordinal)
            {
                ["GPU_INSTANCING"] = true,
                ["TERRAIN_8_LAYERS"] = true,
                ["TERRAIN_BICUBIC"] = true,
            },
        ];
        Shader shader = TerrainShaderLibrary.LoadTerrainShader();
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(backend, shader.Name);
        foreach (ShaderPass pass in shader.Passes)
        {
            foreach (Dictionary<string, bool> keywords in variants)
            {
                ShaderCompileResult result = compiler.Compile(CreateRequest(backend, shader, pass, keywords));
                Assert.True(result.Success, $"{backend} {pass.Name}: {result.ErrorMessage}");
                Assert.Equal(ShaderCompilerKind.Slang, result.CompilerKind);
            }
        }
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ModernBackendsTerrainInstancedVariantsHaveDxcSlangReflectionParity(GraphicsBackend backend)
    {
        // DXC itself is only vendored for Windows, and this contract compares DXC against Slang
        // even for the Vulkan param (DXC -spirv), so any non-Windows host must skip.
        if (!OperatingSystem.IsWindows())
            return;

        var keywords = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["GPU_INSTANCING"] = true,
            ["TERRAIN_8_LAYERS"] = true,
            ["TERRAIN_BICUBIC"] = true,
        };
        Shader shader = TerrainShaderLibrary.LoadTerrainShader();
        foreach (ShaderPass pass in shader.Passes)
        {
            ShaderCompileRequest request = CreateRequest(backend, shader, pass, keywords);
            ShaderCompileResult dxc = new DxcShaderCompiler().Compile(request);
            ShaderCompileResult slang = new RuntimeSlangShaderCompiler().Compile(request);

            Assert.True(dxc.Success, $"DXC {shader.Name}/{pass.Name}: {dxc.ErrorMessage}");
            Assert.True(slang.Success, $"Slang {shader.Name}/{pass.Name}: {slang.ErrorMessage}");
            Assert.Equal(string.Empty, ShaderReflectionContractComparer.CompareLogical(dxc, slang));
        }
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, false)]
    [InlineData(GraphicsBackend.Direct3D12, false)]
    [InlineData(GraphicsBackend.Vulkan, true)]
    [InlineData(GraphicsBackend.Direct3D12, true)]
    public void ModernBackendsGrassParticleInstancedVariantsCompileAndHaveDxcSlangReflectionParity(
        GraphicsBackend backend, bool particle)
    {
        // See ModernBackendsTerrainInstancedVariantsHaveDxcSlangReflectionParity: DXC is the
        // reference compiler here and only exists on Windows.
        if (!OperatingSystem.IsWindows())
            return;

        var keywords = new Dictionary<string, bool>(StringComparer.Ordinal) { ["GPU_INSTANCING"] = true };
        Shader shader = particle ? Shader.LoadDefault(DefaultShader.Particle) : TerrainShaderLibrary.LoadGrassShader();
        foreach (ShaderPass pass in shader.Passes)
        {
            ShaderCompileRequest request = CreateRequest(backend, shader, pass, keywords);
            ShaderCompileResult dxc = new DxcShaderCompiler().Compile(request);
            ShaderCompileResult slang = new RuntimeSlangShaderCompiler().Compile(request);

            Assert.True(dxc.Success, $"DXC {shader.Name}/{pass.Name}: {dxc.ErrorMessage}");
            Assert.True(slang.Success, $"Slang {shader.Name}/{pass.Name}: {slang.ErrorMessage}");
            string differences = ShaderReflectionContractComparer.CompareLogical(dxc, slang);
            Assert.True(differences.Length == 0, $"{shader.Name}/{pass.Name}/{backend}: {differences}");
        }
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void UiSlangUniformLayoutMatchesRuntimeUploadAbi(GraphicsBackend backend)
    {
        Shader shader = Shader.LoadDefault(DefaultShader.UI);
        ShaderBindingSlot? uips = null;
        ShaderBindingSlot? uiBrushes = null;
        foreach (ShaderPass pass in shader.Passes)
        {
            ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(CreateRequest(backend, shader, pass));
            Assert.True(result.Success, $"{backend} {shader.Name}/{pass.Name}: {result.ErrorMessage}");
            foreach (ShaderBindingSlot buffer in Assert.IsType<ShaderBindingLayout>(result.BindingLayout).Buffers)
            {
                if (buffer.Name == "UIPS")
                    uips = buffer;
                else if (buffer.Name == "UiBrushes")
                    uiBrushes = buffer;
            }

            if (uips.HasValue && uiBrushes.HasValue)
                break;
        }

        // Frame-constant scalars: dpiScale c0.x, viewportSize c0.y-.z, blur c0.w, flip c1.x,
        // texture decode c1.y — matches Rendering.UIFragmentUniformsData, the uploaded struct.
        ShaderBindingSlot layout = Assert.IsType<ShaderBindingSlot>(uips);
        Assert.Equal(32, layout.ByteSize);
        Assert.Collection(
            layout.UniformFields,
            field => AssertFieldOffset(field, "dpiScale", 0),
            field => AssertFieldOffset(field, "viewportSize", 4),
            field => AssertFieldOffset(field, "backdropBlurAmount", 12),
            field => AssertFieldOffset(field, "backdropFlipY", 16),
            field => AssertFieldOffset(field, "texture0SrgbDecode", 20));

        // Brush record array: 96 records * 9 float4 (Canvas.BrushRecordsPerPage *
        // UiBrushRecord.Float4Count) fed as a whole buffer via SetBuffer("UiBrushes", ...).
        ShaderBindingSlot brushLayout = Assert.IsType<ShaderBindingSlot>(uiBrushes);
        Assert.Equal(864 * 16, brushLayout.ByteSize);
        Assert.Collection(
            brushLayout.UniformFields,
            field => AssertFieldOffset(field, "uiBrushData", 0));
    }

    private static ShaderCompilerKind Selected(string? shaderName) =>
        Selected(GraphicsBackend.Vulkan, shaderName);

    private static ShaderCompilerKind Selected(GraphicsBackend backend, string? shaderName) =>
        ShaderCompilerFactory.GetSelectedCompilerKind(backend, shaderName);

    private static ShaderCompileRequest InvalidRequest(GraphicsBackend backend) => new()
    {
        TargetBackend = backend,
        Language = ShaderLanguage.Hlsl,
        VertexSource = "float4 main( : SV_Position { return 0; }",
        FragmentSource = "float4 main() : SV_Target { return 1; }",
        ShaderName = "Default/Invalid",
        PassName = "Invalid",
    };

    /// <summary>
    /// The UI shader carries per-draw-state brush records in a float4 uniform array indexed by a
    /// vertex attribute (Quill's UiBrushRecord). That construct must survive the non-desktop
    /// Slang targets too, or the regression would only surface in a browser.
    /// <para>
    /// Metal is deliberately absent: Slang's Metal target rejects the whole UI fragment entry
    /// point because the SDF text path calls <c>fwidth</c> ("unavailable features in entry point"),
    /// which is unrelated to the brush-record array and already reproduces on the pre-record
    /// revision of this shader. Add Metal here once that pre-existing gap is closed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(GraphicsBackend.WebGPU)]
    public void UiShaderCompilesForEverySlangTargetBackend(GraphicsBackend backend)
    {
        Shader shader = Shader.LoadDefault(DefaultShader.UI);

        foreach (ShaderPass pass in shader.Passes)
        {
            ShaderCompileResult result = new RuntimeSlangShaderCompiler()
                .Compile(CreateRequest(backend, shader, pass));

            Assert.True(result.Success, $"{backend} {shader.Name}/{pass.Name}: {result.ErrorMessage}");
            Assert.NotEmpty(result.VertexBytecode!);
            Assert.NotEmpty(result.FragmentBytecode!);
        }
    }

    /// <summary>
    /// The GLSL program is authored separately from the Slang one, so the OpenGL / WebGL2 path
    /// needs its own guard that the UiBrushes uniform block and the brush-index vertex attribute
    /// still compile.
    /// </summary>
    [Fact]
    public void UiShaderCompilesForOpenGl()
    {
        Shader shader = Shader.LoadDefault(DefaultShader.UI);

        foreach (ShaderPass pass in shader.Passes)
        {
            Assert.True(pass.HasGlsl, $"{shader.Name}/{pass.Name} lost its GLSL program.");
            ShaderCompileResult result = new GlslShaderCompiler().Compile(new ShaderCompileRequest
            {
                TargetBackend = GraphicsBackend.OpenGL,
                Language = ShaderLanguage.Glsl,
                VertexSource = GetSource(pass, "_vertexSource"),
                FragmentSource = GetSource(pass, "_fragmentSource"),
                ShaderName = shader.Name,
                PassName = pass.Name,
            });

            Assert.True(result.Success, $"OpenGL {shader.Name}/{pass.Name}: {result.ErrorMessage}");
        }
    }

    private static ShaderCompileRequest CreateRequest(
        GraphicsBackend backend,
        Shader shader,
        ShaderPass pass,
        IReadOnlyDictionary<string, bool>? keywords = null) => new()
    {
        TargetBackend = backend,
        Language = ShaderLanguage.Hlsl,
        VertexSource = GetSource(pass, "_hlslVertexSource"),
        FragmentSource = GetSource(pass, "_hlslFragmentSource"),
        ShaderName = shader.Name,
        PassName = pass.Name,
        Keywords = keywords,
    };

    private static string GetSource(ShaderPass pass, string fieldName) =>
        (string)typeof(ShaderPass)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;

    private static void AssertFieldOffset(ShaderUniformField field, string name, int offset)
    {
        Assert.Equal(name, field.Name);
        Assert.Equal(offset, field.Offset);
    }
}
