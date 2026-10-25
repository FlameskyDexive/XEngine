// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShaderGraphCompilerTests
{
    [Fact]
    public void EchoRoundTrip_PreservesNodesEdgesAndProperties()
    {
        ShaderGraphAsset original = ShaderGraphAsset.CreateUnlitAlbedoGraph();
        original.Nodes[0].PosX = 12.5f;
        original.Nodes[0].PosY = -4f;

        var echo = Serializer.Serialize(typeof(object), original);
        var deserialized = Serializer.Deserialize<ShaderGraphAsset>(echo);

        Assert.NotNull(deserialized);
        Assert.Equal("Graph/UnlitAlbedo", deserialized!.ShaderName);
        Assert.Equal(ShaderGraphTargetKind.Unlit, deserialized.Target);
        Assert.Equal(2, deserialized.Properties.Count);
        Assert.Equal("_MainTex", deserialized.Properties[0].Name);
        Assert.Equal(4, deserialized.Nodes.Count);
        Assert.Equal(3, deserialized.Edges.Count);
        Assert.Equal(12.5f, deserialized.Nodes[0].PosX);
        Assert.Equal("n_master", deserialized.Nodes[3].Id);
    }

    [Fact]
    public void Compile_UnlitAlbedo_IsDeterministicAndParses()
    {
        ShaderGraphAsset graph = ShaderGraphAsset.CreateUnlitAlbedoGraph();
        string a = ShaderGraphCompiler.CompileToSource(graph);
        string b = ShaderGraphCompiler.CompileToSource(graph);
        Assert.Equal(a, b);

        Assert.Contains("GLSLPROGRAM", a, StringComparison.Ordinal);
        Assert.Contains("SLANGPROGRAM", a, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.Core + "\"", a, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.Lighting + "\"", a, StringComparison.Ordinal);
        Assert.Contains("ApplyFog", a, StringComparison.Ordinal);
        Assert.Contains("TransformClip", a, StringComparison.Ordinal);
        Assert.Contains("_MainTex", a, StringComparison.Ordinal);
        Assert.Contains("_MainColor", a, StringComparison.Ordinal);
        Assert.Contains("texture(_MainTex", a, StringComparison.Ordinal);
        Assert.Contains("_MainTex.Sample(_MainTexSampler", a, StringComparison.Ordinal);

        Assert.True(ShaderParser.ParseShader("Graph/UnlitAlbedo.shader", a, out Shader? shader));
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(0).HasGlsl);
        Assert.True(shader.GetPass(0).HasSlang);
    }

    [Fact]
    public void Compile_LitConstant_UsesLightBvhIncludes()
    {
        ShaderGraphAsset graph = ShaderGraphAsset.CreateLitConstantGraph();
        string source = ShaderGraphCompiler.CompileToSource(graph);

        Assert.Contains("CalculateForwardLighting", source, StringComparison.Ordinal);
        Assert.Contains("StandardCalculateLighting", source, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.Core + "\"", source, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.Lighting + "\"", source, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.StandardLighting + "\"", source, StringComparison.Ordinal);
        Assert.Contains("vec4(0.8, 0.8, 0.8, 1.0)", source, StringComparison.Ordinal);

        Assert.True(ShaderParser.ParseShader("Graph/LitConstant.shader", source, out Shader? shader));
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(0).HasGlsl);
        Assert.True(shader.GetPass(0).HasSlang);
    }

    [Fact]
    public void Compile_MissingMaster_Throws()
    {
        var graph = new ShaderGraphAsset { ShaderName = "Graph/Empty" };
        var ex = Assert.Throws<ShaderGraphCompileException>(() => ShaderGraphCompiler.CompileToSource(graph));
        Assert.Contains("MasterUnlit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Cycle_Throws()
    {
        var graph = new ShaderGraphAsset { ShaderName = "Graph/Cycle" };
        graph.Nodes.Add(new ShaderGraphNode { Id = "a", Type = "Multiply" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "b", Type = "Multiply" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = "MasterUnlit" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "a", FromPort = "Out", ToNode = "b", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "b", FromPort = "Out", ToNode = "a", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e2", FromNode = "a", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });

        Assert.Throws<ShaderGraphCompileException>(() => ShaderGraphCompiler.CompileToSource(graph));
    }

    [Fact]
    public void TryCompileToShader_UnlitAlbedo_Succeeds()
    {
        Assert.True(ShaderGraphCompiler.TryCompileToShader(
            ShaderGraphAsset.CreateUnlitAlbedoGraph(), out Shader? shader, out string error));
        Assert.True(string.IsNullOrEmpty(error));
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_FullscreenAndDecal_EmitTaggedPasses()
    {
        string fullscreen = ShaderGraphCompiler.CompileToSource(ShaderGraphAsset.CreateFullscreenColorGraph());
        Assert.Contains("Pass \"Fullscreen\"", fullscreen, StringComparison.Ordinal);
        Assert.Contains("\"LightMode\" = \"Fullscreen\"", fullscreen, StringComparison.Ordinal);
        Assert.Contains("GLSLPROGRAM", fullscreen, StringComparison.Ordinal);
        Assert.Contains("SLANGPROGRAM", fullscreen, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("Graph/FullscreenColor.shader", fullscreen, out Shader? fs));
        Assert.NotNull(fs);

        string decal = ShaderGraphCompiler.CompileToSource(ShaderGraphAsset.CreateDecalAlbedoGraph());
        Assert.Contains("Pass \"Decal\"", decal, StringComparison.Ordinal);
        Assert.Contains("\"LightMode\" = \"Decal\"", decal, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("Graph/DecalAlbedo.shader", decal, out Shader? decalShader));
        Assert.NotNull(decalShader);

        var graph = new ShaderGraphAsset { ShaderName = "Graph/FsEmpty", Target = ShaderGraphTargetKind.Fullscreen };
        var ex = Assert.Throws<ShaderGraphCompileException>(() => ShaderGraphCompiler.CompileToSource(graph));
        Assert.Contains("MasterFullscreen", ex.Message, StringComparison.Ordinal);
    }
}
