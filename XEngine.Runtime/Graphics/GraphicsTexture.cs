// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

public class GraphicsTexture : IDisposable
{
    private readonly RHI.IGraphicsDevice? _ownerDevice;

    // Handle is 0 until the executor's CreateTexture opcode runs on the render
    // thread and writes the real GL name back.
    public uint Handle { get; internal set; }
    public RHI.GpuHandle NativeHandle => new(Handle);
    public TextureType Type { get; protected set; }

    /// <summary>Engine format enum retained for non-GL backends.</summary>
    internal TextureImageFormat ImageFormat { get; }

    /// <summary>
    /// Number of mip levels requested by the next allocation. The value is set by resource
    /// deserialization before the first upload; backends keep their existing single-level default
    /// for all runtime-created textures.
    /// </summary>
    internal uint MipLevelsHint { get; set; } = 1;

    /// <summary>
    /// Multisample count for the next allocation (1 = single-sample). Backends read this when the
    /// AllocateTexture2D opcode runs, so it must be set before the texture is allocated — the
    /// constructor overload taking a sample count is the supported way to do that.
    /// <para>
    /// A multisample texture is a render target only: it has exactly one mip level, is never
    /// sampled by a shader, cannot be uploaded to or read back, and must be resolved into a
    /// single-sample texture (<c>CommandBuffer.ResolveTexture</c>) before any of that is legal.
    /// </para>
    /// </summary>
    public uint SampleCount { get; internal set; } = 1;

    /// <summary>True when this texture is a multisample render target.</summary>
    public bool IsMultisampled => SampleCount > 1;

    public GraphicsTexture(TextureType type, TextureImageFormat format)
        : this(type, format, 1)
    {
    }

    public GraphicsTexture(TextureType type, TextureImageFormat format, uint sampleCount)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type), type, null);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, null);
        if (sampleCount < 1 || (sampleCount & (sampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Sample count must be a power of two.");
        if (sampleCount > 1 && type != TextureType.Texture2D)
            throw new ArgumentException("Only Texture2D supports multisampling.", nameof(type));

        Type = type;
        ImageFormat = format;
        SampleCount = sampleCount;
        _ownerDevice = Graphics.Device;
        Handle = 0;

        using var cmd = Graphics.GetCommandBuffer("GraphicsTexture.Create");
        cmd.EncodeCreateTexture(this);
        Graphics.Submit(cmd);
    }

    public bool IsDisposed { get; protected set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        // The owning device may already be torn down (Graphics.Dispose runs before stray textures
        // are finalized), and the texture may have been created on a device that no longer matches
        // the current one. In either case there is nothing left to submit the disposal against —
        // and the finalizer thread must never throw (a GPU handle leaking once at shutdown is
        // harmless compared to crashing the process).
        if (Graphics.Device == null || !ReferenceEquals(_ownerDevice, Graphics.Device))
            return;

        using var cmd = Graphics.GetCommandBuffer("GraphicsTexture.Dispose");
        cmd.EncodeDisposeTexture(this);
        Graphics.Submit(cmd);
    }

    public override string ToString()
    {
        return Handle.ToString();
    }
}
