// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Backends.Metal.Native;

namespace XEngine.Runtime.Backends.Metal;

internal sealed class MetalBufferResource
{
    public nint Buffer;
    public ulong Size;
    public BufferType Type;
    public bool Dynamic;

    public void Release()
    {
        if (Buffer == 0)
            return;
        MetalNative.Release(Buffer);
        Buffer = 0;
    }
}

internal sealed class MetalTextureResource
{
    public nint Texture;
    public MTLPixelFormat Format;
    public TextureImageFormat EngineFormat;
    public TextureType Type;
    public uint Width;
    public uint Height;
    public uint MipLevels = 1;
    public TextureWrap WrapS = TextureWrap.Repeat;
    public TextureWrap WrapT = TextureWrap.Repeat;
    public TextureWrap WrapR = TextureWrap.Repeat;
    public TextureMin MinFilter = TextureMin.Linear;
    public TextureMag MagFilter = TextureMag.Linear;
    public bool CompareEnabled;
    public nint Sampler;
    public bool SamplerDirty = true;

    public void ReleaseTexture()
    {
        if (Texture == 0)
            return;
        MetalNative.Release(Texture);
        Texture = 0;
    }

    public void ReleaseSampler()
    {
        if (Sampler == 0)
            return;
        MetalNative.Release(Sampler);
        Sampler = 0;
        SamplerDirty = true;
    }

    public void Release()
    {
        ReleaseSampler();
        ReleaseTexture();
    }
}

internal sealed class MetalVertexArrayResource
{
    public uint VertexBuffer;
    public uint InstanceBuffer;
    public uint IndexBuffer;
    public VertexFormat Format = null!;
    public VertexFormat? InstanceFormat;
}

internal sealed class MetalFramebufferResource
{
    public uint[] ColorHandles = [];
    public int[] ColorMips = [];
    public uint DepthHandle;
    public int DepthMip;
    public uint Width;
    public uint Height;
}
