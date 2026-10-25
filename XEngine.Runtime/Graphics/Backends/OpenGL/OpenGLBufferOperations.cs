// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.OpenGL;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>OpenGL buffer allocation and mutation kept behind the backend boundary.</summary>
internal static unsafe class OpenGLBufferOperations
{
    public static void Create(
        GL gl,
        OpenGLCommandExecutor executor,
        GraphicsBuffer buffer,
        bool dynamic,
        ReadOnlySpan<byte> data)
    {
        buffer.Handle = gl.GenBuffer();
        if (data.Length == 0)
            return;

        Set(gl, executor, buffer, data, dynamic);
    }

    public static void Update(
        GL gl,
        OpenGLCommandExecutor executor,
        GraphicsBuffer buffer,
        uint destinationOffset,
        ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        if (destinationOffset == 0 && data.Length >= buffer.SizeInBytes)
        {
            Set(gl, executor, buffer, data, dynamic: true);
            return;
        }

        BufferTargetARB target = Bind(gl, executor, buffer);
        fixed (byte* dataPtr = data)
            gl.BufferSubData(target, (nint)destinationOffset, (nuint)data.Length, dataPtr);
    }

    private static void Set(
        GL gl,
        OpenGLCommandExecutor executor,
        GraphicsBuffer buffer,
        ReadOnlySpan<byte> data,
        bool dynamic)
    {
        BufferTargetARB target = Bind(gl, executor, buffer);
        BufferUsageARB usage = dynamic ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw;
        fixed (byte* dataPtr = data)
            gl.BufferData(target, (nuint)data.Length, dataPtr, usage);
        buffer.SizeInBytes = (uint)data.Length;
    }

    private static BufferTargetARB Bind(GL gl, OpenGLCommandExecutor executor, GraphicsBuffer buffer)
    {
        BufferTargetARB target = ToTarget(buffer.OriginalType);
        if (buffer.OriginalType == BufferType.ElementsBuffer)
        {
            // ELEMENT_ARRAY_BUFFER belongs to VAO state. Upload through VAO 0 so an update cannot
            // replace the index buffer retained by whichever user VAO was previously bound.
            gl.BindVertexArray(0);
            executor.InvalidateBoundVAO();
        }

        gl.BindBuffer(target, buffer.Handle);
        return target;
    }

    private static BufferTargetARB ToTarget(BufferType type) => type switch
    {
        BufferType.VertexBuffer => BufferTargetARB.ArrayBuffer,
        BufferType.ElementsBuffer => BufferTargetARB.ElementArrayBuffer,
        BufferType.UniformBuffer => BufferTargetARB.UniformBuffer,
        BufferType.StructuredBuffer => BufferTargetARB.ShaderStorageBuffer,
        BufferType.IndirectBuffer => BufferTargetARB.DrawIndirectBuffer,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
