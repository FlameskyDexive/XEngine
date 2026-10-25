using System;
using System.Collections.Generic;

using Xunit;

using XEngine.Runtime;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;

namespace XEngine.GpuAnimation.Tests;

/// <summary>
/// Force-compiles every plausible keyword combination of both crowd shaders (all passes) on the
/// live device. The pipeline sets mesh-attribute keywords (HAS_UV/HAS_UV2/HAS_NORMALS) and
/// GPU_INSTANCING per draw — a combo that fails Slang/GLSL compilation silently falls back to
/// the Invalid shader in production. This sweep fails loudly instead.
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationShaderVariantSmokeTests : IDisposable
{
    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationShaderVariantSmokeTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
        }
        catch (Exception)
        {
            GraphicsBackendSelection.Preferred = _previousBackend;
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void AllKeywordCombinations_CompileWithoutFallback()
    {
        if (Graphics.Device == null)
            return; // no Vulkan on this host — skip silently

        var errors = new List<string>();
        void Capture(string message, DebugStackTrace? trace, LogSeverity severity)
        {
            if (severity is LogSeverity.Error or LogSeverity.Exception)
                errors.Add(message);
        }
        Debug.OnLog += Capture;
        try
        {
            string[] shaders = { "unlit", "lit" };
            string[] keywords = { "GPU_INSTANCING", "HAS_UV", "HAS_UV2", "HAS_NORMALS" };
            int combos = 1 << keywords.Length;
            int compiled = 0;

            foreach (string shaderName in shaders)
            {
                Shader shader = shaderName == "unlit"
                    ? GpuAnimationShaderLibrary.LoadUnlitShader()
                    : GpuAnimationShaderLibrary.LoadLitShader();
                for (int mask = 0; mask < combos; mask++)
                {
                    var material = new Material(shader);
                    for (int k = 0; k < keywords.Length; k++)
                        material.SetKeyword(keywords[k], (mask & (1 << k)) != 0);

                    for (int pass = 0; pass < shader.PassArray.Length; pass++)
                    {
                        Assert.True(shader.PassArray[pass].TryGetMaterialVariant(material, out XEngine.Runtime.RHI.Shaders.ShaderVariant? variant),
                            $"{shaderName} pass {pass} keyword-mask {mask} failed to resolve a variant");
                        Assert.NotNull(variant);
                        compiled++;
                    }
                    material.Dispose();
                }
            }

            Assert.True(errors.Count == 0,
                $"{errors.Count} shader variant compile failures. First (full):\n{errors.FirstOrDefault() ?? ""}");
            Assert.True(compiled >= 64, $"expected >= 64 variant compiles, got {compiled}");
            _ = 0;
        }
        finally
        {
            Debug.OnLog -= Capture;
        }
    }
}
