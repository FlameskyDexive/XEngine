// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;

namespace XEngine.Runtime.Backends.WebGPU.Native;

/// <summary>
/// Blittable resource / pipeline structs from unified <c>webgpu.h</c> (wgpu-native v29.0.1.1).
/// Layout must match the vendored header; tests lock the win-x64 sizes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUBufferDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public WGPUBufferUsage usage;
    public ulong size;
    public uint mappedAtCreation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUExtent3D
{
    public uint width;
    public uint height;
    public uint depthOrArrayLayers;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUOrigin3D
{
    public uint x;
    public uint y;
    public uint z;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUTexelCopyBufferLayout
{
    public ulong offset;
    public uint bytesPerRow;
    public uint rowsPerImage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUTexelCopyBufferInfo
{
    public WGPUTexelCopyBufferLayout layout;
    public nint buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUTexelCopyTextureInfo
{
    public nint texture;
    public uint mipLevel;
    public WGPUOrigin3D origin;
    public WGPUTextureAspect aspect;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUTextureDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public WGPUTextureUsage usage;
    public WGPUTextureDimension dimension;
    public WGPUExtent3D size;
    public WGPUTextureFormat format;
    public uint mipLevelCount;
    public uint sampleCount;
    public nuint viewFormatCount;
    public WGPUTextureFormat* viewFormats;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSamplerDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public WGPUAddressMode addressModeU;
    public WGPUAddressMode addressModeV;
    public WGPUAddressMode addressModeW;
    public WGPUFilterMode magFilter;
    public WGPUFilterMode minFilter;
    public WGPUMipmapFilterMode mipmapFilter;
    public float lodMinClamp;
    public float lodMaxClamp;
    public uint compare;
    public ushort maxAnisotropy;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUShaderSourceWGSL
{
    public WGPUChainedStruct chain;
    public WGPUStringView code;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUShaderModuleDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUCommandEncoderDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUCommandBufferDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUColor
{
    public double r;
    public double g;
    public double b;
    public double a;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPURenderPassColorAttachment
{
    public WGPUChainedStruct* nextInChain;
    public nint view;
    public uint depthSlice;
    public nint resolveTarget;
    public WGPULoadOp loadOp;
    public WGPUStoreOp storeOp;
    public WGPUColor clearValue;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPURenderPassDepthStencilAttachment
{
    public WGPUChainedStruct* nextInChain;
    public nint view;
    public WGPULoadOp depthLoadOp;
    public WGPUStoreOp depthStoreOp;
    public float depthClearValue;
    public uint depthReadOnly;
    public WGPULoadOp stencilLoadOp;
    public WGPUStoreOp stencilStoreOp;
    public uint stencilClearValue;
    public uint stencilReadOnly;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPURenderPassDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public nuint colorAttachmentCount;
    public WGPURenderPassColorAttachment* colorAttachments;
    public WGPURenderPassDepthStencilAttachment* depthStencilAttachment;
    public nint occlusionQuerySet;
    public void* timestampWrites;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUTextureViewDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public WGPUTextureFormat format;
    public WGPUTextureViewDimension dimension;
    public uint baseMipLevel;
    public uint mipLevelCount;
    public uint baseArrayLayer;
    public uint arrayLayerCount;
    public WGPUTextureAspect aspect;
    public WGPUTextureUsage usage;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUVertexState
{
    public WGPUChainedStruct* nextInChain;
    public nint module;
    public WGPUStringView entryPoint;
    public nuint constantCount;
    public void* constants;
    public nuint bufferCount;
    public WGPUVertexBufferLayout* buffers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUPrimitiveState
{
    public WGPUChainedStruct* nextInChain;
    public WGPUPrimitiveTopology topology;
    public WGPUIndexFormat stripIndexFormat;
    public WGPUFrontFace frontFace;
    public WGPUCullMode cullMode;
    public uint unclippedDepth;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUMultisampleState
{
    public WGPUChainedStruct* nextInChain;
    public uint count;
    public uint mask;
    public uint alphaToCoverageEnabled;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUFragmentState
{
    public WGPUChainedStruct* nextInChain;
    public nint module;
    public WGPUStringView entryPoint;
    public nuint constantCount;
    public void* constants;
    public nuint targetCount;
    public WGPUColorTargetState* targets;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUColorTargetState
{
    public WGPUChainedStruct* nextInChain;
    public WGPUTextureFormat format;
    public WGPUBlendState* blend;
    public WGPUColorWriteMask writeMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPURenderPipelineDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public nint layout;
    public WGPUVertexState vertex;
    public WGPUPrimitiveState primitive;
    public void* depthStencil;
    public WGPUMultisampleState multisample;
    public WGPUFragmentState* fragment;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUBufferBindingLayout
{
    public WGPUChainedStruct* nextInChain;
    public WGPUBufferBindingType type;
    public uint hasDynamicOffset;
    public ulong minBindingSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUSamplerBindingLayout
{
    public WGPUChainedStruct* nextInChain;
    public WGPUSamplerBindingType type;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUTextureBindingLayout
{
    public WGPUChainedStruct* nextInChain;
    public WGPUTextureSampleType sampleType;
    public WGPUTextureViewDimension viewDimension;
    public uint multisampled;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUStorageTextureBindingLayout
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStorageTextureAccess access;
    public WGPUTextureFormat format;
    public WGPUTextureViewDimension viewDimension;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUBindGroupLayoutEntry
{
    public WGPUChainedStruct* nextInChain;
    public uint binding;
    public WGPUShaderStage visibility;
    public uint bindingArraySize;
    public WGPUBufferBindingLayout buffer;
    public WGPUSamplerBindingLayout sampler;
    public WGPUTextureBindingLayout texture;
    public WGPUStorageTextureBindingLayout storageTexture;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUBindGroupLayoutDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public nuint entryCount;
    public WGPUBindGroupLayoutEntry* entries;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUBindGroupEntry
{
    public WGPUChainedStruct* nextInChain;
    public uint binding;
    public nint buffer;
    public ulong offset;
    public ulong size;
    public nint sampler;
    public nint textureView;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUBindGroupDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public nint layout;
    public nuint entryCount;
    public WGPUBindGroupEntry* entries;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUPipelineLayoutDescriptor
{
    public WGPUChainedStruct* nextInChain;
    public WGPUStringView label;
    public nuint bindGroupLayoutCount;
    public nint* bindGroupLayouts;
    public uint immediateSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUBlendComponent
{
    public WGPUBlendOperation operation;
    public WGPUBlendFactor srcFactor;
    public WGPUBlendFactor dstFactor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUBlendState
{
    public WGPUBlendComponent color;
    public WGPUBlendComponent alpha;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WGPUStencilFaceState
{
    public WGPUCompareFunction compare;
    public WGPUStencilOperation failOp;
    public WGPUStencilOperation depthFailOp;
    public WGPUStencilOperation passOp;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUDepthStencilState
{
    public WGPUChainedStruct* nextInChain;
    public WGPUTextureFormat format;
    public WGPUOptionalBool depthWriteEnabled;
    public WGPUCompareFunction depthCompare;
    public WGPUStencilFaceState stencilFront;
    public WGPUStencilFaceState stencilBack;
    public uint stencilReadMask;
    public uint stencilWriteMask;
    public int depthBias;
    public float depthBiasSlopeScale;
    public float depthBiasClamp;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUVertexAttribute
{
    public WGPUChainedStruct* nextInChain;
    public WGPUVertexFormat format;
    public ulong offset;
    public uint shaderLocation;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WGPUVertexBufferLayout
{
    public WGPUChainedStruct* nextInChain;
    public WGPUVertexStepMode stepMode;
    public ulong arrayStride;
    public nuint attributeCount;
    public WGPUVertexAttribute* attributes;
}
