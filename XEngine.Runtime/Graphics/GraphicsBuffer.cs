// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

public class GraphicsBuffer : IDisposable
{
    private readonly RHI.IGraphicsDevice? _ownerDevice;

    public bool IsDisposed { get; protected set; }

    // Handle and SizeInBytes are filled by the active backend's CreateBuffer opcode.
    public uint Handle { get; internal set; }
    public RHI.GpuHandle NativeHandle => new(Handle);
    public readonly BufferType OriginalType;
    public uint SizeInBytes { get; internal set; }

    public GraphicsBuffer(BufferType type, ReadOnlySpan<byte> initialData, bool dynamic)
    {
        if (type == BufferType.Count)
            throw new ArgumentOutOfRangeException(nameof(type), type, null);

        OriginalType = type;
        _ownerDevice = Graphics.Device;
        // CPU mirror of the backend allocation size. The command translator updates it when
        // storage is replaced by a later full-buffer update.
        SizeInBytes = (uint)initialData.Length;
        Handle = 0;

        using var cmd = Graphics.GetCommandBuffer("GraphicsBuffer.Create");
        cmd.EncodeCreateBuffer(this, dynamic, initialData);
        Graphics.Submit(cmd);
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        if (!ReferenceEquals(_ownerDevice, Graphics.Device))
            return;

        // Queue the GL delete so it runs after any encoded use of this buffer that
        // hasn't executed yet. Sync submit makes it immediate; render-thread mode
        // makes it deferred but still ordered.
        using var cmd = Graphics.GetCommandBuffer("GraphicsBuffer.Dispose");
        cmd.EncodeDisposeBuffer(this);
        Graphics.Submit(cmd);
    }

    public override string ToString()
    {
        return Handle.ToString();
    }
}
