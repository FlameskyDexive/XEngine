// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Backends.Metal.Native;

/// <summary>
/// Frozen I0 selector strings and a few Metal enum values used by M1.
/// Selector text is compile-time constant; IMPs are never looked up by reflection.
/// </summary>
internal static class MetalSelectorNames
{
    internal const string NewCommandQueue = "newCommandQueue";
    internal const string NewBufferWithLengthOptions = "newBufferWithLength:options:";
    internal const string NewTextureWithDescriptor = "newTextureWithDescriptor:";
    internal const string NewSamplerStateWithDescriptor = "newSamplerStateWithDescriptor:";
    internal const string NewLibraryWithSourceOptionsError = "newLibraryWithSource:options:error:";
    internal const string NewLibraryWithDataError = "newLibraryWithData:error:";
    internal const string NewRenderPipelineStateWithDescriptorError = "newRenderPipelineStateWithDescriptor:error:";
    internal const string NewDepthStencilStateWithDescriptor = "newDepthStencilStateWithDescriptor:";
    internal const string CommandBuffer = "commandBuffer";
    internal const string RenderCommandEncoderWithDescriptor = "renderCommandEncoderWithDescriptor:";
    internal const string BlitCommandEncoder = "blitCommandEncoder";
    internal const string PresentDrawable = "presentDrawable:";
    internal const string Commit = "commit";
    internal const string WaitUntilCompleted = "waitUntilCompleted";
    internal const string Status = "status";
    internal const string SetRenderPipelineState = "setRenderPipelineState:";
    internal const string SetViewport = "setViewport:";
    internal const string SetScissorRect = "setScissorRect:";
    internal const string SetVertexBufferOffsetAtIndex = "setVertexBuffer:offset:atIndex:";
    internal const string SetVertexBytesLengthAtIndex = "setVertexBytes:length:atIndex:";
    internal const string SetVertexTextureAtIndex = "setVertexTexture:atIndex:";
    internal const string SetFragmentBufferOffsetAtIndex = "setFragmentBuffer:offset:atIndex:";
    internal const string SetFragmentBytesLengthAtIndex = "setFragmentBytes:length:atIndex:";
    internal const string SetFragmentTextureAtIndex = "setFragmentTexture:atIndex:";
    internal const string SetFragmentSamplerStateAtIndex = "setFragmentSamplerState:atIndex:";
    internal const string SetCullMode = "setCullMode:";
    internal const string SetDepthStencilState = "setDepthStencilState:";
    internal const string SetDepthCompareFunction = "setDepthCompareFunction:";
    internal const string SetDepthWriteEnabled = "setDepthWriteEnabled:";
    internal const string DrawPrimitivesVertexStartVertexCountInstanceCount =
        "drawPrimitives:vertexStart:vertexCount:instanceCount:";
    internal const string DrawIndexedPrimitivesIndexCountIndexTypeIndexBufferIndexBufferOffsetInstanceCount =
        "drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:";
    internal const string EndEncoding = "endEncoding";
    internal const string CopyFromBuffer = "copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:";
    internal const string CopyFromTexture = "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:";
    internal const string GenerateMipmapsForTexture = "generateMipmapsForTexture:";
    internal const string NextDrawable = "nextDrawable";
    internal const string SetLayerDevice = "setDevice:";
    internal const string SetDrawableSize = "setDrawableSize:";
    internal const string Texture = "texture";
    internal const string NewTextureViewWithPixelFormat = "newTextureViewWithPixelFormat:";
    internal const string Retain = "retain";
    internal const string Release = "release";
    internal const string Name = "name";
    internal const string UTF8String = "UTF8String";
    internal const string SupportsFamily = "supportsFamily:";
    internal const string Texture2DDescriptorWithPixelFormatWidthHeightMipmapped =
        "texture2DDescriptorWithPixelFormat:width:height:mipmapped:";
    internal const string TextureCubeDescriptorWithPixelFormatSizeMipmapped =
        "textureCubeDescriptorWithPixelFormat:size:mipmapped:";
    internal const string SetUsage = "setUsage:";
    internal const string SetStorageMode = "setStorageMode:";
    internal const string NewCounterSampleBufferWithDescriptorError = "newCounterSampleBufferWithDescriptor:error:";
    internal const string Contents = "contents";
    internal const string New = "new";
    internal const string Drain = "drain";
    internal const string SetSAddressMode = "setSAddressMode:";
    internal const string SetTAddressMode = "setTAddressMode:";
    internal const string SetRAddressMode = "setRAddressMode:";
    internal const string SetMinFilter = "setMinFilter:";
    internal const string SetMagFilter = "setMagFilter:";
    internal const string SetMipFilter = "setMipFilter:";
    internal const string MipmapLevelCount = "mipmapLevelCount";
    internal const string ReplaceRegionMipmapLevelWithBytesBytesPerRow =
        "replaceRegion:mipmapLevel:withBytes:bytesPerRow:";
    internal const string ReplaceRegionSliceMipmapLevelWithBytesBytesPerRowBytesPerImage =
        "replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:bytesPerImage:";
    internal const string GetBytesBytesPerRowFromRegionMipmapLevelSlice =
        "getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:";
    internal const string GetBytesBytesPerRowFromRegionMipmapLevel =
        "getBytes:bytesPerRow:fromRegion:mipmapLevel:";
    internal const string SetMipmapLevelCount = "setMipmapLevelCount:";
    internal const string Alloc = "alloc";
    internal const string InitWithUTF8String = "initWithUTF8String:";
    internal const string NewFunctionWithName = "newFunctionWithName:";
    internal const string FunctionNames = "functionNames";
    internal const string Count = "count";
    internal const string VertexDescriptor = "vertexDescriptor";
    internal const string Attributes = "attributes";
    internal const string Layouts = "layouts";
    internal const string SetAttributeFormat = "setFormat:";
    internal const string SetAttributeOffset = "setOffset:";
    internal const string SetAttributeBufferIndex = "setBufferIndex:";
    internal const string SetLayoutStride = "setStride:";
    internal const string SetLayoutStepFunction = "setStepFunction:";
    internal const string SetLayoutStepRate = "setStepRate:";
    internal const string SetVertexDescriptor = "setVertexDescriptor:";
    internal const string LocalizedDescription = "localizedDescription";
    internal const string SetVertexFunction = "setVertexFunction:";
    internal const string SetFragmentFunction = "setFragmentFunction:";
    internal const string ColorAttachments = "colorAttachments";
    internal const string ObjectAtIndexedSubscript = "objectAtIndexedSubscript:";
    internal const string SetPixelFormat = "setPixelFormat:";
    internal const string SetBlendingEnabled = "setBlendingEnabled:";
    internal const string SetSourceRGBBlendFactor = "setSourceRGBBlendFactor:";
    internal const string SetDestinationRGBBlendFactor = "setDestinationRGBBlendFactor:";
    internal const string SetRgbBlendOperation = "setRgbBlendOperation:";
    internal const string SetSourceAlphaBlendFactor = "setSourceAlphaBlendFactor:";
    internal const string SetDestinationAlphaBlendFactor = "setDestinationAlphaBlendFactor:";
    internal const string SetAlphaBlendOperation = "setAlphaBlendOperation:";
    internal const string SetWriteMask = "setWriteMask:";
    internal const string SetDepthAttachmentPixelFormat = "setDepthAttachmentPixelFormat:";
    internal const string SetStencilAttachmentPixelFormat = "setStencilAttachmentPixelFormat:";
    internal const string RenderPassDescriptor = "renderPassDescriptor";
    internal const string DepthAttachment = "depthAttachment";
    internal const string StencilAttachment = "stencilAttachment";
    internal const string SetTexture = "setTexture:";
    internal const string SetLoadAction = "setLoadAction:";
    internal const string SetStoreAction = "setStoreAction:";
    internal const string SetClearColor = "setClearColor:";
    internal const string SetClearDepth = "setClearDepth:";
    internal const string SetLevel = "setLevel:";
    internal const string SetFrontFacingWinding = "setFrontFacingWinding:";
}

internal enum MTLVertexStepFunction : ulong
{
    Constant = 0,
    PerVertex = 1,
    PerInstance = 2,
}

internal enum MTLGPUFamily : long
{
    Apple1 = 1001,
    Apple2 = 1002,
}

internal enum MTLPixelFormat : uint
{
    Invalid = 0,
    R8Unorm = 10,
    R8Sint = 14,
    R16Uint = 23,
    R16Float = 25,
    R32Uint = 53,
    R32Sint = 54,
    R32Float = 55,
    RG16Uint = 63,
    RG16Float = 65,
    RGBA8Unorm = 70,
    RGBA8UnormSrgb = 71,
    BGRA8Unorm = 80,
    RG32Uint = 103,
    RG32Sint = 104,
    RG32Float = 105,
    RGBA16Uint = 113,
    RGBA16Float = 115,
    RGBA32Uint = 123,
    RGBA32Sint = 124,
    RGBA32Float = 125,
    EAC_RGBA8 = 178,
    EAC_RGBA8_sRGB = 179,
    ETC2_RGB8 = 180,
    ETC2_RGB8_sRGB = 181,
    ASTC_4x4_sRGB = 186,
    ASTC_4x4_LDR = 204,
    Depth16Unorm = 250,
    Depth32Float = 252,
    Depth24Unorm_Stencil8 = 255,
    Depth32Float_Stencil8 = 260,
}

[Flags]
internal enum MTLTextureUsage : ulong
{
    ShaderRead = 1,
    RenderTarget = 4,
}

internal enum MTLStorageMode : ulong
{
    Shared = 0,
    Private = 2,
}

internal enum MTLSamplerAddressMode : ulong
{
    ClampToEdge = 0,
    Repeat = 2,
    MirrorRepeat = 3,
    ClampToZero = 4,
}

internal enum MTLSamplerMinMagFilter : ulong
{
    Nearest = 0,
    Linear = 1,
}

internal enum MTLSamplerMipFilter : ulong
{
    NotMipmapped = 0,
    Nearest = 1,
    Linear = 2,
}

internal enum MTLBlendFactor : ulong
{
    Zero = 0,
    One = 1,
    SourceColor = 2,
    OneMinusSourceColor = 3,
    SourceAlpha = 4,
    OneMinusSourceAlpha = 5,
    DestinationColor = 6,
    OneMinusDestinationColor = 7,
    DestinationAlpha = 8,
    OneMinusDestinationAlpha = 9,
    SourceAlphaSaturated = 10,
    BlendColor = 11,
    OneMinusBlendColor = 12,
    BlendAlpha = 13,
    OneMinusBlendAlpha = 14,
    Source1Color = 15,
    OneMinusSource1Color = 16,
    Source1Alpha = 17,
    OneMinusSource1Alpha = 18,
}

internal enum MTLBlendOperation : ulong
{
    Add = 0,
    Subtract = 1,
    ReverseSubtract = 2,
    Min = 3,
    Max = 4,
}

internal enum MTLCompareFunction : ulong
{
    Never = 0,
    Less = 1,
    Equal = 2,
    LessEqual = 3,
    Greater = 4,
    NotEqual = 5,
    GreaterEqual = 6,
    Always = 7,
}

internal enum MTLCullMode : ulong
{
    None = 0,
    Front = 1,
    Back = 2,
}

internal enum MTLWinding : ulong
{
    Clockwise = 0,
    CounterClockwise = 1,
}

internal enum MTLPrimitiveType : ulong
{
    Point = 0,
    Line = 1,
    LineStrip = 2,
    Triangle = 3,
    TriangleStrip = 4,
}

internal enum MTLIndexType : ulong
{
    UInt16 = 0,
    UInt32 = 1,
}

internal enum MTLStencilOperation : ulong
{
    Keep = 0,
    Zero = 1,
    Replace = 2,
    IncrementClamp = 3,
    DecrementClamp = 4,
    Invert = 5,
    IncrementWrap = 6,
    DecrementWrap = 7,
}

internal enum MTLVertexFormat : ulong
{
    Invalid = 0,
    UChar4 = 3,
    Char4 = 6,
    UChar4Normalized = 9,
    Char4Normalized = 12,
    Short2 = 16,
    Short4 = 18,
    Short4Normalized = 24,
    Float = 28,
    Float2 = 29,
    Float3 = 30,
    Float4 = 31,
    Int = 32,
    Int2 = 33,
    Int4 = 35,
}

[Flags]
internal enum MTLColorWriteMask : ulong
{
    None = 0,
    All = 0xF,
}

internal enum MTLLoadAction : ulong
{
    DontCare = 0,
    Load = 1,
    Clear = 2,
}

internal enum MTLStoreAction : ulong
{
    DontCare = 0,
    Store = 1,
}

internal struct MTLClearColor
{
    public double Red;
    public double Green;
    public double Blue;
    public double Alpha;
}

internal struct MTLViewport
{
    public double OriginX;
    public double OriginY;
    public double Width;
    public double Height;
    public double ZNear;
    public double ZFar;
}

internal struct MTLScissorRect
{
    public nuint X;
    public nuint Y;
    public nuint Width;
    public nuint Height;
}

internal struct MTLOrigin
{
    public nuint X;
    public nuint Y;
    public nuint Z;
}

internal struct MTLSize
{
    public nuint Width;
    public nuint Height;
    public nuint Depth;
}

internal struct MTLRegion
{
    public MTLOrigin Origin;
    public MTLSize Size;
}

internal enum MTLCommandBufferStatus : int
{
    NotEnqueued = 0,
    Enqueued = 1,
    Committed = 2,
    Scheduled = 3,
    Completed = 4,
    Error = 5,
}

internal static class MetalAvailability
{
    internal static bool IsAppleMetalHost =>
        OperatingSystem.IsIOS()
        || OperatingSystem.IsMacOS()
        || OperatingSystem.IsMacCatalyst()
        || OperatingSystem.IsTvOS();
}
