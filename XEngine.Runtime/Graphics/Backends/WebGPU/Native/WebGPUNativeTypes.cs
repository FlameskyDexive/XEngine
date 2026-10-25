// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

namespace XEngine.Runtime.Backends.WebGPU.Native;

/// <summary>
/// Blittable subset of unified <c>webgpu.h</c> (wgpu-native v29.0.1.1) needed to create a device,
/// wait on futures, and configure a surface. Keep this file aligned with the vendored header in
/// <c>ThirdParty/wgpu-native/include/webgpu/webgpu.h</c>. Do not add wgpu-native extras from
/// <c>wgpu.h</c> — those do not exist on emdawnwebgpu.
/// </summary>
internal enum WGPUCallbackMode : uint
{
    WaitAnyOnly = 0x00000001,
    AllowProcessEvents = 0x00000002,
    AllowSpontaneous = 0x00000003,
}

internal enum WGPUFeatureLevel : uint
{
    Undefined = 0x00000000,
    Compatibility = 0x00000001,
    Core = 0x00000002,
}

internal enum WGPUInstanceFeatureName : uint
{
    TimedWaitAny = 0x00000001,
}

internal enum WGPUPowerPreference : uint
{
    Undefined = 0x00000000,
    LowPower = 0x00000001,
    HighPerformance = 0x00000002,
}

internal enum WGPUBackendType : uint
{
    Undefined = 0x00000000,
    Null = 0x00000001,
    WebGPU = 0x00000002,
    D3D11 = 0x00000003,
    D3D12 = 0x00000004,
    Metal = 0x00000005,
    Vulkan = 0x00000006,
    OpenGL = 0x00000007,
    OpenGLES = 0x00000008,
}

internal enum WGPUAdapterType : uint
{
    DiscreteGPU = 0x00000001,
    IntegratedGPU = 0x00000002,
    CPU = 0x00000003,
    Unknown = 0x00000004,
}

internal enum WGPURequestAdapterStatus : uint
{
    Success = 0x00000001,
    CallbackCancelled = 0x00000002,
    Unavailable = 0x00000003,
    Error = 0x00000004,
}

internal enum WGPURequestDeviceStatus : uint
{
    Success = 0x00000001,
    CallbackCancelled = 0x00000002,
    Error = 0x00000003,
}

internal enum WGPUQueueWorkDoneStatus : uint
{
    Success = 0x00000001,
    CallbackCancelled = 0x00000002,
    Error = 0x00000003,
}

internal enum WGPUWaitStatus : uint
{
    Success = 0x00000001,
    TimedOut = 0x00000002,
    Error = 0x00000003,
}

internal enum WGPUStatus : uint
{
    Success = 0x00000001,
    Error = 0x00000002,
}

internal enum WGPUErrorType : uint
{
    NoError = 0x00000001,
    Validation = 0x00000002,
    OutOfMemory = 0x00000003,
    Internal = 0x00000004,
    Unknown = 0x00000005,
}

internal enum WGPUDeviceLostReason : uint
{
    Unknown = 0x00000001,
    Destroyed = 0x00000002,
    CallbackCancelled = 0x00000003,
    FailedCreation = 0x00000004,
}

internal enum WGPUSType : uint
{
    ShaderSourceWGSL = 0x00000002,
    SurfaceSourceWindowsHWND = 0x00000005,
}

internal enum WGPUTextureFormat : uint
{
    Undefined = 0x00000000,
    R8Unorm = 0x00000001,
    R16Uint = 0x00000007,
    R16Float = 0x00000009,
    R32Float = 0x0000000E,
    R32Uint = 0x0000000F,
    R32Sint = 0x00000010,
    RG16Uint = 0x00000013,
    RG16Float = 0x00000015,
    RGBA8Unorm = 0x00000016,
    RGBA8UnormSrgb = 0x00000017,
    BGRA8Unorm = 0x0000001B,
    BGRA8UnormSrgb = 0x0000001C,
    RG32Float = 0x00000021,
    RG32Uint = 0x00000022,
    RG32Sint = 0x00000023,
    RGBA16Uint = 0x00000026,
    RGBA16Float = 0x00000028,
    RGBA32Float = 0x00000029,
    RGBA32Uint = 0x0000002A,
    RGBA32Sint = 0x0000002B,
    Depth16Unorm = 0x0000002D,
    Depth24Plus = 0x0000002E,
    Depth24PlusStencil8 = 0x0000002F,
    Depth32Float = 0x00000030,
    Depth32FloatStencil8 = 0x00000031,
    ETC2RGB8Unorm = 0x00000040,
    ETC2RGB8UnormSrgb = 0x00000041,
    ETC2RGBA8Unorm = 0x00000044,
    ETC2RGBA8UnormSrgb = 0x00000045,
    ASTC4x4Unorm = 0x0000004A,
    ASTC4x4UnormSrgb = 0x0000004B,
}

internal enum WGPUPresentMode : uint
{
    Undefined = 0x00000000,
    Fifo = 0x00000001,
    FifoRelaxed = 0x00000002,
    Immediate = 0x00000003,
    Mailbox = 0x00000004,
}

internal enum WGPUCompositeAlphaMode : uint
{
    Auto = 0x00000000,
    Opaque = 0x00000001,
    Premultiplied = 0x00000002,
    Unpremultiplied = 0x00000003,
    Inherit = 0x00000004,
}

internal enum WGPUSurfaceGetCurrentTextureStatus : uint
{
    SuccessOptimal = 0x00000001,
    SuccessSuboptimal = 0x00000002,
    Timeout = 0x00000003,
    Outdated = 0x00000004,
    Lost = 0x00000005,
    Error = 0x00000006,
}

[Flags]
internal enum WGPUTextureUsage : ulong
{
    None = 0,
    CopySrc = 1 << 0,
    CopyDst = 1 << 1,
    TextureBinding = 1 << 2,
    StorageBinding = 1 << 3,
    RenderAttachment = 1 << 4,
}

[Flags]
internal enum WGPUBufferUsage : ulong
{
    None = 0,
    MapRead = 1 << 0,
    MapWrite = 1 << 1,
    CopySrc = 1 << 2,
    CopyDst = 1 << 3,
    Index = 1 << 4,
    Vertex = 1 << 5,
    Uniform = 1 << 6,
    Storage = 1 << 7,
    Indirect = 1 << 8,
}

[Flags]
internal enum WGPUMapMode : ulong
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
}

[Flags]
internal enum WGPUShaderStage : ulong
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Compute = 1 << 2,
}

[Flags]
internal enum WGPUColorWriteMask : ulong
{
    None = 0,
    Red = 1 << 0,
    Green = 1 << 1,
    Blue = 1 << 2,
    Alpha = 1 << 3,
    All = Red | Green | Blue | Alpha,
}

internal enum WGPUMapAsyncStatus : uint
{
    Success = 0x00000001,
    CallbackCancelled = 0x00000002,
    Error = 0x00000003,
    Aborted = 0x00000004,
}

internal enum WGPUAddressMode : uint
{
    Undefined = 0,
    ClampToEdge = 0x00000001,
    Repeat = 0x00000002,
    MirrorRepeat = 0x00000003,
}

internal enum WGPUFilterMode : uint
{
    Undefined = 0,
    Nearest = 0x00000001,
    Linear = 0x00000002,
}

internal enum WGPUMipmapFilterMode : uint
{
    Undefined = 0,
    Nearest = 0x00000001,
    Linear = 0x00000002,
}

internal enum WGPUTextureDimension : uint
{
    Undefined = 0,
    D1 = 0x00000001,
    D2 = 0x00000002,
    D3 = 0x00000003,
}

internal enum WGPUTextureViewDimension : uint
{
    Undefined = 0,
    D1 = 0x00000001,
    D2 = 0x00000002,
    D2Array = 0x00000003,
    Cube = 0x00000004,
    CubeArray = 0x00000005,
    D3 = 0x00000006,
}

internal enum WGPUTextureAspect : uint
{
    Undefined = 0,
    All = 0x00000001,
    StencilOnly = 0x00000002,
    DepthOnly = 0x00000003,
}

internal enum WGPUTextureSampleType : uint
{
    Undefined = 0,
    Float = 0x00000002,
    UnfilterableFloat = 0x00000003,
    Depth = 0x00000004,
    Sint = 0x00000005,
    Uint = 0x00000006,
}

internal enum WGPUSamplerBindingType : uint
{
    Undefined = 0,
    Filtering = 0x00000002,
    NonFiltering = 0x00000003,
    Comparison = 0x00000004,
}

internal enum WGPUBufferBindingType : uint
{
    BindingNotUsed = 0,
    Undefined = 0x00000001,
    Uniform = 0x00000002,
    Storage = 0x00000003,
    ReadOnlyStorage = 0x00000004,
}

internal enum WGPUStorageTextureAccess : uint
{
    BindingNotUsed = 0,
    Undefined = 0x00000001,
    WriteOnly = 0x00000002,
    ReadOnly = 0x00000003,
    ReadWrite = 0x00000004,
}

internal enum WGPULoadOp : uint
{
    Undefined = 0,
    Load = 0x00000001,
    Clear = 0x00000002,
}

internal enum WGPUStoreOp : uint
{
    Undefined = 0,
    Store = 0x00000001,
    Discard = 0x00000002,
}

internal enum WGPUPrimitiveTopology : uint
{
    Undefined = 0,
    PointList = 0x00000001,
    LineList = 0x00000002,
    LineStrip = 0x00000003,
    TriangleList = 0x00000004,
    TriangleStrip = 0x00000005,
}

internal enum WGPUFrontFace : uint
{
    Undefined = 0,
    CCW = 0x00000001,
    CW = 0x00000002,
}

internal enum WGPUCullMode : uint
{
    Undefined = 0,
    None = 0x00000001,
    Front = 0x00000002,
    Back = 0x00000003,
}

internal enum WGPUIndexFormat : uint
{
    Undefined = 0,
    Uint16 = 0x00000001,
    Uint32 = 0x00000002,
}

internal enum WGPUCompareFunction : uint
{
    Undefined = 0,
    Never = 0x00000001,
    Less = 0x00000002,
    Equal = 0x00000003,
    LessEqual = 0x00000004,
    Greater = 0x00000005,
    NotEqual = 0x00000006,
    GreaterEqual = 0x00000007,
    Always = 0x00000008,
}

internal enum WGPUStencilOperation : uint
{
    Undefined = 0,
    Keep = 0x00000001,
    Zero = 0x00000002,
    Replace = 0x00000003,
    Invert = 0x00000004,
    IncrementClamp = 0x00000005,
    DecrementClamp = 0x00000006,
    IncrementWrap = 0x00000007,
    DecrementWrap = 0x00000008,
}

internal enum WGPUBlendFactor : uint
{
    Undefined = 0,
    Zero = 0x00000001,
    One = 0x00000002,
    Src = 0x00000003,
    OneMinusSrc = 0x00000004,
    SrcAlpha = 0x00000005,
    OneMinusSrcAlpha = 0x00000006,
    Dst = 0x00000007,
    OneMinusDst = 0x00000008,
    DstAlpha = 0x00000009,
    OneMinusDstAlpha = 0x0000000A,
    SrcAlphaSaturated = 0x0000000B,
    Constant = 0x0000000C,
    OneMinusConstant = 0x0000000D,
    Src1 = 0x0000000E,
    OneMinusSrc1 = 0x0000000F,
    Src1Alpha = 0x00000010,
    OneMinusSrc1Alpha = 0x00000011,
}

internal enum WGPUBlendOperation : uint
{
    Undefined = 0,
    Add = 0x00000001,
    Subtract = 0x00000002,
    ReverseSubtract = 0x00000003,
    Min = 0x00000004,
    Max = 0x00000005,
}

internal enum WGPUOptionalBool : uint
{
    False = 0,
    True = 0x00000001,
    Undefined = 0x00000002,
}

internal enum WGPUVertexFormat : uint
{
    Uint8x4 = 0x00000003,
    Sint8x4 = 0x00000006,
    Unorm8x4 = 0x00000009,
    Snorm8x4 = 0x0000000C,
    Sint16x2 = 0x00000011,
    Sint16x4 = 0x00000012,
    Snorm16x4 = 0x00000018,
    Float32 = 0x0000001C,
    Float32x2 = 0x0000001D,
    Float32x3 = 0x0000001E,
    Float32x4 = 0x0000001F,
    Sint32 = 0x00000024,
    Sint32x2 = 0x00000025,
    Sint32x4 = 0x00000027,
}

internal enum WGPUVertexStepMode : uint
{
    Undefined = 0,
    Vertex = 0x00000001,
    Instance = 0x00000002,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUChainedStruct
{
    public WGPUChainedStruct* next;
    public WGPUSType sType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUStringView
{
    public byte* data;
    public nuint length;

    public static WGPUStringView Empty => new() { data = null, length = 0 };

    public string ToManagedString()
    {
        if (data == null || length == 0)
            return string.Empty;
        if (length == nuint.MaxValue)
            return Marshal.PtrToStringUTF8((nint)data) ?? string.Empty;
        int byteCount = length > (nuint)int.MaxValue ? int.MaxValue : (int)length;
        return Marshal.PtrToStringUTF8((nint)data, byteCount) ?? string.Empty;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUFuture
{
    public ulong id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUFutureWaitInfo
{
    public WGPUFuture future;
    public uint completed;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUInstanceLimits
{
    public WGPUChainedStruct* nextInChain;
    public nuint timedWaitAnyMaxCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUInstanceDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public nuint requiredFeatureCount;
    public WGPUInstanceFeatureName* requiredFeatures;
    public WGPUInstanceLimits* requiredLimits;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPURequestAdapterOptions
{
    public WGPUChainedStruct* nextInChain;
    public WGPUFeatureLevel featureLevel;
    public WGPUPowerPreference powerPreference;
    public uint forceFallbackAdapter;
    public WGPUBackendType backendType;
    public nint compatibleSurface;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUQueueDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUCallbackInfo
{
    public WGPUChainedStruct* nextInChain;
    public WGPUCallbackMode mode;
    public nint callback;
    public void* userdata1;
    public void* userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUUncapturedErrorCallbackInfo
{
    public WGPUChainedStruct* nextInChain;
    public nint callback;
    public void* userdata1;
    public void* userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUDeviceDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public nuint requiredFeatureCount;
    public uint* requiredFeatures;
    public WGPULimits* requiredLimits;
    public WGPUQueueDescriptor defaultQueue;
    public WGPUCallbackInfo deviceLostCallbackInfo;
    public WGPUUncapturedErrorCallbackInfo uncapturedErrorCallbackInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUAdapterInfo
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView vendor;
    public WGPUStringView architecture;
    public WGPUStringView device;
    public WGPUStringView description;
    public WGPUBackendType backendType;
    public WGPUAdapterType adapterType;
    public uint vendorID;
    public uint deviceID;
    public uint subgroupMinSize;
    public uint subgroupMaxSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPULimits
{
    public WGPUChainedStruct* nextInChain;
    public uint maxTextureDimension1D;
    public uint maxTextureDimension2D;
    public uint maxTextureDimension3D;
    public uint maxTextureArrayLayers;
    public uint maxBindGroups;
    public uint maxBindGroupsPlusVertexBuffers;
    public uint maxBindingsPerBindGroup;
    public uint maxDynamicUniformBuffersPerPipelineLayout;
    public uint maxDynamicStorageBuffersPerPipelineLayout;
    public uint maxSampledTexturesPerShaderStage;
    public uint maxSamplersPerShaderStage;
    public uint maxStorageBuffersPerShaderStage;
    public uint maxStorageTexturesPerShaderStage;
    public uint maxUniformBuffersPerShaderStage;
    public ulong maxUniformBufferBindingSize;
    public ulong maxStorageBufferBindingSize;
    public uint minUniformBufferOffsetAlignment;
    public uint minStorageBufferOffsetAlignment;
    public uint maxVertexBuffers;
    public ulong maxBufferSize;
    public uint maxVertexAttributes;
    public uint maxVertexBufferArrayStride;
    public uint maxInterStageShaderVariables;
    public uint maxColorAttachments;
    public uint maxColorAttachmentBytesPerSample;
    public uint maxComputeWorkgroupStorageSize;
    public uint maxComputeInvocationsPerWorkgroup;
    public uint maxComputeWorkgroupSizeX;
    public uint maxComputeWorkgroupSizeY;
    public uint maxComputeWorkgroupSizeZ;
    public uint maxComputeWorkgroupsPerDimension;
    public uint maxImmediateSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSurfaceSourceWindowsHWND
{
    public WGPUChainedStruct chain;
    public nint hinstance;
    public nint hwnd;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSurfaceDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSurfaceConfiguration
{
    public WGPUChainedStruct* nextInChain;
    public nint device;
    public WGPUTextureFormat format;
    public WGPUTextureUsage usage;
    public uint width;
    public uint height;
    public nuint viewFormatCount;
    public WGPUTextureFormat* viewFormats;
    public WGPUCompositeAlphaMode alphaMode;
    public WGPUPresentMode presentMode;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSurfaceCapabilities
{
    public WGPUChainedStruct* nextInChain;
    public WGPUTextureUsage usages;
    public nuint formatCount;
    public WGPUTextureFormat* formats;
    public nuint presentModeCount;
    public WGPUPresentMode* presentModes;
    public nuint alphaModeCount;
    public WGPUCompositeAlphaMode* alphaModes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSurfaceTexture
{
    public WGPUChainedStruct* nextInChain;
    public nint texture;
    public WGPUSurfaceGetCurrentTextureStatus status;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void WGPURequestAdapterCallback(
    WGPURequestAdapterStatus status,
    nint adapter,
    WGPUStringView message,
    void* userdata1,
    void* userdata2);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void WGPURequestDeviceCallback(
    WGPURequestDeviceStatus status,
    nint device,
    WGPUStringView message,
    void* userdata1,
    void* userdata2);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void WGPUQueueWorkDoneCallback(
    WGPUQueueWorkDoneStatus status,
    WGPUStringView message,
    void* userdata1,
    void* userdata2);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void WGPUDeviceLostCallback(
    nint* device,
    WGPUDeviceLostReason reason,
    WGPUStringView message,
    void* userdata1,
    void* userdata2);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void WGPUUncapturedErrorCallback(
    nint* device,
    WGPUErrorType type,
    WGPUStringView message,
    void* userdata1,
    void* userdata2);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void WGPUBufferMapCallback(
    WGPUMapAsyncStatus status,
    WGPUStringView message,
    void* userdata1,
    void* userdata2);
