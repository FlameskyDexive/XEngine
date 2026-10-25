// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Deterministic Shader Graph → XEngine Shader DSL compiler. Emits both GLSLPROGRAM and
/// SLANGPROGRAM. Lit target uses the existing LightBVH forward lighting includes.
/// </summary>
public static class ShaderGraphCompiler
{
    public static string CompileToSource(ShaderGraphAsset graph)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        graph.OnValidate();
        return ShaderGraphSourceCompiler.CompileToSource(
            graph.ShaderName, graph.Target, graph.Properties, graph.Nodes, graph.Edges);
    }

    public static bool TryCompileToShader(ShaderGraphAsset graph, out Shader? shader, out string error)
    {
        shader = null;
        error = "";
        try
        {
            string source = CompileToSource(graph);
            if (!ShaderParser.ParseShader(graph.ShaderName + ".shader", source, out shader) || shader == null)
            {
                error = "ShaderParser rejected generated source.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
