// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

public class GraphicsVertexArray : IDisposable
{
    private readonly RHI.IGraphicsDevice? _ownerDevice;

    public uint Handle { get; internal set; }
    public RHI.GpuHandle NativeHandle => new(Handle);
    public bool IsDisposed { get; protected set; }

    // Construction params retained for deferred creation by each backend.
    private readonly VertexFormat _format;
    private readonly GraphicsBuffer _vertices;
    private readonly GraphicsBuffer? _indices;
    private readonly VertexFormat? _instanceFormat;
    private readonly GraphicsBuffer? _instanceBuffer;

    internal VertexFormat Format => _format;
    internal GraphicsBuffer Vertices => _vertices;
    internal GraphicsBuffer? Indices => _indices;
    internal VertexFormat? InstanceFormat => _instanceFormat;
    internal GraphicsBuffer? InstanceBuffer => _instanceBuffer;

    public GraphicsVertexArray(
        VertexFormat format,
        GraphicsBuffer vertices,
        GraphicsBuffer? indices,
        VertexFormat? instanceFormat = null,
        GraphicsBuffer? instanceBuffer = null)
    {
        _format = format;
        _ownerDevice = Graphics.Device;
        _vertices = vertices;
        _indices = indices;
        _instanceFormat = instanceFormat;
        _instanceBuffer = instanceBuffer;
        Handle = 0;

        using var cmd = Graphics.GetCommandBuffer("GraphicsVertexArray.Create");
        cmd.EncodeCreateVertexArray(this);
        Graphics.Submit(cmd);
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        if (!ReferenceEquals(_ownerDevice, Graphics.Device))
            return;

        using var cmd = Graphics.GetCommandBuffer("GraphicsVertexArray.Dispose");
        cmd.EncodeDisposeVertexArray(this);
        Graphics.Submit(cmd);
    }

    public override string ToString()
    {
        return Handle.ToString();
    }
}
