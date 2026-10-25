// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.Vulkan;

namespace XEngine.Runtime.Backends.Vulkan;

/// <summary>Maps engine enums to Vulkan format / state enums.</summary>
internal static class VulkanFormats
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
        _ => throw new NotSupportedException($"Vulkan initial upload for {format} is not supported."),
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

    public static Format ToVkFormat(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => Format.R8G8B8A8Unorm,
        TextureImageFormat.Color4bSrgb => Format.R8G8B8A8Srgb,
        TextureImageFormat.Byte => Format.R8Uint,
        TextureImageFormat.Float => Format.R32Sfloat,
        TextureImageFormat.Float2 => Format.R32G32Sfloat,
        TextureImageFormat.Float3 => Format.R32G32B32Sfloat,
        TextureImageFormat.Float4 => Format.R32G32B32A32Sfloat,
        TextureImageFormat.Short => Format.R16Sfloat,
        TextureImageFormat.Short2 => Format.R16G16Sfloat,
        TextureImageFormat.Short3 => Format.R16G16B16Sfloat,
        TextureImageFormat.Short4 => Format.R16G16B16A16Sfloat,
        TextureImageFormat.Int => Format.R32Sint,
        TextureImageFormat.Int2 => Format.R32G32Sint,
        TextureImageFormat.Int3 => Format.R32G32B32Sint,
        TextureImageFormat.Int4 => Format.R32G32B32A32Sint,
        TextureImageFormat.UnsignedShort => Format.R16Uint,
        TextureImageFormat.UnsignedShort2 => Format.R16G16Uint,
        TextureImageFormat.UnsignedShort3 => Format.R16G16B16Uint,
        TextureImageFormat.UnsignedShort4 => Format.R16G16B16A16Uint,
        TextureImageFormat.UnsignedInt => Format.R32Uint,
        TextureImageFormat.UnsignedInt2 => Format.R32G32Uint,
        TextureImageFormat.UnsignedInt3 => Format.R32G32Uint,
        TextureImageFormat.UnsignedInt4 => Format.R32G32B32A32Uint,
        // Metal has no 24-bit depth formats: through MoltenVK, X8_D24 and D24S8 translate to
        // MTLPixelFormatInvalid and trip a native assertion on texture creation. Remap to the
        // 32-bit Metal-native equivalents (macOS MTLPixelFormatDepth32Float[_Stencil8]).
        TextureImageFormat.Depth16f => Format.D16Unorm,
        TextureImageFormat.Depth24f => OperatingSystem.IsMacOS() ? Format.D32Sfloat : Format.X8D24UnormPack32,
        TextureImageFormat.Depth32f => Format.D32Sfloat,
        TextureImageFormat.Depth24Stencil8 => OperatingSystem.IsMacOS() ? Format.D32SfloatS8Uint : Format.D24UnormS8Uint,
        TextureImageFormat.Etc2Rgb8 => Format.Etc2R8G8B8UnormBlock,
        TextureImageFormat.Etc2Rgba8 => Format.Etc2R8G8B8A8UnormBlock,
        TextureImageFormat.Astc4x4 => Format.Astc4x4UnormBlock,
        TextureImageFormat.Etc2Rgb8Srgb => Format.Etc2R8G8B8SrgbBlock,
        TextureImageFormat.Etc2Rgba8Srgb => Format.Etc2R8G8B8A8SrgbBlock,
        TextureImageFormat.Astc4x4Srgb => Format.Astc4x4SrgbBlock,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static ImageAspectFlags AspectFor(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Depth16f or TextureImageFormat.Depth24f or TextureImageFormat.Depth32f
            => ImageAspectFlags.DepthBit,
        TextureImageFormat.Depth24Stencil8
            => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
        _ => ImageAspectFlags.ColorBit,
    };

    /// <summary>
    /// Map an engine sample count (1/2/4/8) to its <see cref="SampleCountFlags"/> bit. The Vulkan
    /// flag value equals the sample count itself, so the mapping is a straight cast — this wrapper
    /// exists to validate the input and to document that invariant at every call site.
    /// </summary>
    public static SampleCountFlags ToSampleCount(uint samples)
    {
        if (samples < 1 || (samples & (samples - 1)) != 0 || samples > 64)
            throw new ArgumentOutOfRangeException(nameof(samples), samples, "Vulkan sample count must be a power of two between 1 and 64.");
        return (SampleCountFlags)samples;
    }

    public static bool IsDepth(TextureImageFormat format) =>
        format is TextureImageFormat.Depth16f
            or TextureImageFormat.Depth24f
            or TextureImageFormat.Depth32f
            or TextureImageFormat.Depth24Stencil8;

    public static bool HasStencil(Format format) =>
        format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint;

    public static PrimitiveTopology ToTopology(Topology topology) => topology switch
    {
        Topology.Points => PrimitiveTopology.PointList,
        Topology.Lines => PrimitiveTopology.LineList,
        Topology.LineStrip => PrimitiveTopology.LineStrip,
        Topology.Triangles => PrimitiveTopology.TriangleList,
        Topology.TriangleStrip => PrimitiveTopology.TriangleStrip,
        // Legacy GL topologies — approximate; full emulation is a known gap.
        Topology.LineLoop => PrimitiveTopology.LineStrip,
        Topology.TriangleFan => PrimitiveTopology.TriangleList,
        Topology.Quads => PrimitiveTopology.TriangleList,
        _ => PrimitiveTopology.TriangleList,
    };

    public static Format ToVertexFormat(VertexFormat.VertexType type, byte count, bool normalized)
    {
        return (type, count, normalized) switch
        {
            (VertexFormat.VertexType.Float, 1, _) => Format.R32Sfloat,
            (VertexFormat.VertexType.Float, 2, _) => Format.R32G32Sfloat,
            (VertexFormat.VertexType.Float, 3, _) => Format.R32G32B32Sfloat,
            (VertexFormat.VertexType.Float, 4, _) => Format.R32G32B32A32Sfloat,
            (VertexFormat.VertexType.Byte, 4, true) => Format.R8G8B8A8SNorm,
            (VertexFormat.VertexType.Byte, 4, false) => Format.R8G8B8A8Sint,
            (VertexFormat.VertexType.UnsignedByte, 4, true) => Format.R8G8B8A8Unorm,
            (VertexFormat.VertexType.UnsignedByte, 4, false) => Format.R8G8B8A8Uint,
            (VertexFormat.VertexType.Short, 2, false) => Format.R16G16Sint,
            (VertexFormat.VertexType.Short, 4, true) => Format.R16G16B16A16SNorm,
            (VertexFormat.VertexType.Short, 4, false) => Format.R16G16B16A16Sint,
            (VertexFormat.VertexType.Int, 1, _) => Format.R32Sint,
            (VertexFormat.VertexType.Int, 2, _) => Format.R32G32Sint,
            (VertexFormat.VertexType.Int, 4, _) => Format.R32G32B32A32Sint,
            _ => Format.R32G32B32A32Sfloat,
        };
    }

    public static CompareOp ToCompareOp(RasterizerState.DepthMode mode) => mode switch
    {
        RasterizerState.DepthMode.Never => CompareOp.Never,
        RasterizerState.DepthMode.Less => CompareOp.Less,
        RasterizerState.DepthMode.Equal => CompareOp.Equal,
        RasterizerState.DepthMode.Lequal => CompareOp.LessOrEqual,
        RasterizerState.DepthMode.Greater => CompareOp.Greater,
        RasterizerState.DepthMode.Notequal => CompareOp.NotEqual,
        RasterizerState.DepthMode.Gequal => CompareOp.GreaterOrEqual,
        RasterizerState.DepthMode.Always => CompareOp.Always,
        _ => CompareOp.LessOrEqual,
    };

    public static CompareOp ToCompareOp(RasterizerState.StencilFunction function) => function switch
    {
        RasterizerState.StencilFunction.Never => CompareOp.Never,
        RasterizerState.StencilFunction.Less => CompareOp.Less,
        RasterizerState.StencilFunction.Equal => CompareOp.Equal,
        RasterizerState.StencilFunction.Lequal => CompareOp.LessOrEqual,
        RasterizerState.StencilFunction.Greater => CompareOp.Greater,
        RasterizerState.StencilFunction.Notequal => CompareOp.NotEqual,
        RasterizerState.StencilFunction.Gequal => CompareOp.GreaterOrEqual,
        RasterizerState.StencilFunction.Always => CompareOp.Always,
        _ => CompareOp.Always,
    };

    public static StencilOp ToStencilOp(RasterizerState.StencilOp operation) => operation switch
    {
        RasterizerState.StencilOp.Keep => StencilOp.Keep,
        RasterizerState.StencilOp.Zero => StencilOp.Zero,
        RasterizerState.StencilOp.Replace => StencilOp.Replace,
        RasterizerState.StencilOp.Incr => StencilOp.IncrementAndClamp,
        RasterizerState.StencilOp.IncrWrap => StencilOp.IncrementAndWrap,
        RasterizerState.StencilOp.Decr => StencilOp.DecrementAndClamp,
        RasterizerState.StencilOp.DecrWrap => StencilOp.DecrementAndWrap,
        RasterizerState.StencilOp.Invert => StencilOp.Invert,
        _ => StencilOp.Keep,
    };

    public static CullModeFlags ToCullMode(RasterizerState.PolyFace face) => face switch
    {
        RasterizerState.PolyFace.None => CullModeFlags.None,
        RasterizerState.PolyFace.Front => CullModeFlags.FrontBit,
        RasterizerState.PolyFace.Back => CullModeFlags.BackBit,
        RasterizerState.PolyFace.FrontAndBack => CullModeFlags.FrontAndBack,
        _ => CullModeFlags.BackBit,
    };

    public static FrontFace ToFrontFace(
        RasterizerState.WindingOrder winding,
        bool usesPositiveViewportHeight = false)
    {
        FrontFace frontFace = winding == RasterizerState.WindingOrder.CW
            ? FrontFace.Clockwise
            : FrontFace.CounterClockwise;

        // Vulkan determines winding after applying the viewport transform. A positive viewport
        // height reverses the winding used by XEngine's normal negative-height convention.
        return usesPositiveViewportHeight
            ? frontFace == FrontFace.Clockwise ? FrontFace.CounterClockwise : FrontFace.Clockwise
            : frontFace;
    }

    public static BlendFactor ToBlendFactor(RasterizerState.Blending b) => b switch
    {
        RasterizerState.Blending.Zero => BlendFactor.Zero,
        RasterizerState.Blending.One => BlendFactor.One,
        RasterizerState.Blending.SrcColor => BlendFactor.SrcColor,
        RasterizerState.Blending.OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
        RasterizerState.Blending.DstColor => BlendFactor.DstColor,
        RasterizerState.Blending.OneMinusDstColor => BlendFactor.OneMinusDstColor,
        RasterizerState.Blending.SrcAlpha => BlendFactor.SrcAlpha,
        RasterizerState.Blending.OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
        RasterizerState.Blending.DstAlpha => BlendFactor.DstAlpha,
        RasterizerState.Blending.OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
        RasterizerState.Blending.ConstantColor => BlendFactor.ConstantColor,
        RasterizerState.Blending.OneMinusConstantColor => BlendFactor.OneMinusConstantColor,
        RasterizerState.Blending.ConstantAlpha => BlendFactor.ConstantAlpha,
        RasterizerState.Blending.OneMinusConstantAlpha => BlendFactor.OneMinusConstantAlpha,
        RasterizerState.Blending.SrcAlphaSaturate => BlendFactor.SrcAlphaSaturate,
        RasterizerState.Blending.Src1Color => BlendFactor.Src1Color,
        RasterizerState.Blending.OneMinusSrc1Color => BlendFactor.OneMinusSrc1Color,
        RasterizerState.Blending.Src1Alpha => BlendFactor.Src1Alpha,
        RasterizerState.Blending.OneMinusSrc1Alpha => BlendFactor.OneMinusSrc1Alpha,
        _ => BlendFactor.One,
    };

    public static BlendOp ToBlendOp(RasterizerState.BlendMode mode) => mode switch
    {
        RasterizerState.BlendMode.Add => BlendOp.Add,
        RasterizerState.BlendMode.Subtract => BlendOp.Subtract,
        RasterizerState.BlendMode.ReverseSubtract => BlendOp.ReverseSubtract,
        RasterizerState.BlendMode.Min => BlendOp.Min,
        RasterizerState.BlendMode.Max => BlendOp.Max,
        _ => BlendOp.Add,
    };

    public static Filter ToFilter(TextureMin min) => min switch
    {
        TextureMin.Nearest or TextureMin.NearestMipmapNearest or TextureMin.NearestMipmapLinear => Filter.Nearest,
        _ => Filter.Linear,
    };

    public static Filter ToFilter(TextureMag mag) =>
        mag == TextureMag.Nearest ? Filter.Nearest : Filter.Linear;

    public static SamplerAddressMode ToAddressMode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.Repeat => SamplerAddressMode.Repeat,
        TextureWrap.MirroredRepeat => SamplerAddressMode.MirroredRepeat,
        TextureWrap.ClampToEdge => SamplerAddressMode.ClampToEdge,
        TextureWrap.ClampToBorder => SamplerAddressMode.ClampToBorder,
        _ => SamplerAddressMode.Repeat,
    };
}
