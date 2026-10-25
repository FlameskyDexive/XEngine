// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.Metal.Native;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// Maps engine enums onto Metal.framework pixel formats and raster state.
/// Metal has no 3-component texture formats; those engine formats throw.
/// Depth24 maps to Depth32 on purpose: iOS has no D24, so the table is portable.
/// </summary>
internal static class MetalFormats
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
        _ => throw new NotSupportedException($"Metal initial upload for {format} is not supported."),
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

    public static bool IsDepth(TextureImageFormat format) => format is
        TextureImageFormat.Depth16f
            or TextureImageFormat.Depth24f
            or TextureImageFormat.Depth32f
            or TextureImageFormat.Depth24Stencil8;

    /// <summary>
    /// Depth/stencil textures must use private storage on Metal. Shared storage is
    /// retained for color textures because the current upload path writes through
    /// <c>replaceRegion</c> and the player also reads those resources on the CPU.
    /// </summary>
    public static MTLStorageMode ToStorageMode(TextureImageFormat format) =>
        IsDepth(format) ? MTLStorageMode.Private : MTLStorageMode.Shared;

    public static bool HasStencil(MTLPixelFormat format) =>
        format is MTLPixelFormat.Depth24Unorm_Stencil8 or MTLPixelFormat.Depth32Float_Stencil8;

    public static bool IsThreeComponent(TextureImageFormat format) => format is
        TextureImageFormat.Short3
        or TextureImageFormat.Float3
        or TextureImageFormat.Int3
        or TextureImageFormat.UnsignedShort3
        or TextureImageFormat.UnsignedInt3;

    public static MTLPixelFormat ToPixelFormat(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => MTLPixelFormat.RGBA8Unorm,
        TextureImageFormat.Color4bSrgb => MTLPixelFormat.RGBA8UnormSrgb,
        TextureImageFormat.Byte => MTLPixelFormat.R8Unorm,
        TextureImageFormat.Float => MTLPixelFormat.R32Float,
        TextureImageFormat.Float2 => MTLPixelFormat.RG32Float,
        TextureImageFormat.Float4 => MTLPixelFormat.RGBA32Float,
        TextureImageFormat.Short => MTLPixelFormat.R16Float,
        TextureImageFormat.Short2 => MTLPixelFormat.RG16Float,
        TextureImageFormat.Short4 => MTLPixelFormat.RGBA16Float,
        TextureImageFormat.Int => MTLPixelFormat.R32Sint,
        TextureImageFormat.Int2 => MTLPixelFormat.RG32Sint,
        TextureImageFormat.Int4 => MTLPixelFormat.RGBA32Sint,
        TextureImageFormat.UnsignedShort => MTLPixelFormat.R16Uint,
        TextureImageFormat.UnsignedShort2 => MTLPixelFormat.RG16Uint,
        TextureImageFormat.UnsignedShort4 => MTLPixelFormat.RGBA16Uint,
        TextureImageFormat.UnsignedInt => MTLPixelFormat.R32Uint,
        TextureImageFormat.UnsignedInt2 => MTLPixelFormat.RG32Uint,
        TextureImageFormat.UnsignedInt4 => MTLPixelFormat.RGBA32Uint,
        TextureImageFormat.Depth16f => MTLPixelFormat.Depth16Unorm,
        // iOS has no D24. Portable mapping: 32-bit depth, packed depth+stencil as Depth32Float_Stencil8.
        TextureImageFormat.Depth24f => MTLPixelFormat.Depth32Float,
        TextureImageFormat.Depth32f => MTLPixelFormat.Depth32Float,
        TextureImageFormat.Depth24Stencil8 => MTLPixelFormat.Depth32Float_Stencil8,
        TextureImageFormat.Etc2Rgb8 => MTLPixelFormat.ETC2_RGB8,
        TextureImageFormat.Etc2Rgba8 => MTLPixelFormat.EAC_RGBA8,
        TextureImageFormat.Astc4x4 => MTLPixelFormat.ASTC_4x4_LDR,
        TextureImageFormat.Etc2Rgb8Srgb => MTLPixelFormat.ETC2_RGB8_sRGB,
        TextureImageFormat.Etc2Rgba8Srgb => MTLPixelFormat.EAC_RGBA8_sRGB,
        TextureImageFormat.Astc4x4Srgb => MTLPixelFormat.ASTC_4x4_sRGB,
        _ when IsThreeComponent(format) => throw new NotSupportedException(
            $"Metal has no 3-component texture format for {format}."),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static MTLSamplerAddressMode ToAddressMode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.Repeat => MTLSamplerAddressMode.Repeat,
        TextureWrap.MirroredRepeat => MTLSamplerAddressMode.MirrorRepeat,
        TextureWrap.ClampToBorder => MTLSamplerAddressMode.ClampToEdge,
        _ => MTLSamplerAddressMode.ClampToEdge,
    };

    public static MTLSamplerMinMagFilter ToMinMag(TextureMin min) => min is TextureMin.Nearest
        or TextureMin.NearestMipmapNearest or TextureMin.NearestMipmapLinear
        ? MTLSamplerMinMagFilter.Nearest
        : MTLSamplerMinMagFilter.Linear;

    public static MTLSamplerMinMagFilter ToMinMag(TextureMag mag) =>
        mag == TextureMag.Nearest ? MTLSamplerMinMagFilter.Nearest : MTLSamplerMinMagFilter.Linear;

    public static MTLSamplerMipFilter ToMipFilter(TextureMin min) => min switch
    {
        TextureMin.Nearest or TextureMin.Linear => MTLSamplerMipFilter.NotMipmapped,
        TextureMin.NearestMipmapNearest or TextureMin.LinearMipmapNearest => MTLSamplerMipFilter.Nearest,
        _ => MTLSamplerMipFilter.Linear,
    };

    public static MTLPrimitiveType ToPrimitiveType(Topology topology) => topology switch
    {
        Topology.Points => MTLPrimitiveType.Point,
        Topology.Lines => MTLPrimitiveType.Line,
        Topology.LineStrip or Topology.LineLoop => MTLPrimitiveType.LineStrip,
        Topology.TriangleStrip => MTLPrimitiveType.TriangleStrip,
        _ => MTLPrimitiveType.Triangle,
    };

    public static bool IsStripTopology(Topology topology) =>
        topology is Topology.LineStrip or Topology.TriangleStrip or Topology.LineLoop;

    public static MTLCullMode ToCullMode(RasterizerState.PolyFace face) => face switch
    {
        RasterizerState.PolyFace.None => MTLCullMode.None,
        RasterizerState.PolyFace.Front => MTLCullMode.Front,
        RasterizerState.PolyFace.Back => MTLCullMode.Back,
        RasterizerState.PolyFace.FrontAndBack => MTLCullMode.Back,
        _ => MTLCullMode.Back,
    };

    public static MTLWinding ToWinding(RasterizerState.WindingOrder winding) =>
        winding == RasterizerState.WindingOrder.CW ? MTLWinding.Clockwise : MTLWinding.CounterClockwise;

    public static MTLCompareFunction ToCompare(RasterizerState.DepthMode mode) => mode switch
    {
        RasterizerState.DepthMode.Never => MTLCompareFunction.Never,
        RasterizerState.DepthMode.Less => MTLCompareFunction.Less,
        RasterizerState.DepthMode.Equal => MTLCompareFunction.Equal,
        RasterizerState.DepthMode.Lequal => MTLCompareFunction.LessEqual,
        RasterizerState.DepthMode.Greater => MTLCompareFunction.Greater,
        RasterizerState.DepthMode.Notequal => MTLCompareFunction.NotEqual,
        RasterizerState.DepthMode.Gequal => MTLCompareFunction.GreaterEqual,
        RasterizerState.DepthMode.Always => MTLCompareFunction.Always,
        _ => MTLCompareFunction.LessEqual,
    };

    public static MTLCompareFunction ToCompare(RasterizerState.StencilFunction function) => function switch
    {
        RasterizerState.StencilFunction.Never => MTLCompareFunction.Never,
        RasterizerState.StencilFunction.Less => MTLCompareFunction.Less,
        RasterizerState.StencilFunction.Equal => MTLCompareFunction.Equal,
        RasterizerState.StencilFunction.Lequal => MTLCompareFunction.LessEqual,
        RasterizerState.StencilFunction.Greater => MTLCompareFunction.Greater,
        RasterizerState.StencilFunction.Notequal => MTLCompareFunction.NotEqual,
        RasterizerState.StencilFunction.Gequal => MTLCompareFunction.GreaterEqual,
        RasterizerState.StencilFunction.Always => MTLCompareFunction.Always,
        _ => MTLCompareFunction.Always,
    };

    public static MTLStencilOperation ToStencilOp(RasterizerState.StencilOp operation) => operation switch
    {
        RasterizerState.StencilOp.Keep => MTLStencilOperation.Keep,
        RasterizerState.StencilOp.Zero => MTLStencilOperation.Zero,
        RasterizerState.StencilOp.Replace => MTLStencilOperation.Replace,
        RasterizerState.StencilOp.Incr => MTLStencilOperation.IncrementClamp,
        RasterizerState.StencilOp.IncrWrap => MTLStencilOperation.IncrementWrap,
        RasterizerState.StencilOp.Decr => MTLStencilOperation.DecrementClamp,
        RasterizerState.StencilOp.DecrWrap => MTLStencilOperation.DecrementWrap,
        RasterizerState.StencilOp.Invert => MTLStencilOperation.Invert,
        _ => MTLStencilOperation.Keep,
    };

    public static MTLBlendFactor ToBlendFactor(RasterizerState.Blending blending) => blending switch
    {
        RasterizerState.Blending.Zero => MTLBlendFactor.Zero,
        RasterizerState.Blending.One => MTLBlendFactor.One,
        RasterizerState.Blending.SrcColor => MTLBlendFactor.SourceColor,
        RasterizerState.Blending.OneMinusSrcColor => MTLBlendFactor.OneMinusSourceColor,
        RasterizerState.Blending.DstColor => MTLBlendFactor.DestinationColor,
        RasterizerState.Blending.OneMinusDstColor => MTLBlendFactor.OneMinusDestinationColor,
        RasterizerState.Blending.SrcAlpha => MTLBlendFactor.SourceAlpha,
        RasterizerState.Blending.OneMinusSrcAlpha => MTLBlendFactor.OneMinusSourceAlpha,
        RasterizerState.Blending.DstAlpha => MTLBlendFactor.DestinationAlpha,
        RasterizerState.Blending.OneMinusDstAlpha => MTLBlendFactor.OneMinusDestinationAlpha,
        RasterizerState.Blending.ConstantColor or RasterizerState.Blending.ConstantAlpha => MTLBlendFactor.BlendColor,
        RasterizerState.Blending.OneMinusConstantColor or RasterizerState.Blending.OneMinusConstantAlpha =>
            MTLBlendFactor.OneMinusBlendColor,
        RasterizerState.Blending.SrcAlphaSaturate => MTLBlendFactor.SourceAlphaSaturated,
        RasterizerState.Blending.Src1Color => MTLBlendFactor.Source1Color,
        RasterizerState.Blending.OneMinusSrc1Color => MTLBlendFactor.OneMinusSource1Color,
        RasterizerState.Blending.Src1Alpha => MTLBlendFactor.Source1Alpha,
        RasterizerState.Blending.OneMinusSrc1Alpha => MTLBlendFactor.OneMinusSource1Alpha,
        _ => MTLBlendFactor.One,
    };

    public static MTLBlendOperation ToBlendOp(RasterizerState.BlendMode mode) => mode switch
    {
        RasterizerState.BlendMode.Add => MTLBlendOperation.Add,
        RasterizerState.BlendMode.Subtract => MTLBlendOperation.Subtract,
        RasterizerState.BlendMode.ReverseSubtract => MTLBlendOperation.ReverseSubtract,
        RasterizerState.BlendMode.Min => MTLBlendOperation.Min,
        RasterizerState.BlendMode.Max => MTLBlendOperation.Max,
        _ => MTLBlendOperation.Add,
    };

    public static MTLVertexFormat ToVertexFormat(VertexFormat.VertexType type, byte count, bool normalized) =>
        (type, count, normalized) switch
        {
            (VertexFormat.VertexType.Float, 1, _) => MTLVertexFormat.Float,
            (VertexFormat.VertexType.Float, 2, _) => MTLVertexFormat.Float2,
            (VertexFormat.VertexType.Float, 3, _) => MTLVertexFormat.Float3,
            (VertexFormat.VertexType.Float, 4, _) => MTLVertexFormat.Float4,
            (VertexFormat.VertexType.Byte, 4, true) => MTLVertexFormat.Char4Normalized,
            (VertexFormat.VertexType.Byte, 4, false) => MTLVertexFormat.Char4,
            (VertexFormat.VertexType.UnsignedByte, 4, true) => MTLVertexFormat.UChar4Normalized,
            (VertexFormat.VertexType.UnsignedByte, 4, false) => MTLVertexFormat.UChar4,
            (VertexFormat.VertexType.Short, 2, false) => MTLVertexFormat.Short2,
            (VertexFormat.VertexType.Short, 4, true) => MTLVertexFormat.Short4Normalized,
            (VertexFormat.VertexType.Short, 4, false) => MTLVertexFormat.Short4,
            (VertexFormat.VertexType.Int, 1, _) => MTLVertexFormat.Int,
            (VertexFormat.VertexType.Int, 2, _) => MTLVertexFormat.Int2,
            (VertexFormat.VertexType.Int, 4, _) => MTLVertexFormat.Int4,
            _ => MTLVertexFormat.Float4,
        };

    public static uint Align(uint value, uint alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;

    public static ulong Align(ulong value, ulong alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
