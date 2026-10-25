// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// OpenGL / Null path: injects <c>#version</c> and keyword <c>#define</c>s into GLSL
/// source. Actual GL compile/link stays in <see cref="GraphicsProgram"/>.
/// </summary>
public sealed class GlslShaderCompiler : IShaderCompiler
{
    public const string DefaultGlslVersion = "410";

    public ShaderCompileResult Compile(ShaderCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Language != ShaderLanguage.Glsl)
        {
            return ShaderCompileResult.Fail(
                $"GlslShaderCompiler only accepts GLSL source (got {request.Language}).");
        }

        if (string.IsNullOrWhiteSpace(request.VertexSource))
            return ShaderCompileResult.Fail("Vertex source is null or empty.");
        if (string.IsNullOrWhiteSpace(request.FragmentSource))
            return ShaderCompileResult.Fail("Fragment source is null or empty.");

        string vert = InjectPreamble(request.VertexSource, request.Keywords, request.ExtraDefines);
        string frag = InjectPreamble(request.FragmentSource, request.Keywords, request.ExtraDefines);
        string? geom = string.IsNullOrWhiteSpace(request.GeometrySource)
            ? null
            : InjectPreamble(request.GeometrySource, request.Keywords, request.ExtraDefines);

        return new ShaderCompileResult
        {
            Success = true,
            Format = ShaderBytecodeFormat.GlslSource,
            GlslVertexSource = vert,
            GlslFragmentSource = frag,
            GlslGeometrySource = geom,
            BindingLayout = new ShaderBindingLayout(),
        };
    }

    /// <summary>
    /// Prepends <c>#version</c>, keyword defines, and <c>FRAGMENT_VERSION</c> the same way
    /// <see cref="Rendering.Shaders.ShaderPass"/> historically did inline.
    /// </summary>
    public static string InjectPreamble(
        string source,
        IReadOnlyDictionary<string, bool>? keywords,
        IReadOnlyList<string>? extraDefines = null,
        string glslVersion = DefaultGlslVersion)
    {
        var sb = new StringBuilder(source.Length + 256);
        sb.Append("#version ").Append(glslVersion).Append('\n');

        if (extraDefines != null)
        {
            for (int i = 0; i < extraDefines.Count; i++)
            {
                string define = extraDefines[i];
                if (!string.IsNullOrWhiteSpace(define))
                    sb.Append("#define ").Append(define).Append('\n');
            }
        }

        if (keywords != null)
        {
            foreach (KeyValuePair<string, bool> kvp in keywords)
            {
                if (kvp.Value)
                    sb.Append("#define ").Append(kvp.Key).Append('\n');
            }
        }

        sb.Append("#define FRAGMENT_VERSION 1\n");
        sb.Append(source);
        return sb.ToString();
    }
}
