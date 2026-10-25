// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Backends.WebGPU.Native;

namespace XEngine.Runtime.Backends.WebGPU;

internal sealed class WebGPUBufferResource
{
    public nint Buffer;
    public ulong Size;
    public BufferType Type;
    public bool Dynamic;

    public void Release()
    {
        if (Buffer == 0)
            return;
        WebGPUNative.wgpuBufferRelease(Buffer);
        Buffer = 0;
    }
}

internal sealed class WebGPUTextureResource
{
    public nint Texture;
    public WGPUTextureFormat Format;
    public TextureImageFormat EngineFormat;
    public TextureType Type;
    public uint Width;
    public uint Height;
    public uint Depth = 1;
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
        WebGPUNative.wgpuTextureRelease(Texture);
        Texture = 0;
    }

    public void ReleaseSampler()
    {
        if (Sampler == 0)
            return;
        WebGPUNative.wgpuSamplerRelease(Sampler);
        Sampler = 0;
        SamplerDirty = true;
    }

    public void Release()
    {
        ReleaseSampler();
        ReleaseTexture();
    }
}

internal sealed class WebGPUVertexArrayResource
{
    public uint VertexBuffer;
    public uint IndexBuffer;
    public uint InstanceBuffer;
    public VertexFormat Format = null!;
    public VertexFormat? InstanceFormat;
}

internal sealed class WebGPUFramebufferResource
{
    public uint[] ColorHandles = [];
    public int[] ColorMips = [];
    public nint[] ColorViews = [];
    public WGPUTextureFormat[] ColorFormats = [];
    public uint DepthHandle;
    public int DepthMip;
    public nint DepthView;
    public WGPUTextureFormat DepthFormat;
    public uint Width;
    public uint Height;

    public void ReleaseViews()
    {
        for (int i = 0; i < ColorViews.Length; i++)
        {
            if (ColorViews[i] == 0)
                continue;
            WebGPUNative.wgpuTextureViewRelease(ColorViews[i]);
            ColorViews[i] = 0;
        }

        if (DepthView != 0)
        {
            WebGPUNative.wgpuTextureViewRelease(DepthView);
            DepthView = 0;
        }
    }
}
