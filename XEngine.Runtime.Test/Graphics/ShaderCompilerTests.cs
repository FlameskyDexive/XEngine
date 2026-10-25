// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Terrain;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Stage C shader-pipeline: dual-language parsing, GLSL compile path, and DXC discovery.
/// </summary>
public class ShaderCompilerTests
{
    private const string DualLanguageShader = """
Shader "Test/Dual"

Pass "Main"
{
    GLSLPROGRAM
    Vertex
    {
        void main() { gl_Position = vec4(0.0); }
    }
    Fragment
    {
        layout(location = 0) out vec4 c;
        void main() { c = vec4(1.0); }
    }
    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        float4 main() : SV_Position { return float4(0, 0, 0, 1); }
    }
    Fragment
    {
        float4 main() : SV_Target { return float4(1, 1, 1, 1); }
    }
    ENDSLANG
}
""";

    [Fact]
    public void Parser_Accepts_SlangProgram_Alongside_Glsl()
    {
        bool ok = ShaderParser.ParseShader("test.shader", DualLanguageShader, out Shader? shader);
        Assert.True(ok);
        Assert.NotNull(shader);

        ShaderPass pass = shader!.GetPass(0);
        Assert.True(pass.HasGlsl);
        Assert.True(pass.HasHlsl);
        Assert.True(pass.HasSlang);
        Assert.Equal(ShaderModernSourceKind.Slang, pass.ModernSourceKind);

        string hlslVert = (string)typeof(ShaderPass)
            .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
        Assert.Contains("SV_Position", hlslVert);
    }

    [Fact]
    public void Parser_Accepts_Compute_Only_Slang_Pass()
    {
        const string computeShader = """
Shader "Test/Compute"

Pass "Init"
{
    SLANGPROGRAM
    Compute
    {
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) { }
    }
    ENDSLANG
}
""";

        Assert.True(ShaderParser.ParseShader("compute.shader", computeShader, out Shader? shader));
        ShaderPass pass = Assert.IsType<Shader>(shader).GetPass(0);
        Assert.True(pass.HasCompute);
        Assert.True(pass.HasHlsl);
        Assert.False(pass.HasHlslGraphics);
        Assert.False(pass.HasGlsl);
        Assert.True(pass.HasSlang);
    }

    [Fact]
    public void Parser_Accepts_Legacy_HlslProgram_For_Explicit_Rollback()
    {
        string legacyShader = DualLanguageShader
            .Replace("SLANGPROGRAM", "HLSLPROGRAM", StringComparison.Ordinal)
            .Replace("ENDSLANG", "ENDHLSL", StringComparison.Ordinal);

        Assert.True(ShaderParser.ParseShader("legacy.shader", legacyShader, out Shader? shader));
        ShaderPass pass = Assert.IsType<Shader>(shader).GetPass(0);
        Assert.True(pass.HasHlsl);
        Assert.False(pass.HasSlang);
        Assert.Equal(ShaderModernSourceKind.LegacyHlsl, pass.ModernSourceKind);
    }

    [Fact]
    public void Parser_StillParses_Blit_And_Standard_Glsl()
    {
        Shader blit = Shader.LoadDefault(DefaultShader.Blit);
        Assert.NotNull(blit);
        Assert.True(blit.GetPass(0).HasGlsl);
        Assert.True(blit.GetPass(0).HasHlsl);

        Shader standard = Shader.LoadDefault(DefaultShader.Standard);
        Assert.NotNull(standard);
        Assert.True(standard.GetPass(0).HasGlsl);
        Assert.True(standard.GetPass(0).HasHlsl);
        Assert.Contains("Standard", standard.GetPass(0).Name);
    }

    [Fact]
    public void GlslShaderCompiler_Injects_Version_And_Defines()
    {
        var compiler = new GlslShaderCompiler();
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.OpenGL,
            Language = ShaderLanguage.Glsl,
            VertexSource = "void main() {}",
            FragmentSource = "void main() {}",
            Keywords = new System.Collections.Generic.Dictionary<string, bool>
            {
                ["FOO"] = true,
                ["BAR"] = false,
            },
        });

        Assert.True(result.Success);
        Assert.Equal(ShaderBytecodeFormat.GlslSource, result.Format);
        Assert.Contains("#version 410", result.GlslVertexSource);
        Assert.Contains("#define FOO", result.GlslVertexSource);
        Assert.DoesNotContain("#define BAR", result.GlslVertexSource!);
    }

    [Fact]
    public void DxcShaderCompiler_Reports_NotFound_Or_Compiles_Tiny_Hlsl()
    {
        DxcShaderCompiler.ResetLocateCache();
        var compiler = new DxcShaderCompiler();

        string tinyVs = """
float4 main() : SV_Position
{
    return float4(0, 0, 0, 1);
}
""";
        string tinyPs = """
float4 main() : SV_Target
{
    return float4(1, 0, 1, 1);
}
""";

        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Direct3D12,
            Language = ShaderLanguage.Hlsl,
            VertexSource = tinyVs,
            FragmentSource = tinyPs,
        });

        if (compiler.ResolvedDxcPath == null)
        {
            Assert.False(result.Success);
            Assert.Contains("dxc", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
            return;
        }

        // Prefer DXIL (always available on Windows Kits DXC). Also try Vulkan/SPIR-V when the
        // located DXC was built with ENABLE_SPIRV_CODEGEN.
        if (result.Success)
        {
            Assert.Equal(ShaderBytecodeFormat.Dxil, result.Format);
            Assert.NotNull(result.VertexBytecode);
            Assert.NotNull(result.FragmentBytecode);
            Assert.True(result.VertexBytecode!.Length > 0);
            Assert.True(result.FragmentBytecode!.Length > 0);
            return;
        }

        // DXC present but compile failed — must be a clear error, never an unhandled throw.
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));

        ShaderCompileResult spirv = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Vulkan,
            Language = ShaderLanguage.Hlsl,
            VertexSource = tinyVs,
            FragmentSource = tinyPs,
        });

        if (spirv.Success)
        {
            Assert.Equal(ShaderBytecodeFormat.SpirV, spirv.Format);
            Assert.True(spirv.VertexBytecode!.Length > 0);
        }
        else
        {
            Assert.Contains("dxc", spirv.ErrorMessage! + result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DxcShaderCompiler_Finds_Vendored_Binary_From_Trailing_Base_Directory()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string baseDirectory = AppContext.BaseDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

        string? dxcPath = DxcShaderCompiler.FindVendoredDxc(baseDirectory);

        Assert.NotNull(dxcPath);
        Assert.EndsWith(
            System.IO.Path.Combine("ThirdParty", "DXC", "windows-x64", "bin", "x64", "dxc.exe"),
            dxcPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShaderCompilerFactory_Picks_By_Backend()
    {
        Assert.IsType<GlslShaderCompiler>(ShaderCompilerFactory.GetCompiler(GraphicsBackend.OpenGL));
        Assert.IsType<GlslShaderCompiler>(ShaderCompilerFactory.GetCompiler(GraphicsBackend.Null));
        Assert.Equal(
            ShaderCompilerKind.Slang,
            ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Vulkan));
        Assert.Equal(
            ShaderCompilerKind.Slang,
            ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Direct3D12));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.Vulkan));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.Direct3D12));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.Metal));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.WebGL2));
        Assert.False(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.OpenGL));
        Assert.Equal(
            ShaderCompilerKind.Slang,
            ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Metal));
        Assert.Equal(
            ShaderCompilerKind.Slang,
            ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.WebGL2));
    }

    [Fact]
    public void DxcShaderCompiler_Removes_Explicit_Vulkan_Bindings_Before_Shifts()
    {
        const string source = "[[vk::location(0)]] float2 uv : TEXCOORD0; [[vk::binding(3, 2)]] Texture2D Main : register(t3);";

        string normalized = DxcShaderCompiler.RemoveExplicitVulkanBindings(source);

        Assert.Contains("[[vk::location(0)]]", normalized);
        Assert.DoesNotContain("vk::binding", normalized);
        Assert.Contains("Texture2D Main : register(t3);", normalized);
    }

    [Fact]
    public void DxcShaderCompiler_Maps_Vertex_Semantics_To_Dxc_Locations()
    {
        const string source = "struct VSInput { float3 position : POSITION; float2 uv : TEXCOORD0; float3 normal : NORMAL; float4 tangent : TANGENT; [[vk::location(8)]] float4 instanceRow : TEXCOORD8; };";

        ShaderVertexInputLayout layout = DxcShaderCompiler.ParseVertexInputLayout(source);

        Assert.True(layout.TryGetLocation((uint)VertexFormat.VertexSemantic.Position, out uint position));
        Assert.True(layout.TryGetLocation((uint)VertexFormat.VertexSemantic.Tangent, out uint tangent));
        Assert.True(layout.TryGetLocation(8, out uint instanceRow));
        Assert.Equal(0u, position);
        Assert.Equal(3u, tangent);
        Assert.Equal(8u, instanceRow);
    }

    [Fact]
    public void DxcShaderCompiler_Assigns_All_Vulkan_Vertex_Input_Locations()
    {
        const string source = "struct VSInput { float3 position : POSITION; float2 uv : TEXCOORD0; [[vk::location(8)]] float4 instanceRow : TEXCOORD8; };";
        ShaderVertexInputLayout layout = DxcShaderCompiler.ParseVertexInputLayout(source);

        string normalized = DxcShaderCompiler.ApplyVulkanVertexInputLocations(source, layout);

        Assert.Contains("[[vk::location(0)]] float3 position : POSITION;", normalized);
        Assert.Contains("[[vk::location(1)]] float2 uv : TEXCOORD0;", normalized);
        Assert.Contains("[[vk::location(8)]] float4 instanceRow : TEXCOORD8;", normalized);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(normalized, "vk::location").Count);
    }

    [Fact]
    public void DxcBindingReflection_Merges_Stages_And_Uses_Explicit_Registers()
    {
        const string vertexSource = """
cbuffer GlobalUniforms : register(b0) { float4x4 ViewProjection; };
cbuffer ObjectUniforms : register(b2) { float4x4 Model; };
Texture2D SharedTexture : register(t3);
SamplerState SharedSampler : register(s3);
float4 SampleHelper(Texture2D tex, SamplerState sampler, float2 uv) { return tex.Sample(sampler, uv); }
""";
        const string fragmentSource = """
cbuffer GlobalUniforms : register(b0) { float4 CameraPosition; };
Texture2D SharedTexture : register(t3);
TextureCube Environment : register(t1);
SamplerState SharedSampler : register(s3);
SamplerComparisonState ShadowSampler : register(s1);
""";

        ShaderBindingLayout layout = DxcShaderCompiler.ParseBindingLayout(vertexSource, fragmentSource);

        Assert.Collection(
            layout.Textures,
            slot => AssertBinding(slot, ShaderBindingKind.Texture, 1, "Environment"),
            slot => AssertBinding(slot, ShaderBindingKind.Texture, 3, "SharedTexture"));
        Assert.Collection(
            layout.Buffers,
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 0, "GlobalUniforms"),
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 2, "ObjectUniforms"));
        Assert.Collection(
            layout.Samplers,
            slot => AssertBinding(slot, ShaderBindingKind.Sampler, 1, "ShadowSampler"),
            slot => AssertBinding(slot, ShaderBindingKind.Sampler, 3, "SharedSampler"));
    }

    [Fact]
    public void DxcBindingReflection_Rejects_Conflicting_CrossStage_Slots()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DxcShaderCompiler.ParseBindingLayout(
                "Texture2D Shared : register(t0);",
                "Texture2D Shared : register(t1);"));

        Assert.Contains("conflicting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DxcBindingReflection_Rejects_Different_Names_At_One_Slot()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DxcShaderCompiler.ParseBindingLayout(
                "cbuffer VertexMaterial : register(b2) { float4 Tint; };",
                "cbuffer FragmentMaterial : register(b2) { float4 Tint; };"));

        Assert.Contains("both", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VertexMaterial", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FragmentMaterial", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DxcBindingReflection_Preserves_Spaces_Arrays_And_Stages()
    {
        const string source = "Texture2D SharedTextures[3] : register(t5, space2);";

        ShaderBindingLayout layout = DxcShaderCompiler.ParseBindingLayout(source, source);

        ShaderBindingSlot binding = Assert.Single(layout.Textures);
        AssertBinding(binding, ShaderBindingKind.Texture, 5, "SharedTextures");
        Assert.Equal(2, binding.Space);
        Assert.Equal(3, binding.Count);
        Assert.Equal(ShaderStageMask.Vertex | ShaderStageMask.Fragment, binding.Stages);
    }

    [Fact]
    public void DxcBindingReflection_Rejects_Overlapping_Array_Ranges()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            DxcShaderCompiler.ParseBindingLayout(
                "Texture2D VertexTextures[2] : register(t5, space3);",
                "Texture2D FragmentTexture : register(t6, space3);"));

        Assert.Contains("assigned to both", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VertexTextures", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FragmentTexture", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Critical_Shaders_Have_Dual_Sources()
    {
        DefaultShader[] critical =
        [
            DefaultShader.Blit,
            DefaultShader.Unlit,
            DefaultShader.Invalid,
            DefaultShader.Standard,
            DefaultShader.Grid,
            DefaultShader.Gizmos,
            DefaultShader.GizmoIcon,
            DefaultShader.DefaultUI,
            DefaultShader.DefaultText,
            DefaultShader.DefaultTextMesh,
            DefaultShader.Sprite,
            DefaultShader.Line,
            DefaultShader.Particle,
            DefaultShader.UI,
            DefaultShader.ProceduralSkybox,
            DefaultShader.GradientSkybox,
            DefaultShader.Tonemapper,
        ];

        foreach (DefaultShader id in critical)
        {
            Shader shader = Shader.LoadDefault(id);
            Assert.NotNull(shader);
            foreach (ShaderPass pass in shader.Passes)
            {
                Assert.True(pass.HasGlsl, $"{id}/{pass.Name} missing GLSL");
                Assert.True(pass.HasHlsl, $"{id}/{pass.Name} missing HLSL");
            }
        }
    }

    [Fact]
    public void Critical_Modern_Shader_Binding_Layouts_Are_Collision_Free()
    {
        DefaultShader[] critical =
        [
            DefaultShader.Blit,
            DefaultShader.Unlit,
            DefaultShader.Invalid,
            DefaultShader.Standard,
            DefaultShader.Grid,
            DefaultShader.Gizmos,
            DefaultShader.GizmoIcon,
            DefaultShader.DefaultUI,
            DefaultShader.DefaultText,
            DefaultShader.DefaultTextMesh,
            DefaultShader.Sprite,
            DefaultShader.Line,
            DefaultShader.Particle,
            DefaultShader.UI,
            DefaultShader.ProceduralSkybox,
            DefaultShader.GradientSkybox,
            DefaultShader.Tonemapper,
        ];

        foreach (DefaultShader id in critical)
        {
            Shader shader = Shader.LoadDefault(id);
            foreach (ShaderPass pass in shader.Passes)
            {
                string vertexSource = (string)typeof(ShaderPass)
                    .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(pass)!;
                string fragmentSource = (string)typeof(ShaderPass)
                    .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(pass)!;

                Exception? error = Record.Exception(() => DxcShaderCompiler.ParseBindingLayout(vertexSource, fragmentSource));
                Assert.True(error == null, $"{id}/{pass.Name}: {error?.Message}");
            }
        }
    }

    [Fact]
    public void Standard_Prepass_Hlsl_Uses_Shared_Material_Buffer_Layout()
    {
        Shader shader = Shader.LoadDefault(DefaultShader.Standard);
        ShaderPass pass = shader.GetPass("Prepass");
        string vertexSource = (string)typeof(ShaderPass)
            .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
        string fragmentSource = (string)typeof(ShaderPass)
            .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;

        ShaderBindingLayout layout = DxcShaderCompiler.ParseBindingLayout(vertexSource, fragmentSource);

        Assert.Collection(
            layout.Buffers,
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 0, "GlobalUniforms"),
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 1, "ObjectUniforms"),
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 2, "PrepassMaterial"),
            // Blend-shape scalars: declared in the shared XEngineCG include under BLENDSHAPES.
            slot => AssertBinding(slot, ShaderBindingKind.Buffer, 7, "MorphParams"));
        Assert.DoesNotContain(layout.Buffers, slot => slot.Name == "PrepassVS" || slot.Name == "PrepassPS");
        string[] materialFields = ["float2 _Tiling", "float2 _Offset", "float4 _MainColor", "float _AlphaCutoff"];
        foreach (string field in materialFields)
        {
            Assert.Contains(field, vertexSource);
            Assert.Contains(field, fragmentSource);
        }
    }

    [Fact]
    public void Standard_Prepass_Vulkan_Material_Layout_Has_Stable_Field_Offsets()
        => AssertCompiledPrepassMaterialLayout(GraphicsBackend.Vulkan);

    [Rhi.MacOSMetalFact]
    public void Standard_Prepass_Metal_Material_Layout_Has_Stable_Field_Offsets()
        => AssertCompiledPrepassMaterialLayout(GraphicsBackend.Metal);

    private static void AssertCompiledPrepassMaterialLayout(GraphicsBackend backend)
    {
        ShaderPass pass = Shader.LoadDefault(DefaultShader.Standard).GetPass("Prepass");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var compiler = new RuntimeSlangShaderCompiler();
        ShaderCompileResult compiled = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = (string)typeof(ShaderPass).GetField("_hlslVertexSource", flags)!.GetValue(pass)!,
            FragmentSource = (string)typeof(ShaderPass).GetField("_hlslFragmentSource", flags)!.GetValue(pass)!,
        });
        Assert.True(compiled.Success, compiled.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(compiled.BindingLayout);
        ShaderBindingSlot material = Assert.Single(layout.Buffers, slot => slot.Name == "PrepassMaterial");
        Assert.Equal(2, material.Slot);
        Assert.Equal(ShaderStageMask.Vertex | ShaderStageMask.Fragment, material.Stages);
        Assert.Equal(48, material.ByteSize);
        Assert.Collection(material.UniformFields,
            field => Assert.Equal(new ShaderUniformField("_Tiling", 0, 8, ShaderScalarType.Float32, 1, 2, 1, 0, 0), field),
            field => Assert.Equal(new ShaderUniformField("_Offset", 8, 8, ShaderScalarType.Float32, 1, 2, 1, 0, 0), field),
            field => Assert.Equal(new ShaderUniformField("_MainColor", 16, 16, ShaderScalarType.Float32, 1, 4, 1, 0, 0), field),
            field => Assert.Equal(new ShaderUniformField("_AlphaCutoff", 32, 4, ShaderScalarType.Float32, 1, 1, 1, 0, 0), field));
    }

    [Fact]
    public void Standard_Prepass_Hlsl_Compiles_For_Modern_Backends_When_Dxc_Is_Available()
    {
        var compiler = new DxcShaderCompiler();
        if (compiler.ResolvedDxcPath == null)
            return;

        Shader shader = Shader.LoadDefault(DefaultShader.Standard);
        ShaderPass pass = shader.GetPass("Prepass");
        string vertexSource = (string)typeof(ShaderPass)
            .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
        string fragmentSource = (string)typeof(ShaderPass)
            .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;

        ShaderCompileResult d3d12 = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Direct3D12,
            Language = ShaderLanguage.Hlsl,
            VertexSource = vertexSource,
            FragmentSource = fragmentSource,
        });
        Assert.True(d3d12.Success, d3d12.ErrorMessage);
        Assert.Equal(ShaderBytecodeFormat.Dxil, d3d12.Format);

        ShaderCompileResult vulkan = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Vulkan,
            Language = ShaderLanguage.Hlsl,
            VertexSource = vertexSource,
            FragmentSource = fragmentSource,
        });
        if (!vulkan.Success && vulkan.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
            return;

        Assert.True(vulkan.Success, vulkan.ErrorMessage);
        Assert.Equal(ShaderBytecodeFormat.SpirV, vulkan.Format);
    }

    [Fact]
    public void All_Default_Passes_Compile_For_Modern_Backends_When_Dxc_Is_Available()
    {
        var compiler = new DxcShaderCompiler();
        if (compiler.ResolvedDxcPath == null)
            return;

        var slang = new RuntimeSlangShaderCompiler();
        bool spirvUnavailable = false;
        int shaderCount = 0;
        int passCount = 0;
        foreach (DefaultShader shaderId in Enum.GetValues<DefaultShader>())
        {
            shaderCount++;
            Shader shader = Shader.LoadDefault(shaderId);
            foreach (ShaderPass pass in shader.Passes)
            {
                passCount++;
                Assert.True(pass.HasHlsl, $"{shaderId}/{pass.Name} missing HLSL");

                if (!pass.HasHlslGraphics)
                {
                    // Compute-only pass. DXC has no compute pipeline path; modern backends always
                    // compile compute through Slang (ShaderCompilerFactory Auto selects Slang and
                    // WebGPU/Metal ignore XENGINE_SHADER_COMPILER), so gate the Slang compile here.
                    Assert.True(pass.HasCompute, $"{shaderId}/{pass.Name} has neither graphics nor compute HLSL");
                    string computeSource = (string)typeof(ShaderPass)
                        .GetField("_hlslComputeSource", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .GetValue(pass)!;
                    foreach (GraphicsBackend backend in new[] { GraphicsBackend.Direct3D12, GraphicsBackend.Vulkan })
                    {
                        ShaderCompileResult compute = slang.Compile(new ShaderCompileRequest
                        {
                            TargetBackend = backend,
                            Language = ShaderLanguage.Hlsl,
                            VertexSource = "",
                            FragmentSource = "",
                            ComputeSource = computeSource,
                        });
                        Assert.True(compute.Success, $"{shaderId}/{pass.Name} [{backend}]: {compute.ErrorMessage}");
                        Assert.Equal(
                            backend == GraphicsBackend.Vulkan ? ShaderBytecodeFormat.SpirV : ShaderBytecodeFormat.Dxil,
                            compute.Format);
                        Assert.NotNull(compute.ComputeBytecode);
                        Assert.NotEmpty(compute.ComputeBytecode);
                    }

                    continue;
                }

                string vertexSource = (string)typeof(ShaderPass)
                    .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(pass)!;
                string fragmentSource = (string)typeof(ShaderPass)
                    .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(pass)!;

                ShaderCompileResult d3d12 = compiler.Compile(new ShaderCompileRequest
                {
                    TargetBackend = GraphicsBackend.Direct3D12,
                    Language = ShaderLanguage.Hlsl,
                    VertexSource = vertexSource,
                    FragmentSource = fragmentSource,
                });
                Assert.True(d3d12.Success, $"{shaderId}/{pass.Name}: {d3d12.ErrorMessage}");
                Assert.Equal(ShaderBytecodeFormat.Dxil, d3d12.Format);

                if (spirvUnavailable)
                    continue;

                ShaderCompileResult vulkan = compiler.Compile(new ShaderCompileRequest
                {
                    TargetBackend = GraphicsBackend.Vulkan,
                    Language = ShaderLanguage.Hlsl,
                    VertexSource = vertexSource,
                    FragmentSource = fragmentSource,
                });
                if (!vulkan.Success && vulkan.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
                {
                    spirvUnavailable = true;
                    continue;
                }

                Assert.True(vulkan.Success, $"{shaderId}/{pass.Name}: {vulkan.ErrorMessage}");
                Assert.Equal(ShaderBytecodeFormat.SpirV, vulkan.Format);
            }
        }

        Assert.Equal(43, shaderCount);
        Assert.Equal(80, passCount);
    }

    [Fact]
    public void DxcBindingReflection_Expands_Register_Aliases()
    {
        const string fragmentSource = """
#define LIGHT_TEXTURE_REGISTER t20
#define LIGHT_SAMPLER_REGISTER s20
#ifndef LIGHT_TEXTURE_REGISTER
#define LIGHT_TEXTURE_REGISTER t4
#define LIGHT_SAMPLER_REGISTER s4
#endif
Texture2D LightTexture : register(LIGHT_TEXTURE_REGISTER);
SamplerState LightSampler : register(LIGHT_SAMPLER_REGISTER);
float4 main() : SV_Target { return LightTexture.Sample(LightSampler, 0.0.xx); }
""";

        ShaderBindingLayout layout = DxcShaderCompiler.ParseBindingLayout(string.Empty, fragmentSource);

        Assert.Collection(layout.Textures,
            slot => AssertBinding(slot, ShaderBindingKind.Texture, 20, "LightTexture"));
        Assert.Collection(layout.Samplers,
            slot => AssertBinding(slot, ShaderBindingKind.Sampler, 20, "LightSampler"));
    }

    [Fact]
    public void Terrain_Instanced_EightLayer_Variant_Compiles_For_Modern_Backends_When_Dxc_Is_Available()
    {
        var compiler = new DxcShaderCompiler();
        if (compiler.ResolvedDxcPath == null) return;
        ShaderPass pass = TerrainShaderLibrary.LoadTerrainShader().GetPass("Terrain");
        string vertexSource = (string)typeof(ShaderPass)
            .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
        string fragmentSource = (string)typeof(ShaderPass)
            .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
        var keywords = new System.Collections.Generic.Dictionary<string, bool>
        {
            ["GPU_INSTANCING"] = true,
            ["TERRAIN_8_LAYERS"] = true,
            ["TERRAIN_BICUBIC"] = true,
        };
        foreach (GraphicsBackend backend in new[] { GraphicsBackend.Direct3D12, GraphicsBackend.Vulkan })
        {
            ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
            {
                TargetBackend = backend,
                Language = ShaderLanguage.Hlsl,
                VertexSource = vertexSource,
                FragmentSource = fragmentSource,
                Keywords = keywords,
            });
            if (backend == GraphicsBackend.Vulkan && !result.Success &&
                result.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
                continue;
            Assert.True(result.Success, result.ErrorMessage);
        }
    }

    [Fact]
    public void Grass_Instanced_Variants_Compile_For_Modern_Backends_When_Dxc_Is_Available()
    {
        var compiler = new DxcShaderCompiler();
        if (compiler.ResolvedDxcPath == null) return;
        Shader shader = TerrainShaderLibrary.LoadGrassShader();
        var keywords = new System.Collections.Generic.Dictionary<string, bool> { ["GPU_INSTANCING"] = true };
        foreach (string passName in new[] { "Grass", "GrassPrepass" })
        {
            ShaderPass pass = shader.GetPass(passName);
            string vertexSource = (string)typeof(ShaderPass)
                .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
            string fragmentSource = (string)typeof(ShaderPass)
                .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
            foreach (GraphicsBackend backend in new[] { GraphicsBackend.Direct3D12, GraphicsBackend.Vulkan })
            {
                ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
                {
                    TargetBackend = backend,
                    Language = ShaderLanguage.Hlsl,
                    VertexSource = vertexSource,
                    FragmentSource = fragmentSource,
                    Keywords = keywords,
                });
                if (backend == GraphicsBackend.Vulkan && !result.Success &&
                    result.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
                    continue;
                Assert.True(result.Success, $"{passName}/{backend}: {result.ErrorMessage}");
            }
        }
    }

    [Fact]
    public void VolumetricFog_Passes_Compile_For_Modern_Backends_When_Dxc_Is_Available()
    {
        var compiler = new DxcShaderCompiler();
        if (compiler.ResolvedDxcPath == null) return;
        Shader shader = Shader.LoadDefault(DefaultShader.VolumetricFog);
        foreach (string passName in new[] { "FogMarch", "FogTemporal", "FogComposite" })
        {
            ShaderPass pass = shader.GetPass(passName);
            string vertexSource = (string)typeof(ShaderPass)
                .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
            string fragmentSource = (string)typeof(ShaderPass)
                .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pass)!;
            foreach (GraphicsBackend backend in new[] { GraphicsBackend.Direct3D12, GraphicsBackend.Vulkan })
            {
                ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
                {
                    TargetBackend = backend,
                    Language = ShaderLanguage.Hlsl,
                    VertexSource = vertexSource,
                    FragmentSource = fragmentSource,
                });
                if (backend == GraphicsBackend.Vulkan && !result.Success &&
                    result.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
                    continue;
                Assert.True(result.Success, $"{passName}/{backend}: {result.ErrorMessage}");
            }
        }
    }

    [Fact]
    public void CinematicEffects_Uniform_Branches_Compile_For_Modern_Backends_When_Dxc_Is_Available()
    {
        var compiler = new DxcShaderCompiler();
        if (compiler.ResolvedDxcPath == null)
            return;

        ShaderPass pass = Shader.LoadDefault(DefaultShader.CinematicEffects).GetPass("CinematicEffects");
        string vertexSource = (string)typeof(ShaderPass)
            .GetField("_hlslVertexSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
        string fragmentSource = (string)typeof(ShaderPass)
            .GetField("_hlslFragmentSource", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
        ShaderCompileResult d3d12 = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Direct3D12,
            Language = ShaderLanguage.Hlsl,
            VertexSource = vertexSource,
            FragmentSource = fragmentSource,
        });
        Assert.True(d3d12.Success, d3d12.ErrorMessage);

        ShaderCompileResult vulkan = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Vulkan,
            Language = ShaderLanguage.Hlsl,
            VertexSource = vertexSource,
            FragmentSource = fragmentSource,
        });
        if (!vulkan.Success && vulkan.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
            return;

        Assert.True(vulkan.Success, vulkan.ErrorMessage);
    }

    private static void AssertBinding(ShaderBindingSlot slot, ShaderBindingKind kind, int index, string name)
    {
        Assert.Equal(kind, slot.Kind);
        Assert.Equal(index, slot.Slot);
        Assert.Equal(name, slot.Name);
    }
}
