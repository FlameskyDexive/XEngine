// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.OpenGL;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>OpenGL shader compilation, linking, binding, and deletion kept behind the backend boundary.</summary>
internal static class OpenGLProgramOperations
{
    public static void Compile(GL gl, GraphicsProgram program)
    {
        program.Handle = gl.CreateProgram();

        CompileAndAttach(gl, program, program.PendingFragmentSource, ShaderType.FragmentShader, "Fragment");
        CompileAndAttach(gl, program, program.PendingVertexSource, ShaderType.VertexShader, "Vertex");
        CompileAndAttach(gl, program, program.PendingGeometrySource, ShaderType.GeometryShader, "Geometry");

        gl.LinkProgram(program.Handle);
        string info = ReadInfoLog(() =>
        {
            gl.GetProgramInfoLog(program.Handle, out string log);
            return log;
        });
        gl.GetProgram(program.Handle, ProgramPropertyARB.LinkStatus, out int statusCode);
        if (statusCode != 1)
        {
            program.MarkCompilationFailed();
            gl.DeleteProgram(program.Handle);
            program.Handle = 0;
            throw new InvalidOperationException("Failed to Link Shader Program.\n" +
                info + "\n\nStatus Code: " + statusCode.ToString());
        }

        gl.Flush();
        program.ClearPendingSources();
    }

    public static void Use(GL gl, GraphicsProgram? program)
    {
        if (program == null)
        {
            gl.UseProgram(0);
            return;
        }

        if (GraphicsProgram.currentProgram != null && GraphicsProgram.currentProgram.Handle == program.Handle)
            return;

        gl.UseProgram(program.Handle);
        GraphicsProgram.currentProgram = program;
    }

    public static void Delete(GL gl, GraphicsProgram program)
    {
        if (program.Handle == 0)
            return;

        if (GraphicsProgram.currentProgram != null && GraphicsProgram.currentProgram.Handle == program.Handle)
            GraphicsProgram.currentProgram = null;
        gl.DeleteProgram(program.Handle);
        program.Handle = 0;
    }

    private static void CompileAndAttach(
        GL gl,
        GraphicsProgram program,
        string? source,
        ShaderType shaderType,
        string stageName)
    {
        if (string.IsNullOrEmpty(source))
            return;

        uint shader = gl.CreateShader(shaderType);
        Debug.Log("[XEngine] GL create-shader " + stageName + " src=" + source.Length);
        gl.ShaderSource(shader, source);
        Debug.Log("[XEngine] GL shader-source " + stageName);
        gl.CompileShader(shader);
        Debug.Log("[XEngine] GL compile-shader " + stageName);

        string info = ReadInfoLog(() =>
        {
            gl.GetShaderInfoLog(shader, out string log);
            return log;
        });
        Debug.Log("[XEngine] GL infolog " + stageName + " len=" + info.Length);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int statusCode);
        if (statusCode != 1)
        {
            program.MarkCompilationFailed();
            gl.DeleteShader(shader);
            gl.DeleteProgram(program.Handle);
            program.Handle = 0;
            throw new InvalidOperationException($"Failed to Compile {stageName} Shader Source.\n" +
                info + "\n\nStatus Code: " + statusCode.ToString());
        }

        gl.AttachShader(program.Handle, shader);
        gl.DeleteShader(shader);
    }

    /// <summary>
    /// Silk.NET <c>GetShaderInfoLog</c> / <c>GetProgramInfoLog</c> call
    /// <c>string.Substring</c> with <c>length-1</c>. On WebGL2/wasm a zero-length
    /// log becomes <c>Substring(0, -1)</c> and throws. Empty log is success.
    /// </summary>
    private static string ReadInfoLog(Func<string> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }
}
