// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.WebGPU.Native;

namespace XEngine.Runtime.Backends.WebGPU;

/// <summary>
/// Maps engine enums onto unified <c>webgpu.h</c> / wgpu-native v29.
/// WebGPU has no 3-component texture formats; those engine formats throw.
/// </summary>
internal static class WebGPUFormats
{
    public static int BytesPerPixel(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b or TextureImageFormat.Color4bSrgb => 4,
        TextureImageFormat.Byte => 1,
        TextureImageFormat.Short => 2,
        TextureImageFormat.Short2 => 4,
        TextureImageFormat.Short3 => 6,
        TextureImageFormat.Short4 => 8,
        TextureImageFormat.Float or TextureImageFormat.Int or TextureImageFormat.UnsignedInt => 4,
        TextureImageFormat.Float2 or TextureImageFormat.Int2 or TextureImageFormat.UnsignedInt2 => 8,
        TextureImageFormat.Float3 or TextureImageFormat.Int3 or TextureImageFormat.UnsignedInt3 => 12,
        TextureImageFormat.Float4 or TextureImageFormat.Int4 or TextureImageFormat.UnsignedInt4 => 16,
        TextureImageFormat.UnsignedShort => 2,
        TextureImageFormat.UnsignedShort2 => 4,
        TextureImageFormat.UnsignedShort3 => 6,
        TextureImageFormat.UnsignedShort4 => 8,
        TextureImageFormat.Depth16f => 2,
        TextureImageFormat.Depth24f or TextureImageFormat.Depth32f or TextureImageFormat.Depth24Stencil8 => 4,
        _ => throw new NotSupportedException($"WebGPU initial upload for {format} is not supported."),
    };

    public static bool IsCompressed(TextureImageFormat format) => format is
        TextureImageFormat.Etc2Rgb8 or TextureImageFormat.Etc2Rgba8 or TextureImageFormat.Astc4x4 or
        TextureImageFormat.Etc2Rgb8Srgb or TextureImageFormat.Etc2Rgba8Srgb or TextureImageFormat.Astc4x4Srgb;

    public static int CompressedBlockBytes(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Etc2Rgb8 or TextureImageFormat.Etc2Rgb8Srgb => 8,
        TextureImageFormat.Etc2Rgba8 or TextureImageFormat.Astc4x4 or
        TextureImageFormat.Etc2Rgba8Srgb or TextureImageFormat.Astc4x4Srgb => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a compressed format."),
    };

    public static int CompressedDataSize(TextureImageFormat format, uint width, uint height)
    {
        if (!IsCompressed(format))
            throw new ArgumentException("Format is not block-compressed.", nameof(format));
        uint blocksWide = (width + 3) / 4;
        uint blocksHigh = (height + 3) / 4;
        return checked((int)(blocksWide * blocksHigh * (uint)CompressedBlockBytes(format)));
    }

    public static bool IsDepth(TextureImageFormat format) =>
        format is TextureImageFormat.Depth16f
            or TextureImageFormat.Depth24f
            or TextureImageFormat.Depth32f
            or TextureImageFormat.Depth24Stencil8;

    public static bool HasStencil(WGPUTextureFormat format) =>
        format is WGPUTextureFormat.Depth24PlusStencil8 or WGPUTextureFormat.Depth32FloatStencil8;

    public static bool IsThreeComponent(TextureImageFormat format) => format is
        TextureImageFormat.Short3
        or TextureImageFormat.Float3
        or TextureImageFormat.Int3
        or TextureImageFormat.UnsignedShort3
        or TextureImageFormat.UnsignedInt3;

    public static WGPUTextureFormat ToWgpuFormat(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => WGPUTextureFormat.RGBA8Unorm,
        TextureImageFormat.Color4bSrgb => WGPUTextureFormat.RGBA8UnormSrgb,
        TextureImageFormat.Byte => WGPUTextureFormat.R8Unorm,
        TextureImageFormat.Float => WGPUTextureFormat.R32Float,
        TextureImageFormat.Float2 => WGPUTextureFormat.RG32Float,
        TextureImageFormat.Float4 => WGPUTextureFormat.RGBA32Float,
        TextureImageFormat.Short => WGPUTextureFormat.R16Float,
        TextureImageFormat.Short2 => WGPUTextureFormat.RG16Float,
        TextureImageFormat.Short4 => WGPUTextureFormat.RGBA16Float,
        TextureImageFormat.Int => WGPUTextureFormat.R32Sint,
        TextureImageFormat.Int2 => WGPUTextureFormat.RG32Sint,
        TextureImageFormat.Int4 => WGPUTextureFormat.RGBA32Sint,
        TextureImageFormat.UnsignedShort => WGPUTextureFormat.R16Uint,
        TextureImageFormat.UnsignedShort2 => WGPUTextureFormat.RG16Uint,
        TextureImageFormat.UnsignedShort4 => WGPUTextureFormat.RGBA16Uint,
        TextureImageFormat.UnsignedInt => WGPUTextureFormat.R32Uint,
        TextureImageFormat.UnsignedInt2 => WGPUTextureFormat.RG32Uint,
        TextureImageFormat.UnsignedInt4 => WGPUTextureFormat.RGBA32Uint,
        TextureImageFormat.Depth16f => WGPUTextureFormat.Depth16Unorm,
        TextureImageFormat.Depth24f => WGPUTextureFormat.Depth24Plus,
        TextureImageFormat.Depth32f => WGPUTextureFormat.Depth32Float,
        TextureImageFormat.Depth24Stencil8 => WGPUTextureFormat.Depth24PlusStencil8,
        TextureImageFormat.Etc2Rgb8 => WGPUTextureFormat.ETC2RGB8Unorm,
        TextureImageFormat.Etc2Rgba8 => WGPUTextureFormat.ETC2RGBA8Unorm,
        TextureImageFormat.Astc4x4 => WGPUTextureFormat.ASTC4x4Unorm,
        TextureImageFormat.Etc2Rgb8Srgb => WGPUTextureFormat.ETC2RGB8UnormSrgb,
        TextureImageFormat.Etc2Rgba8Srgb => WGPUTextureFormat.ETC2RGBA8UnormSrgb,
        TextureImageFormat.Astc4x4Srgb => WGPUTextureFormat.ASTC4x4UnormSrgb,
        _ when IsThreeComponent(format) => throw new NotSupportedException(
            $"WebGPU has no 3-component texture format for {format}."),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static WGPUAddressMode ToAddressMode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.Repeat => WGPUAddressMode.Repeat,
        TextureWrap.MirroredRepeat => WGPUAddressMode.MirrorRepeat,
        TextureWrap.ClampToBorder => WGPUAddressMode.ClampToEdge,
        _ => WGPUAddressMode.ClampToEdge,
    };

    public static WGPUFilterMode ToMagFilter(TextureMag mag) =>
        mag == TextureMag.Linear ? WGPUFilterMode.Linear : WGPUFilterMode.Nearest;

    public static WGPUFilterMode ToMinFilter(TextureMin min) => min switch
    {
        TextureMin.Linear or TextureMin.LinearMipmapNearest or TextureMin.LinearMipmapLinear => WGPUFilterMode.Linear,
        _ => WGPUFilterMode.Nearest,
    };

    public static WGPUMipmapFilterMode ToMipFilter(TextureMin min) => min switch
    {
        TextureMin.NearestMipmapLinear or TextureMin.LinearMipmapLinear => WGPUMipmapFilterMode.Linear,
        _ => WGPUMipmapFilterMode.Nearest,
    };

    public static WGPUPrimitiveTopology ToTopology(Topology topology) => topology switch
    {
        Topology.Points => WGPUPrimitiveTopology.PointList,
        Topology.Lines => WGPUPrimitiveTopology.LineList,
        Topology.LineStrip => WGPUPrimitiveTopology.LineStrip,
        Topology.Triangles => WGPUPrimitiveTopology.TriangleList,
        Topology.TriangleStrip => WGPUPrimitiveTopology.TriangleStrip,
        Topology.LineLoop => WGPUPrimitiveTopology.LineStrip,
        Topology.TriangleFan => WGPUPrimitiveTopology.TriangleList,
        Topology.Quads => WGPUPrimitiveTopology.TriangleList,
        _ => WGPUPrimitiveTopology.TriangleList,
    };

    public static bool IsStripTopology(Topology topology) =>
        topology is Topology.LineStrip or Topology.TriangleStrip or Topology.LineLoop;

    public static WGPUCullMode ToCullMode(RasterizerState.PolyFace face) => face switch
    {
        RasterizerState.PolyFace.None => WGPUCullMode.None,
        RasterizerState.PolyFace.Front => WGPUCullMode.Front,
        RasterizerState.PolyFace.Back => WGPUCullMode.Back,
        // WebGPU has no front-and-back cull. Back matches the engine default.
        RasterizerState.PolyFace.FrontAndBack => WGPUCullMode.Back,
        _ => WGPUCullMode.Back,
    };

    public static WGPUFrontFace ToFrontFace(RasterizerState.WindingOrder winding) =>
        winding == RasterizerState.WindingOrder.CW ? WGPUFrontFace.CW : WGPUFrontFace.CCW;

    public static WGPUCompareFunction ToCompare(RasterizerState.DepthMode mode) => mode switch
    {
        RasterizerState.DepthMode.Never => WGPUCompareFunction.Never,
        RasterizerState.DepthMode.Less => WGPUCompareFunction.Less,
        RasterizerState.DepthMode.Equal => WGPUCompareFunction.Equal,
        RasterizerState.DepthMode.Lequal => WGPUCompareFunction.LessEqual,
        RasterizerState.DepthMode.Greater => WGPUCompareFunction.Greater,
        RasterizerState.DepthMode.Notequal => WGPUCompareFunction.NotEqual,
        RasterizerState.DepthMode.Gequal => WGPUCompareFunction.GreaterEqual,
        RasterizerState.DepthMode.Always => WGPUCompareFunction.Always,
        _ => WGPUCompareFunction.LessEqual,
    };

    public static WGPUCompareFunction ToCompare(RasterizerState.StencilFunction function) => function switch
    {
        RasterizerState.StencilFunction.Never => WGPUCompareFunction.Never,
        RasterizerState.StencilFunction.Less => WGPUCompareFunction.Less,
        RasterizerState.StencilFunction.Equal => WGPUCompareFunction.Equal,
        RasterizerState.StencilFunction.Lequal => WGPUCompareFunction.LessEqual,
        RasterizerState.StencilFunction.Greater => WGPUCompareFunction.Greater,
        RasterizerState.StencilFunction.Notequal => WGPUCompareFunction.NotEqual,
        RasterizerState.StencilFunction.Gequal => WGPUCompareFunction.GreaterEqual,
        RasterizerState.StencilFunction.Always => WGPUCompareFunction.Always,
        _ => WGPUCompareFunction.Always,
    };

    public static WGPUStencilOperation ToStencilOp(RasterizerState.StencilOp operation) => operation switch
    {
        RasterizerState.StencilOp.Keep => WGPUStencilOperation.Keep,
        RasterizerState.StencilOp.Zero => WGPUStencilOperation.Zero,
        RasterizerState.StencilOp.Replace => WGPUStencilOperation.Replace,
        RasterizerState.StencilOp.Incr => WGPUStencilOperation.IncrementClamp,
        RasterizerState.StencilOp.IncrWrap => WGPUStencilOperation.IncrementWrap,
        RasterizerState.StencilOp.Decr => WGPUStencilOperation.DecrementClamp,
        RasterizerState.StencilOp.DecrWrap => WGPUStencilOperation.DecrementWrap,
        RasterizerState.StencilOp.Invert => WGPUStencilOperation.Invert,
        _ => WGPUStencilOperation.Keep,
    };

    public static WGPUBlendFactor ToBlendFactor(RasterizerState.Blending blending) => blending switch
    {
        RasterizerState.Blending.Zero => WGPUBlendFactor.Zero,
        RasterizerState.Blending.One => WGPUBlendFactor.One,
        RasterizerState.Blending.SrcColor => WGPUBlendFactor.Src,
        RasterizerState.Blending.OneMinusSrcColor => WGPUBlendFactor.OneMinusSrc,
        RasterizerState.Blending.DstColor => WGPUBlendFactor.Dst,
        RasterizerState.Blending.OneMinusDstColor => WGPUBlendFactor.OneMinusDst,
        RasterizerState.Blending.SrcAlpha => WGPUBlendFactor.SrcAlpha,
        RasterizerState.Blending.OneMinusSrcAlpha => WGPUBlendFactor.OneMinusSrcAlpha,
        RasterizerState.Blending.DstAlpha => WGPUBlendFactor.DstAlpha,
        RasterizerState.Blending.OneMinusDstAlpha => WGPUBlendFactor.OneMinusDstAlpha,
        RasterizerState.Blending.ConstantColor or RasterizerState.Blending.ConstantAlpha => WGPUBlendFactor.Constant,
        RasterizerState.Blending.OneMinusConstantColor or RasterizerState.Blending.OneMinusConstantAlpha =>
            WGPUBlendFactor.OneMinusConstant,
        RasterizerState.Blending.SrcAlphaSaturate => WGPUBlendFactor.SrcAlphaSaturated,
        RasterizerState.Blending.Src1Color => WGPUBlendFactor.Src1,
        RasterizerState.Blending.OneMinusSrc1Color => WGPUBlendFactor.OneMinusSrc1,
        RasterizerState.Blending.Src1Alpha => WGPUBlendFactor.Src1Alpha,
        RasterizerState.Blending.OneMinusSrc1Alpha => WGPUBlendFactor.OneMinusSrc1Alpha,
        _ => WGPUBlendFactor.One,
    };

    public static WGPUBlendOperation ToBlendOp(RasterizerState.BlendMode mode) => mode switch
    {
        RasterizerState.BlendMode.Add => WGPUBlendOperation.Add,
        RasterizerState.BlendMode.Subtract => WGPUBlendOperation.Subtract,
        RasterizerState.BlendMode.ReverseSubtract => WGPUBlendOperation.ReverseSubtract,
        RasterizerState.BlendMode.Min => WGPUBlendOperation.Min,
        RasterizerState.BlendMode.Max => WGPUBlendOperation.Max,
        _ => WGPUBlendOperation.Add,
    };

    public static WGPUVertexFormat ToVertexFormat(VertexFormat.VertexType type, byte count, bool normalized) =>
        (type, count, normalized) switch
        {
            (VertexFormat.VertexType.Float, 1, _) => WGPUVertexFormat.Float32,
            (VertexFormat.VertexType.Float, 2, _) => WGPUVertexFormat.Float32x2,
            (VertexFormat.VertexType.Float, 3, _) => WGPUVertexFormat.Float32x3,
            (VertexFormat.VertexType.Float, 4, _) => WGPUVertexFormat.Float32x4,
            (VertexFormat.VertexType.Byte, 4, true) => WGPUVertexFormat.Snorm8x4,
            (VertexFormat.VertexType.Byte, 4, false) => WGPUVertexFormat.Sint8x4,
            (VertexFormat.VertexType.UnsignedByte, 4, true) => WGPUVertexFormat.Unorm8x4,
            (VertexFormat.VertexType.UnsignedByte, 4, false) => WGPUVertexFormat.Uint8x4,
            (VertexFormat.VertexType.Short, 2, false) => WGPUVertexFormat.Sint16x2,
            (VertexFormat.VertexType.Short, 4, true) => WGPUVertexFormat.Snorm16x4,
            (VertexFormat.VertexType.Short, 4, false) => WGPUVertexFormat.Sint16x4,
            (VertexFormat.VertexType.Int, 1, _) => WGPUVertexFormat.Sint32,
            (VertexFormat.VertexType.Int, 2, _) => WGPUVertexFormat.Sint32x2,
            (VertexFormat.VertexType.Int, 4, _) => WGPUVertexFormat.Sint32x4,
            _ => WGPUVertexFormat.Float32x4,
        };

    public static uint Align(uint value, uint alignment) => (value + alignment - 1u) / alignment * alignment;

    public static ulong Align(ulong value, ulong alignment) => (value + alignment - 1ul) / alignment * alignment;

    public static string FindWgslEntryPoint(string wgsl, string stageAttribute)
    {
        ArgumentException.ThrowIfNullOrEmpty(wgsl);
        ArgumentException.ThrowIfNullOrEmpty(stageAttribute);

        string marker = "@" + stageAttribute;
        int search = 0;
        while (search < wgsl.Length)
        {
            int at = wgsl.IndexOf(marker, search, StringComparison.Ordinal);
            if (at < 0)
                break;

            int afterMarker = at + marker.Length;
            if (afterMarker < wgsl.Length && IsIdentifierChar(wgsl[afterMarker]))
            {
                search = afterMarker;
                continue;
            }

            int fn = wgsl.IndexOf("fn", afterMarker, StringComparison.Ordinal);
            if (fn < 0)
                break;
            int nameStart = fn + 2;
            while (nameStart < wgsl.Length && char.IsWhiteSpace(wgsl[nameStart]))
                nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < wgsl.Length && IsIdentifierChar(wgsl[nameEnd]))
                nameEnd++;
            if (nameEnd > nameStart)
                return wgsl[nameStart..nameEnd];
            search = nameEnd;
        }

        throw new InvalidOperationException($"WGSL is missing an @{stageAttribute} entry point.");
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';
}
