// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Echo-serialized shader graph (<c>.shadergraph</c>). Nodes/edges/properties are plain fields
/// so AI CLI can inspect and patch the file as text. Compile with <see cref="ShaderGraphCompiler"/>.
/// </summary>
[CreateAssetMenu("Rendering/Shader Graph", Extension = ".shadergraph", Order = 43)]
public sealed class ShaderGraphAsset : ScriptableObject
{
    public string ShaderName = "Graph/Unnamed";
    public ShaderGraphTargetKind Target = ShaderGraphTargetKind.Unlit;
    public List<ShaderGraphProperty> Properties = new();
    public List<ShaderGraphNode> Nodes = new();
    public List<ShaderGraphEdge> Edges = new();

    public override void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(ShaderName))
            ShaderName = "Graph/Unnamed";
        Properties ??= new List<ShaderGraphProperty>();
        Nodes ??= new List<ShaderGraphNode>();
        Edges ??= new List<ShaderGraphEdge>();
        int target = (int)Target;
        if (target < 0 || target > 3)
            Target = ShaderGraphTargetKind.Unlit;
    }

    /// <summary>Minimal Unlit graph: sample <c>_MainTex</c> * <c>_MainColor</c> into MasterUnlit.Albedo.</summary>
    public static ShaderGraphAsset CreateUnlitAlbedoGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/UnlitAlbedo",
            Target = ShaderGraphTargetKind.Unlit,
        };
        graph.Properties.Add(new ShaderGraphProperty
        {
            Id = "p_maintex",
            Name = "_MainTex",
            DisplayName = "Albedo",
            Type = ShaderGraphValueType.Texture2D,
            DefaultValue = "white",
        });
        graph.Properties.Add(new ShaderGraphProperty
        {
            Id = "p_color",
            Name = "_MainColor",
            DisplayName = "Tint",
            Type = ShaderGraphValueType.Color,
            DefaultValue = "1,1,1,1",
        });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_tex", Type = "SampleTexture2D" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "Texture", Value = "p_maintex" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_col", Type = "Property" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "Property", Value = "p_color" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_mul", Type = "Multiply" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = "MasterUnlit" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_tex", FromPort = "RGBA", ToNode = "n_mul", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "n_col", FromPort = "Out", ToNode = "n_mul", ToPort = "B" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e2", FromNode = "n_mul", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });
        return graph;
    }

    /// <summary>Minimal Lit graph: constant albedo/normal/emission into MasterLit (LightBVH path).</summary>
    public static ShaderGraphAsset CreateLitConstantGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/LitConstant",
            Target = ShaderGraphTargetKind.Lit,
        };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_alb", Type = "Color" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0.8" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0.8" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0.8" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_emi", Type = "Color" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = "MasterLit" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_alb", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "n_emi", FromPort = "RGB", ToNode = "n_master", ToPort = "Emission" });
        return graph;
    }

    /// <summary>
    /// Unlit graph: UV.x lerps red→blue, Fresnel rim is added, UV goes through TilingAndOffset.
    /// </summary>
    public static ShaderGraphAsset CreateFresnelLerpGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/FresnelLerp",
            Target = ShaderGraphTargetKind.Unlit,
        };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_uv", Type = ShaderGraphNodeLibrary.UV });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_tile", Type = ShaderGraphNodeLibrary.TilingAndOffset });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "TilingX", Value = "1" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "TilingY", Value = "1" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "OffsetX", Value = "0" });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "OffsetY", Value = "0" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_split", Type = ShaderGraphNodeLibrary.Split });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_red", Type = ShaderGraphNodeLibrary.Color });
        graph.Nodes[3].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0.85" });
        graph.Nodes[3].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0.15" });
        graph.Nodes[3].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0.1" });
        graph.Nodes[3].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_blue", Type = ShaderGraphNodeLibrary.Color });
        graph.Nodes[4].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0.1" });
        graph.Nodes[4].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0.25" });
        graph.Nodes[4].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0.85" });
        graph.Nodes[4].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_lerp", Type = ShaderGraphNodeLibrary.Lerp });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_fresnel", Type = ShaderGraphNodeLibrary.Fresnel });
        graph.Nodes[6].Fields.Add(new ShaderGraphNodeField { Key = "Power", Value = "5" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_add", Type = ShaderGraphNodeLibrary.Add });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = ShaderGraphNodeLibrary.MasterUnlit });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_uv", FromPort = "Out", ToNode = "n_tile", ToPort = "UV" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "n_tile", FromPort = "Out", ToNode = "n_split", ToPort = "In" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e2", FromNode = "n_red", FromPort = "Out", ToNode = "n_lerp", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e3", FromNode = "n_blue", FromPort = "Out", ToNode = "n_lerp", ToPort = "B" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e4", FromNode = "n_split", FromPort = "R", ToNode = "n_lerp", ToPort = "T" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e5", FromNode = "n_lerp", FromPort = "Out", ToNode = "n_add", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e6", FromNode = "n_fresnel", FromPort = "Out", ToNode = "n_add", ToPort = "B" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e7", FromNode = "n_add", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });
        return graph;
    }

    /// <summary>Lit graph with Metallic / Smoothness master-stack ports (roughness = 1 - smoothness).</summary>
    public static ShaderGraphAsset CreateLitPbrGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/LitPbr",
            Target = ShaderGraphTargetKind.Lit,
        };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_alb", Type = ShaderGraphNodeLibrary.Color });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0.7" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0.7" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0.7" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_met", Type = ShaderGraphNodeLibrary.Float });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "Value", Value = "0.2" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_sm", Type = ShaderGraphNodeLibrary.Float });
        graph.Nodes[2].Fields.Add(new ShaderGraphNodeField { Key = "Value", Value = "0.8" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = ShaderGraphNodeLibrary.MasterLit });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_alb", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "n_met", FromPort = "Out", ToNode = "n_master", ToPort = "Metallic" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e2", FromNode = "n_sm", FromPort = "Out", ToNode = "n_master", ToPort = "Smoothness" });
        return graph;
    }

    /// <summary>Unlit graph that offsets object-space vertex position along the object normal.</summary>
    public static ShaderGraphAsset CreateVertexOffsetGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/VertexOffset",
            Target = ShaderGraphTargetKind.Unlit,
        };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_pos", Type = ShaderGraphNodeLibrary.Position });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "Space", Value = "Object" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_nrm", Type = ShaderGraphNodeLibrary.Normal });
        graph.Nodes[1].Fields.Add(new ShaderGraphNodeField { Key = "Space", Value = "Object" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_amt", Type = ShaderGraphNodeLibrary.Float });
        graph.Nodes[2].Fields.Add(new ShaderGraphNodeField { Key = "Value", Value = "0.05" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_mul", Type = ShaderGraphNodeLibrary.Multiply });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_add", Type = ShaderGraphNodeLibrary.Add });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_col", Type = ShaderGraphNodeLibrary.Color });
        graph.Nodes[5].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "1" });
        graph.Nodes[5].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "1" });
        graph.Nodes[5].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "1" });
        graph.Nodes[5].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = ShaderGraphNodeLibrary.MasterUnlit });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_nrm", FromPort = "Out", ToNode = "n_mul", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e1", FromNode = "n_amt", FromPort = "Out", ToNode = "n_mul", ToPort = "B" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e2", FromNode = "n_pos", FromPort = "Out", ToNode = "n_add", ToPort = "A" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e3", FromNode = "n_mul", FromPort = "Out", ToNode = "n_add", ToPort = "B" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e4", FromNode = "n_add", FromPort = "Out", ToNode = "n_master", ToPort = "Position" });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e5", FromNode = "n_col", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });
        return graph;
    }

    /// <summary>Minimal Fullscreen graph: constant color into MasterFullscreen.Color.</summary>
    public static ShaderGraphAsset CreateFullscreenColorGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/FullscreenColor",
            Target = ShaderGraphTargetKind.Fullscreen,
        };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_col", Type = ShaderGraphNodeLibrary.Color });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0.2" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0.4" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0.8" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = ShaderGraphNodeLibrary.MasterFullscreen });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_col", FromPort = "Out", ToNode = "n_master", ToPort = "Color" });
        return graph;
    }

    /// <summary>Minimal Decal graph: constant albedo into MasterDecal.Albedo.</summary>
    public static ShaderGraphAsset CreateDecalAlbedoGraph()
    {
        var graph = new ShaderGraphAsset
        {
            ShaderName = "Graph/DecalAlbedo",
            Target = ShaderGraphTargetKind.Decal,
        };
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_col", Type = ShaderGraphNodeLibrary.Color });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "R", Value = "0.9" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "G", Value = "0.2" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "B", Value = "0.1" });
        graph.Nodes[0].Fields.Add(new ShaderGraphNodeField { Key = "A", Value = "1" });
        graph.Nodes.Add(new ShaderGraphNode { Id = "n_master", Type = ShaderGraphNodeLibrary.MasterDecal });
        graph.Edges.Add(new ShaderGraphEdge { Id = "e0", FromNode = "n_col", FromPort = "Out", ToNode = "n_master", ToPort = "Albedo" });
        return graph;
    }
}
