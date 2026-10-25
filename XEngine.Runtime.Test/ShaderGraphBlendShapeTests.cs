// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// ★ N6 — Verifies the Shader Graph → DSL emitter wires blend-shape + skin deformation into the
/// Slang vertex stage (the GLSL stage inherits it via the VertexAttributes include): keyword-gated
/// BLENDINDICES/BLENDWEIGHT inputs, SV_VertexID, the morph→skin order on position, and the morphed
/// normal. Also verifies the GLSL stage morphs its normal and both stages keep the graph Position
/// port expression wired through the deformation.
/// </summary>
public sealed class ShaderGraphBlendShapeTests
{
    private static string Compile(ShaderGraphTargetKind target)
    {
        // Minimal graph: master node only — the deformation emission must be present even when
        // the Position port is unconnected (deformation applies to the raw attribute).
        var nodes = new List<ShaderGraphNode>
        {
            new() { Id = "master", Type = target == ShaderGraphTargetKind.Lit ? ShaderGraphNodeLibrary.MasterLit : ShaderGraphNodeLibrary.MasterUnlit },
        };
        return ShaderGraphSourceCompiler.CompileToSource("Test/GraphDeform", target, new List<ShaderGraphProperty>(), nodes, new List<ShaderGraphEdge>());
    }

    [Fact]
    public void UnlitGraph_SlangVertex_CarriesMorphAndSkin()
    {
        string source = Compile(ShaderGraphTargetKind.Unlit);

        Assert.Contains("SV_VertexID", source);
        Assert.Contains("BLENDINDICES", source);
        Assert.Contains("BLENDWEIGHT", source);
        Assert.Contains("GetMorphedPosition(vertPos, input.vertexId);", source);
        // Morph → skin order: skinning consumes the morphed position.
        Assert.Matches(@"GetMorphedPosition\(vertPos, input\.vertexId\);[\s\S]*?XSkinPosition\(vertPos", source);
        Assert.Contains("GetMorphedNormal(input.vertexNormal, input.vertexId)", source);
        Assert.Contains("XSkinNormal(nDeformed", source);
    }

    [Fact]
    public void LitGraph_SlangVertex_CarriesMorphAndSkin()
    {
        string source = Compile(ShaderGraphTargetKind.Lit);

        Assert.Contains("SV_VertexID", source);
        Assert.Contains("GetMorphedPosition(vertPos, input.vertexId);", source);
        Assert.Contains("XSkinPosition(vertPos", source);
        Assert.Contains("GetMorphedNormal(input.vertexNormal, input.vertexId)", source);
    }

    [Fact]
    public void Graphs_GlslVertex_MorphsNormal()
    {
        string source = Compile(ShaderGraphTargetKind.Unlit);

        // The GLSL stage morphs before transforming the normal (skinning already rides
        // TransformDirection via the VertexAttributes include).
        Assert.Contains("TransformDirection(GetMorphedNormal(vertexNormal));", source);
        // Position still flows through TransformClip, which morphs + skins internally.
        Assert.Contains("gl_Position = TransformClip(vertPos);", source);
    }

    [Fact]
    public void Graphs_SkinBlock_IsKeywordGated()
    {
        string source = Compile(ShaderGraphTargetKind.Unlit);

        // Deformation compiles away entirely for meshes without skin streams.
        Assert.Contains("#if defined(SKINNED) && defined(HAS_BONEINDICES) && defined(HAS_BONEWEIGHTS)", source);
    }
}
