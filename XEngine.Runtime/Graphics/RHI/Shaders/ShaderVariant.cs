// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// A compiled shader variant for the active backend: either a linked OpenGL / WebGL2
/// <see cref="GraphicsProgram"/> or a modern-backend <see cref="CompiledShaderBytecode"/>.
/// </summary>
public sealed class ShaderVariant : IDisposable
{
    private static int s_nextId;

    public int Id { get; } = Interlocked.Increment(ref s_nextId);
    public GraphicsProgram? GlProgram { get; }
    public CompiledShaderBytecode? Bytecode { get; }

    public bool IsOpenGl => GlProgram != null;
    public bool IsBytecode => Bytecode != null;

    /// <summary>Frame Debugger identity: the pass this variant was resolved for.</summary>
    public Rendering.Shaders.ShaderPass? SourcePass { get; private set; }

    /// <summary>Keyword key the variant was resolved with (see ShaderPass.BuildKeywordKey).</summary>
    public string? SourceKeywords { get; private set; }

    internal void SetDebugSource(Rendering.Shaders.ShaderPass pass, string keywords)
    {
        SourcePass = pass;
        SourceKeywords = keywords;
    }

    public ShaderVariant(GraphicsProgram program)
    {
        GlProgram = program ?? throw new ArgumentNullException(nameof(program));
    }

    public ShaderVariant(CompiledShaderBytecode bytecode)
    {
        Bytecode = bytecode ?? throw new ArgumentNullException(nameof(bytecode));
    }

    public void Dispose()
    {
        GlProgram?.Dispose();
    }
}
