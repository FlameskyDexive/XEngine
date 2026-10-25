// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace XEngine.Runtime.Backends.D3D12;

/// <summary>Maps engine enums to DXGI / D3D12 format and state enums.</summary>
internal static class D3D12Formats
{
    public static int BytesPerPixel(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b or TextureImageFormat.Color4bSrgb => 4,
        TextureImageFormat.Byte => 1,
        TextureImageFormat.Short => 2,
        TextureImageFormat.Short2 => 4,
        TextureImageFormat.Short4 => 8,
        TextureImageFormat.Float or TextureImageFormat.Int or TextureImageFormat.UnsignedInt => 4,
        TextureImageFormat.Float2 or TextureImageFormat.Int2 or TextureImageFormat.UnsignedInt2 => 8,
        TextureImageFormat.Float3 or TextureImageFormat.Int3 or TextureImageFormat.UnsignedInt3 => 12,
        TextureImageFormat.Float4 or TextureImageFormat.Int4 or TextureImageFormat.UnsignedInt4 => 16,
        TextureImageFormat.UnsignedShort => 2,
        TextureImageFormat.UnsignedShort2 => 4,
        TextureImageFormat.UnsignedShort4 => 8,
        TextureImageFormat.Short3 or TextureImageFormat.UnsignedShort3 =>
            throw new NotSupportedException($"D3D12 tightly packed upload for {format} requires RGB-to-RGBA expansion."),
        _ => throw new NotSupportedException($"D3D12 initial upload for {format} is not supported."),
    };

    public static Format ToDxgiFormat(TextureImageFormat format) => format switch
    {
        TextureImageFormat.Color4b => Format.R8G8B8A8_UNorm,
        TextureImageFormat.Color4bSrgb => Format.R8G8B8A8_UNorm_SRgb,
        TextureImageFormat.Byte => Format.R8_UInt,
        TextureImageFormat.Float => Format.R32_Float,
        TextureImageFormat.Float2 => Format.R32G32_Float,
        TextureImageFormat.Float3 => Format.R32G32B32_Float,
        TextureImageFormat.Float4 => Format.R32G32B32A32_Float,
        TextureImageFormat.Short => Format.R16_Float,
        TextureImageFormat.Short2 => Format.R16G16_Float,
        TextureImageFormat.Short3 => Format.R16G16B16A16_Float, // no RGB16F in DXGI
        TextureImageFormat.Short4 => Format.R16G16B16A16_Float,
        TextureImageFormat.Int => Format.R32_SInt,
        TextureImageFormat.Int2 => Format.R32G32_SInt,
        TextureImageFormat.Int3 => Format.R32G32B32_SInt,
        TextureImageFormat.Int4 => Format.R32G32B32A32_SInt,
        TextureImageFormat.UnsignedShort => Format.R16_UInt,
        TextureImageFormat.UnsignedShort2 => Format.R16G16_UInt,
        TextureImageFormat.UnsignedShort3 => Format.R16G16B16A16_UInt,
        TextureImageFormat.UnsignedShort4 => Format.R16G16B16A16_UInt,
        TextureImageFormat.UnsignedInt => Format.R32_UInt,
        TextureImageFormat.UnsignedInt2 => Format.R32G32_UInt,
        TextureImageFormat.UnsignedInt3 => Format.R32G32B32_UInt,
        TextureImageFormat.UnsignedInt4 => Format.R32G32B32A32_UInt,
        TextureImageFormat.Depth16f => Format.D16_UNorm,
        TextureImageFormat.Depth24f => Format.D24_UNorm_S8_UInt,
        TextureImageFormat.Depth32f => Format.D32_Float,
        TextureImageFormat.Depth24Stencil8 => Format.D24_UNorm_S8_UInt,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static bool IsDepth(TextureImageFormat format) =>
        format is TextureImageFormat.Depth16f
            or TextureImageFormat.Depth24f
            or TextureImageFormat.Depth32f
            or TextureImageFormat.Depth24Stencil8;

    // Maps a depth/stencil format to the typeless/readable DXGI format a shader-resource view must
    // use to sample a depth texture. Depth attachments are created with the typed depth format
    // (D24_UNorm_S8_UInt etc.) because that's what DSVs require, but SRVs cannot use the depth-only
    // formats directly -- they need the R24/R32 typeless equivalents that read the depth channel as
    // a normalized/float red value. Returns Unknown for non-depth formats.
    public static Format ToDxgiShaderResourceFormat(TextureImageFormat format)
    {
        return format switch
        {
            TextureImageFormat.Depth16f => Format.R16_UNorm,
            TextureImageFormat.Depth24f => Format.R24_UNorm_X8_Typeless,
            TextureImageFormat.Depth32f => Format.R32_Float,
            TextureImageFormat.Depth24Stencil8 => Format.R24_UNorm_X8_Typeless,
            _ => ToDxgiFormat(format),
        };
    }

    public static PrimitiveTopologyType ToTopologyType(Topology topology) => topology switch
    {
        Topology.Points => PrimitiveTopologyType.Point,
        Topology.Lines or Topology.LineStrip or Topology.LineLoop => PrimitiveTopologyType.Line,
        _ => PrimitiveTopologyType.Triangle,
    };

    public static PrimitiveTopology ToTopology(Topology topology) => topology switch
    {
        Topology.Points => PrimitiveTopology.PointList,
        Topology.Lines => PrimitiveTopology.LineList,
        Topology.LineStrip => PrimitiveTopology.LineStrip,
        Topology.Triangles => PrimitiveTopology.TriangleList,
        Topology.TriangleStrip => PrimitiveTopology.TriangleStrip,
        Topology.LineLoop => PrimitiveTopology.LineStrip,
        Topology.TriangleFan => PrimitiveTopology.TriangleList,
        Topology.Quads => PrimitiveTopology.TriangleList,
        _ => PrimitiveTopology.TriangleList,
    };

    public static Format ToVertexFormat(VertexFormat.VertexType type, byte count, bool normalized)
    {
        return (type, count, normalized) switch
        {
            (VertexFormat.VertexType.Float, 1, _) => Format.R32_Float,
            (VertexFormat.VertexType.Float, 2, _) => Format.R32G32_Float,
            (VertexFormat.VertexType.Float, 3, _) => Format.R32G32B32_Float,
            (VertexFormat.VertexType.Float, 4, _) => Format.R32G32B32A32_Float,
            (VertexFormat.VertexType.Byte, 4, true) => Format.R8G8B8A8_SNorm,
            (VertexFormat.VertexType.Byte, 4, false) => Format.R8G8B8A8_SInt,
            (VertexFormat.VertexType.UnsignedByte, 4, true) => Format.R8G8B8A8_UNorm,
            (VertexFormat.VertexType.UnsignedByte, 4, false) => Format.R8G8B8A8_UInt,
            (VertexFormat.VertexType.Short, 2, false) => Format.R16G16_SInt,
            (VertexFormat.VertexType.Short, 4, true) => Format.R16G16B16A16_SNorm,
            (VertexFormat.VertexType.Short, 4, false) => Format.R16G16B16A16_SInt,
            (VertexFormat.VertexType.Int, 1, _) => Format.R32_SInt,
            (VertexFormat.VertexType.Int, 2, _) => Format.R32G32_SInt,
            (VertexFormat.VertexType.Int, 4, _) => Format.R32G32B32A32_SInt,
            _ => Format.R32G32B32A32_Float,
        };
    }

    public static ComparisonFunction ToComparison(RasterizerState.DepthMode mode) => mode switch
    {
        RasterizerState.DepthMode.Never => ComparisonFunction.Never,
        RasterizerState.DepthMode.Less => ComparisonFunction.Less,
        RasterizerState.DepthMode.Equal => ComparisonFunction.Equal,
        RasterizerState.DepthMode.Lequal => ComparisonFunction.LessEqual,
        RasterizerState.DepthMode.Greater => ComparisonFunction.Greater,
        RasterizerState.DepthMode.Notequal => ComparisonFunction.NotEqual,
        RasterizerState.DepthMode.Gequal => ComparisonFunction.GreaterEqual,
        RasterizerState.DepthMode.Always => ComparisonFunction.Always,
        _ => ComparisonFunction.LessEqual,
    };

    public static ComparisonFunction ToComparison(RasterizerState.StencilFunction function) => function switch
    {
        RasterizerState.StencilFunction.Never => ComparisonFunction.Never,
        RasterizerState.StencilFunction.Less => ComparisonFunction.Less,
        RasterizerState.StencilFunction.Equal => ComparisonFunction.Equal,
        RasterizerState.StencilFunction.Lequal => ComparisonFunction.LessEqual,
        RasterizerState.StencilFunction.Greater => ComparisonFunction.Greater,
        RasterizerState.StencilFunction.Notequal => ComparisonFunction.NotEqual,
        RasterizerState.StencilFunction.Gequal => ComparisonFunction.GreaterEqual,
        RasterizerState.StencilFunction.Always => ComparisonFunction.Always,
        _ => ComparisonFunction.Always,
    };

    public static StencilOperation ToStencilOperation(RasterizerState.StencilOp operation) => operation switch
    {
        RasterizerState.StencilOp.Keep => StencilOperation.Keep,
        RasterizerState.StencilOp.Zero => StencilOperation.Zero,
        RasterizerState.StencilOp.Replace => StencilOperation.Replace,
        RasterizerState.StencilOp.Incr => StencilOperation.IncrementSaturate,
        RasterizerState.StencilOp.IncrWrap => StencilOperation.Increment,
        RasterizerState.StencilOp.Decr => StencilOperation.DecrementSaturate,
        RasterizerState.StencilOp.DecrWrap => StencilOperation.Decrement,
        RasterizerState.StencilOp.Invert => StencilOperation.Invert,
        _ => StencilOperation.Keep,
    };

    public static bool HasStencil(Format format) =>
        format is Format.D24_UNorm_S8_UInt or Format.D32_Float_S8X24_UInt;

    public static void GetVertexSemantic(uint semantic, out string name, out uint index)
    {
        switch ((VertexFormat.VertexSemantic)semantic)
        {
            case VertexFormat.VertexSemantic.Position:
                name = "POSITION";
                index = 0;
                return;
            case VertexFormat.VertexSemantic.TexCoord0:
                name = "TEXCOORD";
                index = 0;
                return;
            case VertexFormat.VertexSemantic.TexCoord1:
                name = "TEXCOORD";
                index = 1;
                return;
            case VertexFormat.VertexSemantic.Normal:
                name = "NORMAL";
                index = 0;
                return;
            case VertexFormat.VertexSemantic.Color:
                name = "COLOR";
                index = 0;
                return;
            case VertexFormat.VertexSemantic.Tangent:
                name = "TANGENT";
                index = 0;
                return;
            case VertexFormat.VertexSemantic.BoneIndex:
                name = "BLENDINDICES";
                index = 0;
                return;
            case VertexFormat.VertexSemantic.BoneWeight:
                name = "BLENDWEIGHT";
                index = 0;
                return;
            default:
                name = "TEXCOORD";
                index = semantic;
                return;
        }
    }

    public static CullMode ToCullMode(RasterizerState.PolyFace face) => face switch
    {
        RasterizerState.PolyFace.None => CullMode.None,
        RasterizerState.PolyFace.Front => CullMode.Front,
        RasterizerState.PolyFace.Back => CullMode.Back,
        _ => CullMode.Back,
    };

    public static Blend ToBlend(RasterizerState.Blending b) => b switch
    {
        RasterizerState.Blending.Zero => Blend.Zero,
        RasterizerState.Blending.One => Blend.One,
        RasterizerState.Blending.SrcColor => Blend.SourceColor,
        RasterizerState.Blending.OneMinusSrcColor => Blend.InverseSourceColor,
        RasterizerState.Blending.DstColor => Blend.DestinationColor,
        RasterizerState.Blending.OneMinusDstColor => Blend.InverseDestinationColor,
        RasterizerState.Blending.SrcAlpha => Blend.SourceAlpha,
        RasterizerState.Blending.OneMinusSrcAlpha => Blend.InverseSourceAlpha,
        RasterizerState.Blending.DstAlpha => Blend.DestinationAlpha,
        RasterizerState.Blending.OneMinusDstAlpha => Blend.InverseDestinationAlpha,
        RasterizerState.Blending.ConstantColor => Blend.BlendFactor,
        RasterizerState.Blending.OneMinusConstantColor => Blend.InverseBlendFactor,
        RasterizerState.Blending.ConstantAlpha => Blend.AlphaFactor,
        RasterizerState.Blending.OneMinusConstantAlpha => Blend.InverseAlphaFactor,
        RasterizerState.Blending.SrcAlphaSaturate => Blend.SourceAlphaSaturate,
        RasterizerState.Blending.Src1Color => Blend.Source1Color,
        RasterizerState.Blending.OneMinusSrc1Color => Blend.InverseSource1Color,
        RasterizerState.Blending.Src1Alpha => Blend.Source1Alpha,
        RasterizerState.Blending.OneMinusSrc1Alpha => Blend.InverseSource1Alpha,
        _ => Blend.One,
    };

    public static BlendOperation ToBlendOp(RasterizerState.BlendMode mode) => mode switch
    {
        RasterizerState.BlendMode.Add => BlendOperation.Add,
        RasterizerState.BlendMode.Subtract => BlendOperation.Subtract,
        RasterizerState.BlendMode.ReverseSubtract => BlendOperation.RevSubtract,
        RasterizerState.BlendMode.Min => BlendOperation.Min,
        RasterizerState.BlendMode.Max => BlendOperation.Max,
        _ => BlendOperation.Add,
    };

    public static Filter ToFilter(TextureMin min, TextureMag mag)
    {
        bool nearestMin = min is TextureMin.Nearest or TextureMin.NearestMipmapNearest or TextureMin.NearestMipmapLinear;
        bool nearestMag = mag == TextureMag.Nearest;
        if (nearestMin && nearestMag) return Filter.MinMagMipPoint;
        if (!nearestMin && !nearestMag) return Filter.MinMagMipLinear;
        return Filter.MinMagMipLinear;
    }

    public static TextureAddressMode ToAddressMode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.Repeat => TextureAddressMode.Wrap,
        TextureWrap.MirroredRepeat => TextureAddressMode.Mirror,
        TextureWrap.ClampToEdge => TextureAddressMode.Clamp,
        TextureWrap.ClampToBorder => TextureAddressMode.Border,
        _ => TextureAddressMode.Wrap,
    };
}
