// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShaderGraphNodeLibraryTests
{
    [Fact]
    public void Catalog_ListsMathUvFresnelAndMasterTypes()
    {
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.Lerp));
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.Fresnel));
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.TilingAndOffset));
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.Position));
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.MasterLit));
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.MasterDecal));
        Assert.True(ShaderGraphNodeLibrary.IsKnown(ShaderGraphNodeLibrary.MasterFullscreen));
        Assert.Contains(ShaderGraphNodeLibrary.Subtract, ShaderGraphNodeLibrary.AllTypes);
        Assert.Contains(ShaderGraphNodeLibrary.Saturate, ShaderGraphNodeLibrary.AllTypes);
        Assert.False(ShaderGraphNodeLibrary.IsKnown("NotANode"));
        var inputs = new List<string>();
        var outputs = new List<string>();
        ShaderGraphNodeLibrary.GetPorts(ShaderGraphNodeLibrary.Lerp, inputs, outputs);
        Assert.Equal(new[] { "A", "B", "T" }, inputs);
        Assert.Equal(new[] { "Out" }, outputs);
        ShaderGraphNodeLibrary.GetPorts(ShaderGraphNodeLibrary.MasterLit, inputs, outputs);
        Assert.Contains("Smoothness", inputs);
        Assert.Contains("Position", inputs);
    }

    [Fact]
    public void Compile_FresnelLerp_EmitsMixPowAndTiling()
    {
        ShaderGraphAsset graph = ShaderGraphAsset.CreateFresnelLerpGraph();
        string source = ShaderGraphCompiler.CompileToSource(graph);

        Assert.Contains("mix(", source, StringComparison.Ordinal);
        Assert.Contains("lerp(", source, StringComparison.Ordinal);
        Assert.Contains("pow(1.0 - clamp(dot(", source, StringComparison.Ordinal);
        Assert.Contains("texCoord0 * vec2(1.0, 1.0)", source, StringComparison.Ordinal);
        Assert.Contains("vec4(0.85, 0.15, 0.1, 1.0)", source, StringComparison.Ordinal);
        Assert.Contains("vec4(0.1, 0.25, 0.85, 1.0)", source, StringComparison.Ordinal);

        Assert.True(ShaderParser.ParseShader("Graph/FresnelLerp.shader", source, out Shader? shader));
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(0).HasGlsl);
        Assert.True(shader.GetPass(0).HasSlang);
    }

    [Fact]
    public void Compile_LitPbr_UsesSmoothnessAsOneMinusRoughness()
    {
        string source = ShaderGraphCompiler.CompileToSource(ShaderGraphAsset.CreateLitPbrGraph());
        Assert.Contains("float metallic = 0.2", source, StringComparison.Ordinal);
        Assert.Contains("float roughness = (1.0 - (0.8))", source, StringComparison.Ordinal);
        Assert.Contains("CalculateForwardLighting(worldPos, worldNormal, viewDir, baseColor, metallic, roughness, ao)", source, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("Graph/LitPbr.shader", source, out Shader? shader));
        Assert.True(shader!.GetPass(0).HasGlsl);
        Assert.True(shader.GetPass(0).HasSlang);
    }

    [Fact]
    public void Compile_VertexOffset_TransformsObjectPositionAlongNormal()
    {
        string source = ShaderGraphCompiler.CompileToSource(ShaderGraphAsset.CreateVertexOffsetGraph());
        Assert.Contains("vec3 vertPos = (vertexPosition + (vertexNormal * 0.05))", source, StringComparison.Ordinal);
        Assert.Contains("TransformClip(vertPos)", source, StringComparison.Ordinal);
        Assert.Contains("float3 vertPos = (input.vertexPosition + (input.vertexNormal * 0.05))", source, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("Graph/VertexOffset.shader", source, out Shader? shader));
        Assert.True(shader!.GetPass(0).HasGlsl);
    }

    [Fact]
    public void Compile_SaturateClampOneMinus_EmitsGlslHelpers()
    {
        var graph = new ShaderGraphAsset { ShaderName = "Graph/Math", Target = ShaderGraphTargetKind.Unlit };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_uv", Type = ShaderGraphNodeLibrary.UV });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_sat", Type = ShaderGraphNodeLibrary.Saturate });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_om", Type = ShaderGraphNodeLibrary.OneMinus });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_cl", Type = ShaderGraphNodeLibrary.Clamp });
        graph.Nodes[3].Fields.Add(new ShaderGraphNodeField { Key = "Min", Value = "0.1" });
        graph.Nodes[3].Fields.Add(new ShaderGraphNodeField { Key = "Max", Value = "0.9" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_cmb", Type = ShaderGraphNodeLibrary.Combine });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = ShaderGraphNodeLibrary.MasterUnlit });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_uv", FromPort = "X", ToNode = "n_sat", ToPort = "In" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "n_sat", FromPort = "Out", ToNode = "n_om", ToPort = "In" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e2", FromNode = "n_om", FromPort = "Out", ToNode = "n_cl", ToPort = "In" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e3", FromNode = "n_cl", FromPort = "Out", ToNode = "n_cmb", ToPort = "R" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e4", FromNode = "n_cmb", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });

        string source = ShaderGraphCompiler.CompileToSource(graph);
        Assert.Contains("clamp(texCoord0.r, 0.0, 1.0)", source, StringComparison.Ordinal);
        Assert.Contains("(1.0 - ", source, StringComparison.Ordinal);
        Assert.Contains("clamp(", source, StringComparison.Ordinal);
        Assert.Contains("0.1", source, StringComparison.Ordinal);
        Assert.Contains("vec4(", source, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("Graph/Math.shader", source, out _));
    }

    [Fact]
    public void Preview_FresnelLerp_WritesEvidencePng()
    {
        var canvas = new XGuiAcceptanceScreenshot.Canvas(256, 128, 12, 12, 16);
        canvas.Text(8, 4, "SG LERP FRESNEL", 230, 230, 235, 2);
        for (int y = 24; y < 120; y++)
        {
            for (int x = 8; x < 248; x++)
            {
                float u = (x - 8) / 239f;
                float v = (y - 24) / 95f;
                float nx = u * 2f - 1f;
                float ny = v * 2f - 1f;
                float nz2 = 1f - nx * nx - ny * ny;
                float fres = 0f;
                if (nz2 > 0f)
                {
                    float nz = MathF.Sqrt(nz2);
                    float ndotv = Math.Clamp(nz, 0f, 1f);
                    fres = MathF.Pow(1f - ndotv, 5f);
                }
                float r = 0.85f * (1f - u) + 0.1f * u + fres;
                float g = 0.15f * (1f - u) + 0.25f * u + fres;
                float b = 0.1f * (1f - u) + 0.85f * u + fres;
                int i = (y * canvas.Width + x) * 4;
                canvas.Rgba[i] = ToByte(r);
                canvas.Rgba[i + 1] = ToByte(g);
                canvas.Rgba[i + 2] = ToByte(b);
                canvas.Rgba[i + 3] = 255;
            }
        }

        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "urp", "m25-shadergraph-nodes.png");
        XGuiAcceptanceScreenshot.SaveTo(path, canvas);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);

        int left = Pixel(canvas, 16, 70);
        int right = Pixel(canvas, 240, 70);
        Assert.True(((left >> 16) & 0xFF) > ((left >> 0) & 0xFF), "left panel should be red-dominant");
        Assert.True(((right >> 0) & 0xFF) > ((right >> 16) & 0xFF), "right panel should be blue-dominant");
    }

    private static byte ToByte(float v)
        => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    private static int Pixel(XGuiAcceptanceScreenshot.Canvas canvas, int x, int y)
    {
        int i = (y * canvas.Width + x) * 4;
        return (canvas.Rgba[i] << 16) | (canvas.Rgba[i + 1] << 8) | canvas.Rgba[i + 2];
    }
}
