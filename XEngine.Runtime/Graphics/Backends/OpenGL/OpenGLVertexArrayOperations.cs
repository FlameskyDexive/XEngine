// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.OpenGL;

using static XEngine.Runtime.VertexFormat;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>OpenGL vertex-array creation and attribute binding kept behind the backend boundary.</summary>
internal static unsafe class OpenGLVertexArrayOperations
{
    public static void Create(GL gl, GraphicsVertexArray vertexArray)
    {
        vertexArray.Handle = gl.GenVertexArray();
        if (vertexArray.Handle == 0)
            throw new Exception("Failed to create VAO - glGenVertexArray returned 0");

        gl.BindVertexArray(vertexArray.Handle);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexArray.Vertices.Handle);
        BindFormat(gl, vertexArray.Format);

        if (vertexArray.InstanceFormat != null && vertexArray.InstanceBuffer != null)
        {
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexArray.InstanceBuffer.Handle);
            BindFormat(gl, vertexArray.InstanceFormat);
        }

        // Element-array binding is VAO state, so record it while this VAO is bound.
        if (vertexArray.Indices != null)
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, vertexArray.Indices.Handle);

        gl.BindVertexArray(0);
    }

    public static void Delete(GL gl, GraphicsVertexArray vertexArray)
    {
        if (vertexArray.Handle == 0)
            return;

        gl.DeleteVertexArray(vertexArray.Handle);
        vertexArray.Handle = 0;
    }

    private static void BindFormat(GL gl, VertexFormat format)
    {
        for (int i = 0; i < format.Elements.Length; i++)
        {
            Element element = format.Elements[i];
            uint index = element.Semantic;
            gl.EnableVertexAttribArray(index);
            void* offset = (void*)element.Offset;
            if (element.Type == VertexType.Float || element.Normalized)
            {
                gl.VertexAttribPointer(
                    index,
                    element.Count,
                    (GLEnum)element.Type,
                    element.Normalized,
                    (uint)format.Size,
                    offset);
            }
            else
            {
                gl.VertexAttribIPointer(
                    index,
                    element.Count,
                    (GLEnum)element.Type,
                    (uint)format.Size,
                    offset);
            }

            if (element.Divisor > 0)
                gl.VertexAttribDivisor(index, (uint)element.Divisor);
        }
    }
}
