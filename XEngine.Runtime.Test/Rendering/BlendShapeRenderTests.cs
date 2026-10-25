// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// GPU probe for the BlendShape (morph target) binding chain: a quad whose single blend shape
/// shifts every vertex left by 1.5 NDC must visibly deform through the full pipeline draw path —
/// mesh delta textures + per-renderer weight texture + <c>GetMorphedPosition</c> in the vertex
/// stage — on each backend. With weight 0 the quad fills the target (every pixel red, morphed NDC
/// x encoded in R); with weight 1 only the left quarter stays covered, so the center pixel goes
/// black and a left-quarter pixel carries the shifted value (0.125 = -0.75 NDC x * 0.5 + 0.5).
/// This pins sign, magnitude and vertex-ID addressing, not just "something moved".
/// Skips when the backend's device is unavailable on this host.
/// </summary>
public sealed class BlendShapeRenderTests
{
    private const int Width = 8;
    private const int Height = 4;

    // wgpu-native cannot survive a second full Graphics.Initialize/Dispose cycle inside one
    // process (teardown races leave dangling buffer ids — the next submit panics with
    // "Buffer does not exist"). The first WebGPU case in a process runs; later ones skip.
    // Screenshot evidence for every case therefore comes from per-method filtered runs
    // (one WebGPU lifecycle per process).
    private static bool _webgpuCycleUsed;

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    [InlineData(GraphicsBackend.WebGPU)]
    public void Morph_DeformsQuadThroughPipelineDrawPath(GraphicsBackend backend)
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            if (backend == GraphicsBackend.WebGPU)
            {
                if (_webgpuCycleUsed) return; // one wgpu lifecycle per process — see note above
                _webgpuCycleUsed = true;
            }
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
            // Identity view/projection: the quad is authored directly in clip space.
            GlobalUniforms.SetMatrixVP(Float4x4.Identity);
            GlobalUniforms.Upload();
            if (Graphics.Device == null || Graphics.Device.Backend != backend)
                return; // device unavailable on this host — skip

            const string source = """
                Shader "Test/BlendShapeProbe"
                Pass "Main"
                {
                    Tags { "RenderOrder" = "Opaque" }
                    Cull Off
                    ZWrite Off
                    GLSLPROGRAM
                    Vertex
                    {
                        #include "XEngineCG"
                        #include "VertexAttributes"

                        out vec3 morphedPos;

                        void main()
                        {
                            vec3 p = GetMorphedPosition(vertexPosition);
                            gl_Position = vec4(p, 1.0);
                            morphedPos = p;
                        }
                    }
                    Fragment
                    {
                        in vec3 morphedPos;
                        layout (location = 0) out vec4 fragColor;

                        void main()
                        {
                            fragColor = vec4(morphedPos.x * 0.5 + 0.5, 0.0, 0.0, 1.0);
                        }
                    }
                    ENDGLSL
                    SLANGPROGRAM
                    Vertex
                    {
                        #include "XEngineCG"

                        struct VSInput
                        {
                            float3 vertexPosition : POSITION;
                            uint vertexId : SV_VertexID;
                        };

                        struct VSOutput
                        {
                            float4 position : SV_Position;
                            float3 morphedPos : TEXCOORD0;
                        };

                        VSOutput main(VSInput input)
                        {
                            VSOutput o;
                            float3 p = GetMorphedPosition(input.vertexPosition, input.vertexId);
                            o.position = float4(p, 1.0);
                            o.morphedPos = p;
                            return o;
                        }
                    }
                    Fragment
                    {
                        struct PSInput
                        {
                            float4 position : SV_Position;
                            float3 morphedPos : TEXCOORD0;
                        };

                        float4 main(PSInput input) : SV_Target
                        {
                            return float4(input.morphedPos.x * 0.5 + 0.5, 0.0, 0.0, 1.0);
                        }
                    }
                    ENDSLANG
                }
                """;
            // The fake source path only steers #include resolution: placing it in the Defaults
            // directory lets `#include "XEngineCG"` find XEngineCG.hlsl / VertexAttributes.glsl.
            string defaultsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "XEngine.Runtime", "Assets", "Defaults"));
            // A null include resolver routes `#include "XEngineCG"` to the filesystem, resolved
            // against the fake source path's Defaults directory (XEngineCG.hlsl lives there).
            Assert.True(ShaderParser.ParseShader(Path.Combine(defaultsDir, "BlendShapeProbe.shader"), source, null, out Shader? parsed));
            using Shader shader = parsed!;
            using var material = new Material(shader);

            // Quad covering clip space; the blend shape shifts all four vertices left by 1.5 NDC.
            using var mesh = new Mesh
            {
                Vertices = [new Float3(-1, -1, 0), new Float3(1, -1, 0), new Float3(-1, 1, 0), new Float3(1, 1, 0)],
                Normals = [new Float3(0, 0, 1), new Float3(0, 0, 1), new Float3(0, 0, 1), new Float3(0, 0, 1)],
                UV = [new Float2(0, 0), new Float2(1, 0), new Float2(0, 1), new Float2(1, 1)],
                Indices = [0u, 1u, 2u, 2u, 1u, 3u],
                BlendShapes =
                [
                    new BlendShape
                    {
                        Name = "ShiftLeft",
                        Frames = [new BlendShapeFrame
                        {
                            Weight = 100f,
                            DeltaVertices = [new Float3(-1.5f, 0, 0), new Float3(-1.5f, 0, 0), new Float3(-1.5f, 0, 0), new Float3(-1.5f, 0, 0)],
                        }],
                    },
                ],
            };
            mesh.RecalculateBounds();
            mesh.EnsureMorphTextures();
            Assert.NotNull(mesh.MorphPositionTexture);

            // One active layer: texel (layerIndex=0, weight=1).
            using var weightTex = new Texture2D(1, 1, false, TextureImageFormat.Float4);
            UploadSync(weightTex, new Float4[] { new(0f, 1f, 0f, 0f) }, 1);

            using var targetOn = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            using var targetOff = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            using var pipeline = new ProbePipeline();

            RenderTexture[] targets = [targetOn, targetOff];
            int[] activeCounts = [1, 0];
            for (int i = 0; i < targets.Length; i++)
            {
                var props = new PropertyState();
                props.SetTexture("morphPositionTexture", mesh.MorphPositionTexture!);
                props.SetTexture("morphNormalTexture", mesh.MorphPositionTexture!);
                props.SetTexture("morphTangentTexture", mesh.MorphPositionTexture!);
                props.SetTexture("morphWeightTexture", weightTex);
                props.SetInt("morphActiveCount", activeCounts[i]);
                props.SetInt("morphTexWidth", 4);
                props.SetInt("morphVertexCount", 4);
                props.SetInt("morphHasNormals", 0);
                props.SetInt("morphHasTangents", 0);

                var renderable = new MorphRenderable(material, mesh, props);
                Graphics.BeginFrame();
                using (CommandBuffer cmd = Graphics.GetCommandBuffer("blendshape-probe"))
                {
                    cmd.SetRenderTarget(targets[i].frameBuffer);
                    cmd.SetViewport(0, 0, Width, Height);
                    cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0f, 0f, 0f, 1f), 1f);
                    pipeline.DrawRenderables(cmd, [renderable], "RenderOrder", "Opaque", default, [false], false, targets[i]);
                    Graphics.Submit(cmd);
                }
                Graphics.EndFrameAndWait();
            }

            Graphics.Device.WaitIdle();
            byte[] on = ReadPixels(targetOn);
            byte[] off = ReadPixels(targetOff);

            SaveScreenshotPng($"blendshape-acceptance-{backend}-position", on);
            SaveScreenshotPng($"blendshape-acceptance-{backend}-position-off", off);

            // Weight 0: identity quad covers everything; center pixel's NDC x is 0.125.
            AssertCenterR(off, "weight0", 0.5625f, 2f / 255f);

            // Weight 1: quad shifted to NDC x in [-2.5, -0.5]; the center (NDC 0) is uncovered.
            int center = (Height / 2 * Width + Width / 2) * 4;
            Assert.True(on[center] < 8 && on[center + 1] < 8 && on[center + 2] < 8,
                $"[ {backend}] weight1 center should be uncovered (black), got RGBA {on[center]},{on[center + 1]},{on[center + 2]}");

            // Left-quarter pixel (pixel 1 center = NDC x -0.625) is still covered and encodes the
            // shifted value: -0.625 * 0.5 + 0.5 = 0.1875.
            int leftPixel = (Height / 2 * Width + Width / 8) * 4;
            float expectedLeftR = -0.625f * 0.5f + 0.5f;
            float actualLeftR = on[leftPixel] / 255f;
            Assert.True(MathF.Abs(actualLeftR - expectedLeftR) < 4f / 255f,
                $"[{backend}] weight1 left-quarter R {actualLeftR:0.###} != expected {expectedLeftR:0.###} (RGBA {on[leftPixel]},{on[leftPixel + 1]},{on[leftPixel + 2]})");
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

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    [InlineData(GraphicsBackend.WebGPU)]
    public void Morph_SkinnedOrder_IsMorphThenSkin(GraphicsBackend backend)
    {
        // One bone scaling X by 1.5, morph delta +0.4 on a quad with base x in [-2, 0]:
        // morph-then-skinning covers NDC x up to 0.6; the reverse order only reaches 0.4 — the
        // transformations do not commute, so coverage of pixel 6 (center NDC 0.625) pins the
        // order. Skin-only (no morph) covers up to 0, leaving the right half empty.
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            if (backend == GraphicsBackend.WebGPU)
            {
                if (_webgpuCycleUsed) return; // one wgpu lifecycle per process — see note above
                _webgpuCycleUsed = true;
            }
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
            GlobalUniforms.SetMatrixVP(Float4x4.Identity);
            GlobalUniforms.Upload();
            if (Graphics.Device == null || Graphics.Device.Backend != backend)
                return;

            const string source = """
                Shader "Test/BlendShapeSkinProbe"
                Pass "Main"
                {
                    Tags { "RenderOrder" = "Opaque" }
                    Cull Off
                    ZWrite Off
                    GLSLPROGRAM
                    Vertex
                    {
                        #include "XEngineCG"
                        #include "VertexAttributes"

                        out vec3 finalPos;

                        void main()
                        {
                            vec3 p = GetMorphedPosition(vertexPosition);
                        #ifdef SKINNED
                            p = GetSkinnedPosition(p).xyz;
                        #endif
                            gl_Position = vec4(p, 1.0);
                            finalPos = p;
                        }
                    }
                    Fragment
                    {
                        in vec3 finalPos;
                        layout (location = 0) out vec4 fragColor;

                        void main()
                        {
                            fragColor = vec4(finalPos.x * 0.5 + 0.5, 0.0, 0.0, 1.0);
                        }
                    }
                    ENDGLSL
                    SLANGPROGRAM
                    Vertex
                    {
                        #include "XEngineCG"

                        struct VSInput
                        {
                            float3 vertexPosition : POSITION;
                            uint vertexId : SV_VertexID;
                        #ifdef HAS_BONEINDICES
                            float4 vertexBoneIndices : BLENDINDICES;
                        #endif
                        #ifdef HAS_BONEWEIGHTS
                            float4 vertexBoneWeights : BLENDWEIGHT;
                        #endif
                        };

                        struct VSOutput
                        {
                            float4 position : SV_Position;
                            float3 finalPos : TEXCOORD0;
                            float nDebug : TEXCOORD1;
                        };

                        VSOutput main(VSInput input)
                        {
                            VSOutput o;
                            float3 p = GetMorphedPosition(input.vertexPosition, input.vertexId);
                        #if defined(SKINNED) && defined(HAS_BONEINDICES) && defined(HAS_BONEWEIGHTS)
                            p = XSkinPosition(p, input.vertexBoneIndices, input.vertexBoneWeights).xyz;
                        #endif
                            o.position = float4(p, 1.0);
                            o.finalPos = p;
                            o.nDebug = 0.0;
                            return o;
                        }
                    }
                    Fragment
                    {
                        struct PSInput
                        {
                            float4 position : SV_Position;
                            float3 finalPos : TEXCOORD0;
                            float nDebug : TEXCOORD1;
                        };

                        float4 main(PSInput input) : SV_Target
                        {
                            return float4(input.finalPos.x * 0.5 + 0.5, input.nDebug, 0.0, 1.0);
                        }
                    }
                    ENDSLANG
                }
                """;
            Assert.True(ShaderParser.ParseShader(
                Path.Combine(DefaultShaderDirectory, "BlendShapeSkinProbe.shader"), source, null, out Shader? parsed));
            using Shader shader = parsed!;
            using var material = new Material(shader);

            Float3[] vertices = [new Float3(0, -1, 0), new Float3(0, 1, 0), new Float3(-2, -1, 0), new Float3(-2, 1, 0)];
            using var mesh = new Mesh
            {
                Vertices = vertices,
                Indices = [0u, 1u, 2u, 2u, 1u, 3u],
                BlendShapes =
                [
                    new BlendShape
                    {
                        Name = "Push",
                        Frames = [new BlendShapeFrame
                        {
                            Weight = 100f,
                            DeltaVertices = [new Float3(0.4f, 0, 0), new Float3(0.4f, 0, 0), new Float3(0.4f, 0, 0), new Float3(0.4f, 0, 0)],
                        }],
                    },
                ],
                // One 1-based bone index in every vertex's first slot.
                BoneIndices = [new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0)],
                BoneWeights = [new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0)],
            };
            mesh.RecalculateBounds();
            mesh.EnsureMorphTextures();
            Assert.NotNull(mesh.MorphPositionTexture);

            // One bone: columns 0..3 of the scaled matrix in 4 RGBA32F texels.
            using var boneTex = new Texture2D(4, 1, false, TextureImageFormat.Float4);
            // See UploadSync: synchronous upload so the same-frame vertex fetch sees the matrix.
            UploadSync(boneTex, new[]
            {
                new Float4(2f, 0f, 0f, 0f),
                new Float4(0f, 1f, 0f, 0f),
                new Float4(0f, 0f, 1f, 0f),
                new Float4(0f, 0f, 0f, 1f),
            }, 4);

            using var weightTex = new Texture2D(1, 1, false, TextureImageFormat.Float4);
            UploadSync(weightTex, new Float4[] { new(0f, 1f, 0f, 0f) }, 1);

            using var targetOn = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            using var targetOff = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            using var pipeline = new ProbePipeline();

            RenderTexture[] targets = [targetOn, targetOff];
            byte[][] reads = new byte[2][];
            for (int i = 0; i < targets.Length; i++)
            {
                var props = new PropertyState();
                if (i == 0)
                {
                    props.SetTexture("morphPositionTexture", mesh.MorphPositionTexture!);
                    props.SetTexture("morphNormalTexture", mesh.MorphPositionTexture!);
                    props.SetTexture("morphTangentTexture", mesh.MorphPositionTexture!);
                    props.SetTexture("morphWeightTexture", weightTex);
                    props.SetInt("morphActiveCount", 1);
                    props.SetInt("morphTexWidth", 4);
                    props.SetInt("morphVertexCount", 4);
                    props.SetInt("morphHasNormals", 0);
                    props.SetInt("morphHasTangents", 0);
                }
                else
                {
                    props.SetInt("morphActiveCount", 0);
                }
                props.SetTexture("boneMatrixTexture", boneTex);
                props.SetInt("boneCount", 1);

                var renderable = new MorphRenderable(material, mesh, props);
                Graphics.BeginFrame();
                using (CommandBuffer cmd = Graphics.GetCommandBuffer("blendshape-skin-acceptance"))
                {
                    cmd.SetRenderTarget(targets[i].frameBuffer);
                    cmd.SetViewport(0, 0, Width, Height);
                    cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0f, 0f, 0f, 1f), 1f);
                    pipeline.DrawRenderables(cmd, [renderable], "RenderOrder", "Opaque", default, [false], false, targets[i]);
                    Graphics.Submit(cmd);
                }
                Graphics.EndFrameAndWait();
            }

            Graphics.Device.WaitIdle();
            reads[0] = ReadPixels(targetOn);
            reads[1] = ReadPixels(targetOff);

            SaveScreenshotPng($"blendshape-acceptance-{backend}-skinned-order", reads[0]);
            SaveScreenshotPng($"blendshape-acceptance-{backend}-skinned-order-off", reads[1]);

            // Morph(+0.4)→skin(×2) covers NDC x in [-4, 0.8]: pixel 6 (center 0.625) covered and
            // pixel 7 (center 0.875) empty pins the edge into (0.625, 0.875] — containing 0.8
            // (morph-then-skin) and excluding 0.4 (skin-then-morph / identity skin), which the
            // transformations' non-commutativity makes a genuine order check. Coverage is sampled
            // at pixel centers, so both edges land on distinguishable pixel centers.
            AssertCovered(reads[0], 6);
            AssertUncovered(reads[0], 7, "beyond the morph+skin edge 0.8");

            // Skin-only covers [-4, 0]: the right half stays empty, left quarter filled.
            AssertUncovered(reads[1], Width / 2, "skin-only right half");
            AssertCovered(reads[1], 1);
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

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    [InlineData(GraphicsBackend.WebGPU)]
    public void Morph_ProgressiveTwoFrames_InterpolatesLayers(GraphicsBackend backend)
    {
        // Two-frame progressive shape (w25 +0.25, w75 +0.75) sampled at weight 50: the resolver
        // emits layers (0, 0.5) and (1, 0.5) → total +0.5, quad covers NDC x in [-2, 0.5].
        // Pixel 5 (center 0.375) covered; pixel 7 (0.875) empty. A single-layer mis-resolution
        // (edge 0.25) would leave pixel 5 empty.
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            if (backend == GraphicsBackend.WebGPU)
            {
                if (_webgpuCycleUsed) return; // one wgpu lifecycle per process — see note above
                _webgpuCycleUsed = true;
            }
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
            GlobalUniforms.SetMatrixVP(Float4x4.Identity);
            GlobalUniforms.Upload();
            if (Graphics.Device == null || Graphics.Device.Backend != backend)
                return;

            const string source = """
                Shader "Test/BlendShapeProgressiveProbe"
                Pass "Main"
                {
                    Tags { "RenderOrder" = "Opaque" }
                    Cull Off
                    ZWrite Off
                    GLSLPROGRAM
                    Vertex
                    {
                        #include "XEngineCG"
                        #include "VertexAttributes"

                        out vec3 finalPos;

                        void main()
                        {
                            vec3 p = GetMorphedPosition(vertexPosition);
                            gl_Position = vec4(p, 1.0);
                            finalPos = p;
                        }
                    }
                    Fragment
                    {
                        in vec3 finalPos;
                        layout (location = 0) out vec4 fragColor;

                        void main()
                        {
                            fragColor = vec4(finalPos.x * 0.5 + 0.5, 0.0, 0.0, 1.0);
                        }
                    }
                    ENDGLSL
                    SLANGPROGRAM
                    Vertex
                    {
                        #include "XEngineCG"

                        struct VSInput
                        {
                            float3 vertexPosition : POSITION;
                            uint vertexId : SV_VertexID;
                        };

                        struct VSOutput
                        {
                            float4 position : SV_Position;
                            float3 finalPos : TEXCOORD0;
                        };

                        VSOutput main(VSInput input)
                        {
                            VSOutput o;
                            float3 p = GetMorphedPosition(input.vertexPosition, input.vertexId);
                            o.position = float4(p, 1.0);
                            o.finalPos = p;
                            return o;
                        }
                    }
                    Fragment
                    {
                        struct PSInput
                        {
                            float4 position : SV_Position;
                            float3 finalPos : TEXCOORD0;
                        };

                        float4 main(PSInput input) : SV_Target
                        {
                            return float4(input.finalPos.x * 0.5 + 0.5, 0.0, 0.0, 1.0);
                        }
                    }
                    ENDSLANG
                }
                """;
            Assert.True(ShaderParser.ParseShader(
                Path.Combine(DefaultShaderDirectory, "BlendShapeProgressiveProbe.shader"), source, null, out Shader? parsed));
            using Shader shader = parsed!;
            using var material = new Material(shader);

            using var mesh = new Mesh
            {
                Vertices = [new Float3(0, -1, 0), new Float3(0, 1, 0), new Float3(-2, -1, 0), new Float3(-2, 1, 0)],
                Indices = [0u, 1u, 2u, 2u, 1u, 3u],
                BlendShapes =
                [
                    new BlendShape
                    {
                        Name = "Progressive",
                        Frames =
                        [
                            new BlendShapeFrame { Weight = 25f, DeltaVertices = [new Float3(0.25f, 0, 0), new Float3(0.25f, 0, 0), new Float3(0.25f, 0, 0), new Float3(0.25f, 0, 0)] },
                            new BlendShapeFrame { Weight = 75f, DeltaVertices = [new Float3(0.75f, 0, 0), new Float3(0.75f, 0, 0), new Float3(0.75f, 0, 0), new Float3(0.75f, 0, 0)] },
                        ],
                    },
                ],
            };
            mesh.RecalculateBounds();
            mesh.EnsureMorphTextures();
            Assert.NotNull(mesh.MorphPositionTexture);

            // Weight 50 between frames 25/75: two active layers, 0.5 each.
            using var weightTex = new Texture2D(2, 1, false, TextureImageFormat.Float4);
            UploadSync(weightTex, new Float4[] { new(0f, 0.5f, 0f, 0f), new(1f, 0.5f, 0f, 0f) }, 2);

            using var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            using var pipeline = new ProbePipeline();

            var props = new PropertyState();
            props.SetTexture("morphPositionTexture", mesh.MorphPositionTexture!);
            props.SetTexture("morphNormalTexture", mesh.MorphPositionTexture!);
            props.SetTexture("morphTangentTexture", mesh.MorphPositionTexture!);
            props.SetTexture("morphWeightTexture", weightTex);
            props.SetInt("morphActiveCount", 2);
            props.SetInt("morphTexWidth", 4);
            props.SetInt("morphVertexCount", 4);
            props.SetInt("morphHasNormals", 0);
            props.SetInt("morphHasTangents", 0);

            var renderable = new MorphRenderable(material, mesh, props);
            Graphics.BeginFrame();
            using (CommandBuffer cmd = Graphics.GetCommandBuffer("blendshape-progressive-acceptance"))
            {
                cmd.SetRenderTarget(target.frameBuffer);
                cmd.SetViewport(0, 0, Width, Height);
                cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0f, 0f, 0f, 1f), 1f);
                pipeline.DrawRenderables(cmd, [renderable], "RenderOrder", "Opaque", default, [false], false, target);
                Graphics.Submit(cmd);
            }
            Graphics.EndFrameAndWait();
            Graphics.Device.WaitIdle();
            byte[] pixels = ReadPixels(target);

            SaveScreenshotPng($"blendshape-acceptance-{backend}-progressive", pixels);

            AssertCovered(pixels, 5);
            AssertUncovered(pixels, 7, "beyond the interpolated edge 0.5");
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

    private static string DefaultShaderDirectory => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "XEngine.Runtime", "Assets", "Defaults"));


    private static void AssertCovered(byte[] pixels, int pixelIndex)
    {
        int i = (Height / 2 * Width + pixelIndex) * 4;
        Assert.True(pixels[i] > 8,
            $"pixel {pixelIndex} should be covered, got RGBA {pixels[i]},{pixels[i + 1]},{pixels[i + 2]}");
    }

    private static void AssertUncovered(byte[] pixels, int pixelIndex, string label)
    {
        int i = (Height / 2 * Width + pixelIndex) * 4;
        Assert.True(pixels[i] < 8 && pixels[i + 1] < 8 && pixels[i + 2] < 8,
            $"pixel {pixelIndex} should be uncovered ({label}), got RGBA {pixels[i]},{pixels[i + 1]},{pixels[i + 2]}");
    }

    private static void SaveScreenshotPng(string name, byte[] rgba)
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "screenshots"));
        Directory.CreateDirectory(dir);
        TestPngWriter.WritePng(Path.Combine(dir, name + ".png"), rgba, Width, Height);
    }

    /// <summary>
    /// Uploads texels through the synchronous TexSubImage2D path (SubmitAndWait) — the ordering
    /// guarantee SkinnedMeshRenderer.UploadBoneTexture relies on. The async SetData path raced
    /// same-frame vertex fetches here (the 20260902 "half-uploaded bone texture" bug class):
    /// the skinned probe sampled a stale/zero bone matrix and skinned with an identity fallback.
    /// </summary>
    private static unsafe void UploadSync(Texture2D texture, Float4[] texels, int width)
    {
        fixed (Float4* ptr = texels)
            texture.SetDataPtr(ptr, 0, 0, (uint)width, 1);
    }

    private static byte[] ReadPixels(RenderTexture target)
    {
        byte[] pixels = new byte[Width * Height * 4];
        target.MainTexture.GetData<byte>(pixels.AsMemory());
        return pixels;
    }

    private static void AssertCenterR(byte[] pixels, string label, float expectedR, float tolerance)
    {
        int center = (Height / 2 * Width + Width / 2) * 4;
        float actualR = pixels[center] / 255f;
        Assert.True(MathF.Abs(actualR - expectedR) <= tolerance,
            $"{label} center R {actualR:0.###} != expected {expectedR:0.###} (RGBA {pixels[center]},{pixels[center + 1]},{pixels[center + 2]})");
    }

    private sealed class ProbePipeline : RenderPipeline { }

    /// <summary>Mimics <see cref="SkinnedMeshRenderable"/>'s contract: mesh + per-renderer morph props.</summary>
    private sealed class MorphRenderable(Material material, Mesh mesh, PropertyState properties) : IRenderable
    {
        public Material GetMaterial() => material;
        public int GetLayer() => 0;
        public Float3 GetPosition() => Float3.Zero;
        public GraphicsBuffer? GetIndirectDrawArgs() => null;
        public void GetCullingData(out bool isRenderable, out AABB bounds)
        {
            isRenderable = true;
            bounds = default;
        }
        public void GetRenderingData(ViewerData viewer, out PropertyState outProperties, out Mesh renderMesh,
            out Float4x4 model, out InstanceData[]? instanceData)
        {
            outProperties = properties;
            renderMesh = mesh;
            model = Float4x4.Identity;
            instanceData = null;
        }
    }
}
