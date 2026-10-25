// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// N6 - Blend-shape support for Shader Graph materials: the emitted DSL source (SLANGPROGRAM
/// included) parses back into a Shader and compiles the BLENDSHAPES variant on every modern
/// backend, with the morph-skin order asserted at source level by ShaderGraphBlendShapeTests.
///
/// Known gap (pre-existing, documented in docs/superpowers/evidence/blendshape/README.md):
/// graph materials render black through the offline DrawRenderables probe even on main before
/// the N6 emission changes (position-only mesh, morph disabled), so pixel-coverage acceptance
/// for graph materials awaits that fix.
/// </summary>
public sealed class BlendShapeGraphRenderTests
{
    private static bool _webgpuCycleUsed;

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    [InlineData(GraphicsBackend.WebGPU)]
    public void GraphMaterial_CompilesBlendShapesVariant(GraphicsBackend backend)
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            if (backend == GraphicsBackend.WebGPU)
            {
                if (_webgpuCycleUsed) return; // one wgpu lifecycle per process
                _webgpuCycleUsed = true;
            }
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
            if (Graphics.Device == null || Graphics.Device.Backend != backend)
                return;

            string graphSource = ShaderGraphSourceCompiler.CompileToSource(
                "Test/BlendShapeGraph", ShaderGraphTargetKind.Unlit,
                new List<ShaderGraphProperty>(),
                new List<ShaderGraphNode>
                {
                    new() { Id = "master", Type = ShaderGraphNodeLibrary.MasterUnlit },
                },
                new List<ShaderGraphEdge>());
            string defaultsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "XEngine.Runtime", "Assets", "Defaults"));
            Assert.True(ShaderParser.ParseShader(Path.Combine(defaultsDir, "BlendShapeGraph.shader"),
                graphSource, null, out Shader? parsed));
            using Shader shader = parsed!;
            using var material = new Material(shader);

            // The morph keyword must reach the compiled variant on every backend.
            material.SetKeyword("BLENDSHAPES", true);
            XEngine.Runtime.Rendering.Shaders.ShaderPass pass = shader.GetPass("Unlit");
            Assert.True(pass.TryGetMaterialVariant(material, out RHI.Shaders.ShaderVariant? variant)
                && variant != null, $"graph BLENDSHAPES variant must compile for {backend}");
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            if (Graphics.Device != null)
                Graphics.Dispose();
            RenderTexture.DisposeTemporaryPool();
            GraphicsBackendSelection.Preferred = previous;
        }
    }
}
