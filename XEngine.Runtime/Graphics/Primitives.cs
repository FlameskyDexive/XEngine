// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
using System;

namespace XEngine.Runtime;

public enum TextureWrap { Repeat, ClampToBorder, ClampToEdge, MirroredRepeat }
public enum TextureType { Texture2D, Texture3D, TextureCubeMap, Texture2DArray, }
public enum TextureParameter { WrapS, WrapT, WrapR, MinFilter, MagFilter }
public enum TextureMin { Nearest, Linear, NearestMipmapNearest, LinearMipmapNearest, NearestMipmapLinear, LinearMipmapLinear }
public enum TextureMag { Nearest, Linear }
public enum TextureImageFormat
{
    Color4b,
    Byte,

    Short,
    Short2,
    Short3,
    Short4,

    Float,
    Float2,
    Float3,
    Float4,
    Depth16f,
    Depth24f,
    Depth32f,

    Int,
    Int2,
    Int3,
    Int4,

    UnsignedShort,
    UnsignedShort2,
    UnsignedShort3,
    UnsignedShort4,

    UnsignedInt,
    UnsignedInt2,
    UnsignedInt3,
    UnsignedInt4,

    Depth24Stencil8,

    // Android Vulkan block-compressed texture formats. These formats are uploaded as
    // whole mip payloads through the compressed texture path; they are not texel-addressable.
    Etc2Rgb8,
    Etc2Rgba8,
    Astc4x4,
    Etc2Rgb8Srgb,
    Etc2Rgba8Srgb,
    Astc4x4Srgb,

    /// <summary>RGBA8 storage with hardware sRGB decoding of RGB before filtering; alpha stays linear.</summary>
    Color4bSrgb,
}

/// <summary>
/// Primitive topology for draw calls.
/// <para>
/// <see cref="LineLoop"/>, <see cref="TriangleFan"/>, and <see cref="Quads"/> are
/// OpenGL-legacy topologies. Vulkan and Direct3D12 do not support them natively;
/// backends may emulate them (index expansion) or reject draws that use them.
/// Prefer <see cref="Lines"/>, <see cref="LineStrip"/>, <see cref="Triangles"/>,
/// or <see cref="TriangleStrip"/> for portable content.
/// </para>
/// </summary>
public enum Topology
{
    Points,
    Lines,
    /// <summary>OpenGL-legacy; may be emulated or rejected on Vulkan/D3D12.</summary>
    LineLoop,
    LineStrip,
    Triangles,
    TriangleStrip,
    /// <summary>OpenGL-legacy; may be emulated or rejected on Vulkan/D3D12.</summary>
    TriangleFan,
    /// <summary>OpenGL-legacy; may be emulated or rejected on Vulkan/D3D12.</summary>
    Quads,
}

/// <summary>Helpers for portable (cross-backend) topology selection.</summary>
public static class TopologyUtilities
{
    /// <summary>
    /// Returns true when <paramref name="topology"/> is supported without emulation
    /// on Vulkan and Direct3D12 (as well as OpenGL).
    /// </summary>
    public static bool IsPortable(Topology topology) =>
        topology is Topology.Points
            or Topology.Lines
            or Topology.LineStrip
            or Topology.Triangles
            or Topology.TriangleStrip;
}

public enum FBOTarget { Read, Draw, Framebuffer, }

[Flags]
public enum ClearFlags
{
    Color = 1 << 1,
    Depth = 1 << 2,
    Stencil = 1 << 3,
}

public enum BlitFilter { Nearest, Linear }

public enum BufferType { VertexBuffer, ElementsBuffer, UniformBuffer, StructuredBuffer, IndirectBuffer, Count }

/// <summary>GPU memory visibility transition recorded by <see cref="CommandOpcode.ResourceBarrier"/>.</summary>
public enum GpuBarrierKind : byte
{
    None = 0,
    ComputeToCompute,
    ComputeToVertex,
    ComputeToIndirect,
    ComputeToPixel,
}

public struct RasterizerState
{
    [Flags]
    public enum ColorChannels : byte { None = 0, Red = 1, Green = 2, Blue = 4, Alpha = 8, RGB = Red | Green | Blue, All = RGB | Alpha }

    // Store disabled channels so default(RasterizerState) and older serialized passes
    // continue to write RGBA. A zero-initialized new field must not disable every pixel.
    [XEngine.Echo.SerializeField] private ColorChannels _disabledColorChannels;
    public ColorChannels ColorMask
    {
        readonly get => ColorChannels.All & ~_disabledColorChannels;
        set => _disabledColorChannels = ColorChannels.All & ~value;
    }

    public enum DepthMode { Never, Less, Equal, Lequal, Greater, Notequal, Gequal, Always }
    /// <summary>Triangle fill: solid raster (default) or wireframe (editor debug views). GL maps to
    /// glPolygonMode, D3D12/Vulkan to the pipeline fill/polygon mode; backends without the
    /// capability (WebGL2/WebGPU) ignore it and stay solid.</summary>
    public enum FillModeKind { Solid, Wireframe }
    public enum Blending { Zero, One, SrcColor, OneMinusSrcColor, DstColor, OneMinusDstColor, SrcAlpha, OneMinusSrcAlpha, DstAlpha, OneMinusDstAlpha, ConstantColor, OneMinusConstantColor, ConstantAlpha, OneMinusConstantAlpha, SrcAlphaSaturate, Src1Color, OneMinusSrc1Color, Src1Alpha, OneMinusSrc1Alpha }
    public enum BlendMode { Add, Subtract, ReverseSubtract, Min, Max }
    public enum PolyFace { None, Front, Back, FrontAndBack }
    public enum WindingOrder { CW, CCW }
    public enum StencilFunction { Never, Less, Equal, Lequal, Greater, Notequal, Gequal, Always }
    public enum StencilOp { Keep, Zero, Replace, Incr, IncrWrap, Decr, DecrWrap, Invert }

    public bool DepthTest = true;
    public bool DepthWrite = true;
    public DepthMode Depth = DepthMode.Lequal;

    public bool DoBlend = false;
    public Blending BlendSrc = Blending.SrcAlpha;
    public Blending BlendDst = Blending.OneMinusSrcAlpha;
    public BlendMode Blend = BlendMode.Add;

    public PolyFace CullFace = PolyFace.Back;

    public WindingOrder Winding = WindingOrder.CW;

    public FillModeKind FillMode = FillModeKind.Solid;

    public bool StencilEnabled = false;
    public StencilFunction StencilFunc = StencilFunction.Always;
    public int StencilRef = 0;
    public int StencilReadMask = 255;
    public int StencilWriteMask = 255;
    public StencilOp StencilPassOp = StencilOp.Keep;
    public StencilOp StencilFailOp = StencilOp.Keep;
    public StencilOp StencilZFailOp = StencilOp.Keep;

    public RasterizerState()
    {
        // Default constructor
    }
}
