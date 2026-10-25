// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Runtime.InteropServices;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShaderConfigTests
{
    [Fact]
    public void ShaderConfig_MatchesCpuShadowAndBvhLayout()
    {
        Assert.Equal(ShaderConfig.MaxShadowCasters, SceneLightSystem.MaxShadowCasters);
        Assert.Equal(ShaderConfig.MaxShadowCasters, StandardShadowsUniformsData.MaxShadowCasters);
        Assert.Equal(ShaderConfig.LightBvhTexelsPerLight, LightBVH.TexelsPerLight);
        Assert.Equal(ShaderConfig.LightBvhTexelsPerNode, LightBVH.TexelsPerNode);
        Assert.Equal(4, ShaderConfig.MaxShadowCasters);
        Assert.Equal(4096, ShaderConfig.MaxBvhNodeVisits);
        Assert.Equal(2, ShaderConfig.MaxReflectionProbes);
        Assert.Equal(6, ShaderConfig.LightBvhTexelsPerLight);
        Assert.Equal(8, ShaderConfig.MaxRenderingLayers);
        Assert.Equal(16, ShaderConfig.MaxSoftShadowTaps);
    }

    [Fact]
    public void ShaderConfigIncludes_LockstepWithCSharp()
    {
        AssertDefines(EmbeddedResources.ReadAllText("Assets/Defaults/ShaderLibrary/ShaderConfig.glsl"));
        AssertDefines(EmbeddedResources.ReadAllText("Assets/Defaults/ShaderLibrary/ShaderConfig.hlsl"));
        Assert.True(EmbeddedResources.Exists("Assets/Defaults/ShaderLibrary/Core.glsl"));
        Assert.True(EmbeddedResources.Exists("Assets/Defaults/ShaderLibrary/Core.hlsl"));
        Assert.True(EmbeddedResources.Exists("Assets/Defaults/ShaderLibrary/LightCookie.glsl"));
        Assert.True(EmbeddedResources.Exists("Assets/Defaults/ShaderLibrary/LightCookie.hlsl"));
        Assert.True(EmbeddedResources.Exists("Assets/Defaults/ShaderLibrary/RenderingLayers.glsl"));
        Assert.True(EmbeddedResources.Exists("Assets/Defaults/ShaderLibrary/RenderingLayers.hlsl"));
    }

    [Fact]
    public void SkyboxShadersCenterDomeOnCamera()
    {
        string[] files =
        [
            "Assets/Defaults/XEngineCG.glsl",
            "Assets/Defaults/XEngineCG.hlsl",
            "Assets/Defaults/ProceduralSkybox.shader",
            "Assets/Defaults/GradientSkybox.shader",
            "Assets/Defaults/CubemapSkybox.shader",
        ];
        foreach (string file in files)
            Assert.Contains("TransformSkyboxClip", EmbeddedResources.ReadAllText(file), StringComparison.Ordinal);

        string glsl = EmbeddedResources.ReadAllText("Assets/Defaults/XEngineCG.glsl");
        string hlsl = EmbeddedResources.ReadAllText("Assets/Defaults/XEngineCG.hlsl");
        Assert.Contains("origin.w", glsl, StringComparison.Ordinal);
        Assert.Contains("origin.w", hlsl, StringComparison.Ordinal);
        Assert.Contains("4.0", glsl, StringComparison.Ordinal);
        Assert.Contains("4.0", hlsl, StringComparison.Ordinal);

        Shader procedural = Shader.LoadDefault(DefaultShader.ProceduralSkybox);
        Shader gradient = Shader.LoadDefault(DefaultShader.GradientSkybox);
        Shader cubemap = Shader.LoadDefault(DefaultShader.CubemapSkybox);
        Assert.True(procedural.GetPass(0).HasGlsl);
        Assert.True(procedural.GetPass(0).HasHlsl);
        Assert.True(gradient.GetPass(0).HasGlsl);
        Assert.True(gradient.GetPass(0).HasHlsl);
        Assert.True(cubemap.GetPass(0).HasGlsl);
        Assert.True(cubemap.GetPass(0).HasHlsl);
    }

    [Fact]
    public void SkinningShader_ReconstructsUploadedFloat4x4ColumnsWithoutTranspose()
    {
        // Deliberately non-symmetric, with translation in c3: identity matrices cannot expose the
        // row/column error that caused animated Battle2 vertices to form long triangle slivers.
        Float4x4 matrix = new(
            new Float4(0f, 2f, 3f, 0f),
            new Float4(-1f, 0f, 4f, 0f),
            new Float4(5f, 6f, 0f, 0f),
            new Float4(7f, 8f, 9f, 1f));
        Float4 point = new(0.25f, -0.5f, 2f, 1f);

        ReadOnlySpan<Float4> uploadedColumns = MemoryMarshal.CreateReadOnlySpan(ref matrix.c0, 4);
        Float4 shaderEquivalent = new(
            uploadedColumns[0].X * point.X + uploadedColumns[1].X * point.Y + uploadedColumns[2].X * point.Z + uploadedColumns[3].X * point.W,
            uploadedColumns[0].Y * point.X + uploadedColumns[1].Y * point.Y + uploadedColumns[2].Y * point.Z + uploadedColumns[3].Y * point.W,
            uploadedColumns[0].Z * point.X + uploadedColumns[1].Z * point.Y + uploadedColumns[2].Z * point.Z + uploadedColumns[3].Z * point.W,
            uploadedColumns[0].W * point.X + uploadedColumns[1].W * point.Y + uploadedColumns[2].W * point.Z + uploadedColumns[3].W * point.W);
        Float4 cpu = matrix * point;

        Assert.Equal(cpu.X, shaderEquivalent.X, 5);
        Assert.Equal(cpu.Y, shaderEquivalent.Y, 5);
        Assert.Equal(cpu.Z, shaderEquivalent.Z, 5);
        Assert.Equal(cpu.W, shaderEquivalent.W, 5);

        string hlsl = EmbeddedResources.ReadAllText("Assets/Defaults/XEngineCG.hlsl");
        Assert.Contains("float4(c0.x, c1.x, c2.x, c3.x)", hlsl, StringComparison.Ordinal);
        Assert.Contains("float4(c0.y, c1.y, c2.y, c3.y)", hlsl, StringComparison.Ordinal);
        Assert.Contains("float4(c0.z, c1.z, c2.z, c3.z)", hlsl, StringComparison.Ordinal);
        Assert.Contains("float4(c0.w, c1.w, c2.w, c3.w)", hlsl, StringComparison.Ordinal);
        Assert.DoesNotContain("return float4x4(c0, c1, c2, c3)", hlsl, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformSkyboxClip_OrthographicCoversNdc()
    {
        Float4x4 view = Float4x4.Identity;
        Float4x4 ortho = Float4x4.CreateOrtho(20f, 10f, 0.1f, 1000f);
        Float4 clip = UniversalRenderPipeline.TransformSkyboxClip(new Float3(1f, 0f, 0f), view, ortho);
        Assert.True(MathF.Abs(clip.W) > 1e-5f);
        Assert.True(MathF.Abs(clip.X / clip.W) >= 1f);

        Float4x4 perspective = Float4x4.CreatePerspectiveFov(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);
        Float4 perspOrigin = UniversalRenderPipeline.TransformSkyboxClip(new Float3(0f, 0f, 1f), view, perspective);
        Assert.True(MathF.Abs(perspOrigin.X / MathF.Max(1e-5f, perspOrigin.W)) < 0.01f);
    }

    [Fact]
    public void TransformSkyboxClip_OrthographicIsoCoversScreenBottom()
    {
        Float3 eye = new(10f, 10f, -10f);
        Float4x4 view = Float4x4.CreateLookAt(eye, Float3.Zero, Float3.UnitY);
        Float4x4 ortho = Float4x4.CreateOrtho(20f, 12f, -80f, 80f);
        Float4 bottom = UniversalRenderPipeline.TransformSkyboxClip(new Float3(0f, -1f, 0f), view, ortho);
        Assert.True(MathF.Abs(bottom.W) > 1e-5f);
        Assert.True(bottom.Y / bottom.W <= -1f);
    }

    [Fact]
    public void TransformSkyboxClip_OrthographicClipsBehindCameraHemisphere()
    {
        Float4x4 view = Float4x4.Identity;
        Float4x4 ortho = Float4x4.CreateOrtho(20f, 10f, 0.1f, 1000f);

        // Behind-camera hemisphere (+Z in view space) must land past the far plane: ortho
        // flattens the dome along view Z, and with ZTest Off both hemispheres otherwise cover
        // the same pixels with submission order deciding who wins (gray facets over the sky).
        Float4 behind = UniversalRenderPipeline.TransformSkyboxClip(new Float3(0f, 0f, 1f), view, ortho);
        Assert.True(behind.Z / MathF.Abs(behind.W) > 1f);

        // The visible hemisphere stays pinned to the far plane.
        Float4 ahead = UniversalRenderPipeline.TransformSkyboxClip(new Float3(0f, 0f, -1f), view, ortho);
        Assert.Equal(ahead.W, ahead.Z);

        // Perspective keeps the plain far-plane mapping for every vertex (w-clipping handles it).
        Float4x4 perspective = Float4x4.CreatePerspectiveFov(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);
        Float4 perspBehind = UniversalRenderPipeline.TransformSkyboxClip(new Float3(0f, 0f, 1f), view, perspective);
        Assert.Equal(perspBehind.W, perspBehind.Z);
    }

    [Fact]
    public void PaperUiShaderDecodesSrgbInputsForSwapchain()
    {
        string source = EmbeddedResources.ReadAllText("Assets/Defaults/UI.shader");
        Assert.Contains("uiColorToLinear", source, StringComparison.Ordinal);
        Assert.Contains("srgbToLinear", source, StringComparison.Ordinal);
        Assert.Contains("texture0SrgbDecode", source, StringComparison.Ordinal);
        Assert.Contains("srgbToLinear(texel.rgb)", source, StringComparison.Ordinal);

        Shader ui = Shader.LoadDefault(DefaultShader.UI);
        ShaderPass pass = ui.GetPass("UI");
        Assert.True(pass.HasGlsl);
        Assert.True(pass.HasHlsl);
    }

    [Fact]
    public void StandardUnlitTerrain_StillParse()
    {
        Shader standard = Shader.LoadDefault(DefaultShader.Standard);
        Shader unlit = Shader.LoadDefault(DefaultShader.Unlit);
        Shader terrain = TerrainShaderLibrary.LoadTerrainShader();

        Assert.True(standard.GetPass(0).HasGlsl);
        Assert.True(standard.GetPass(0).HasHlsl);
        Assert.True(unlit.GetPass(0).HasGlsl);
        Assert.True(terrain.GetPass("Terrain").HasGlsl);

        string glsl = PassSource(standard.GetPass(0), "_fragmentSource");
        string hlsl = PassSource(standard.GetPass(0), "_hlslFragmentSource");
        Assert.Contains($"#define MAX_SHADOW_CASTERS {ShaderConfig.MaxShadowCasters}", glsl);
        Assert.Contains($"#define XENGINE_STANDARD_MAX_SHADOW_CASTERS {ShaderConfig.MaxShadowCasters}", hlsl);
        Assert.Contains($"#define LBVH_MAX_NODE_VISITS {ShaderConfig.MaxBvhNodeVisits}", glsl);
    }

    [Fact]
    public void ShaderLibrary_DocumentsForwardIncludeStacks()
    {
        Assert.Equal("ShaderLibrary/ShaderConfig", ShaderLibrary.Config);
        Assert.Equal("ShaderLibrary/Core", ShaderLibrary.Core);
        Assert.Contains(ShaderLibrary.Config, ShaderLibrary.ForwardLitGlslIncludes);
        Assert.Contains(ShaderLibrary.Lighting, ShaderLibrary.ForwardLitGlslIncludes);
        Assert.Contains(ShaderLibrary.ImageBasedLighting, ShaderLibrary.ForwardLitGlslIncludes);
        Assert.Contains(ShaderLibrary.LightCookie, ShaderLibrary.ForwardLitGlslIncludes);
        Assert.Contains(ShaderLibrary.RenderingLayers, ShaderLibrary.ForwardLitGlslIncludes);
        Assert.Contains(ShaderLibrary.StandardLighting, ShaderLibrary.ForwardLitHlslIncludes);
        Assert.Contains(ShaderLibrary.ImageBasedLighting, ShaderLibrary.ForwardLitHlslIncludes);
        Assert.Contains(ShaderLibrary.LightCookie, ShaderLibrary.ForwardLitHlslIncludes);
        Assert.Contains(ShaderLibrary.RenderingLayers, ShaderLibrary.ForwardLitHlslIncludes);
    }

    private static void AssertDefines(string source)
    {
        Assert.Contains($"#define MAX_SHADOW_CASTERS {ShaderConfig.MaxShadowCasters}", source);
        Assert.Contains($"#define XENGINE_STANDARD_MAX_SHADOW_CASTERS {ShaderConfig.MaxShadowCasters}", source);
        Assert.Contains($"#define LBVH_MAX_NODE_VISITS {ShaderConfig.MaxBvhNodeVisits}", source);
        Assert.Contains($"#define LBVH_TEXELS_PER_LIGHT {ShaderConfig.LightBvhTexelsPerLight}", source);
        Assert.Contains($"#define LBVH_TEXELS_PER_NODE {ShaderConfig.LightBvhTexelsPerNode}", source);
        Assert.Contains($"#define MAX_REFLECTION_PROBES {ShaderConfig.MaxReflectionProbes}", source);
        Assert.Contains($"#define MAX_RENDERING_LAYERS {ShaderConfig.MaxRenderingLayers}", source);
        Assert.Contains($"#define MAX_SOFT_SHADOW_TAPS {ShaderConfig.MaxSoftShadowTaps}", source);
    }

    private static string PassSource(ShaderPass pass, string field)
        => (string)typeof(ShaderPass)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(pass)!;
}
