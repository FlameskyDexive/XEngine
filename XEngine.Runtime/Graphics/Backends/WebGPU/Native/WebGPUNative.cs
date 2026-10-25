// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Header baseline: ThirdParty/wgpu-native/include/webgpu/webgpu.h
// wgpu-native v29.0.1.1 (unified webgpu.h: WGPUStringView, WGPUFuture).
// wgpuInstanceWaitAny is exported but unimplemented in this wgpu-native tag — desktop waits via
// wgpuInstanceProcessEvents. Do not take a dependency on Silk.NET.WebGPU.
// WASM: DllImport("xengine_webgpu") + emdawnwebgpu (--use-port) statically linked into
// dotnet.native.wasm. Do not use "__Internal" — Mono-WASM maps that name to a NULL
// dlopen handle, which wasm_dl_load rejects, so every wgpu* P/Invoke becomes
// DllNotFoundException. The canvas helper already uses this library stem.

using System.Runtime.InteropServices;

namespace XEngine.Runtime.Backends.WebGPU.Native;

internal static unsafe class WebGPUNative
{
    internal const string HeaderRevision = "wgpu-native v29.0.1.1 / unified webgpu.h";
#if XENGINE_WEB
    internal const string NativeLibraryName = "xengine_webgpu";
#else
    internal const string NativeLibraryName = "wgpu_native";
#endif
    private const string LibraryName = NativeLibraryName;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuCreateInstance(WGPUInstanceDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUFuture wgpuInstanceRequestAdapter(
        nint instance,
        WGPURequestAdapterOptions* options,
        WGPUCallbackInfo callbackInfo);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuInstanceCreateSurface(nint instance, WGPUSurfaceDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuInstanceProcessEvents(nint instance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuInstanceRelease(nint instance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUStatus wgpuAdapterGetInfo(nint adapter, WGPUAdapterInfo* info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUStatus wgpuAdapterGetLimits(nint adapter, WGPULimits* limits);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuAdapterInfoFreeMembers(WGPUAdapterInfo info);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUFuture wgpuAdapterRequestDevice(
        nint adapter,
        WGPUDeviceDescriptor* descriptor,
        WGPUCallbackInfo callbackInfo);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuAdapterRelease(nint adapter);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceGetQueue(nint device);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUStatus wgpuDeviceGetLimits(nint device, WGPULimits* limits);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuDeviceRelease(nint device);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUFuture wgpuQueueOnSubmittedWorkDone(nint queue, WGPUCallbackInfo callbackInfo);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuQueueRelease(nint queue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuSurfaceConfigure(nint surface, WGPUSurfaceConfiguration* config);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUStatus wgpuSurfaceGetCapabilities(
        nint surface,
        nint adapter,
        WGPUSurfaceCapabilities* capabilities);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuSurfaceGetCurrentTexture(nint surface, WGPUSurfaceTexture* surfaceTexture);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUStatus wgpuSurfacePresent(nint surface);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuSurfaceUnconfigure(nint surface);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuSurfaceRelease(nint surface);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuSurfaceCapabilitiesFreeMembers(WGPUSurfaceCapabilities capabilities);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuTextureRelease(nint texture);

    internal const ulong WholeSize = ulong.MaxValue;
    internal const uint CopyStrideUndefined = uint.MaxValue;
    internal const uint DepthSliceUndefined = uint.MaxValue;
    internal const uint MipLevelCountUndefined = uint.MaxValue;
    internal const uint ArrayLayerCountUndefined = uint.MaxValue;
    internal const uint BytesPerRowAlignment = 256;

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateBuffer(nint device, WGPUBufferDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateTexture(nint device, WGPUTextureDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateSampler(nint device, WGPUSamplerDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateCommandEncoder(nint device, WGPUCommandEncoderDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateShaderModule(nint device, WGPUShaderModuleDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateBindGroupLayout(nint device, WGPUBindGroupLayoutDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreatePipelineLayout(nint device, WGPUPipelineLayoutDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateRenderPipeline(nint device, WGPURenderPipelineDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuDeviceCreateBindGroup(nint device, WGPUBindGroupDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuQueueWriteBuffer(nint queue, nint buffer, ulong bufferOffset, void* data, nuint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuQueueWriteTexture(
        nint queue,
        WGPUTexelCopyTextureInfo* destination,
        void* data,
        nuint dataSize,
        WGPUTexelCopyBufferLayout* dataLayout,
        WGPUExtent3D* writeSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuQueueSubmit(nint queue, nuint commandCount, nint* commands);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WGPUFuture wgpuBufferMapAsync(
        nint buffer,
        WGPUMapMode mode,
        nuint offset,
        nuint size,
        WGPUCallbackInfo callbackInfo);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void* wgpuBufferGetMappedRange(nint buffer, nuint offset, nuint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void* wgpuBufferGetConstMappedRange(nint buffer, nuint offset, nuint size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuBufferUnmap(nint buffer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuBufferRelease(nint buffer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuCommandEncoderCopyBufferToBuffer(
        nint encoder,
        nint source,
        ulong sourceOffset,
        nint destination,
        ulong destinationOffset,
        ulong size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuCommandEncoderCopyTextureToBuffer(
        nint encoder,
        WGPUTexelCopyTextureInfo* source,
        WGPUTexelCopyBufferInfo* destination,
        WGPUExtent3D* copySize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuCommandEncoderCopyTextureToTexture(
        nint encoder,
        WGPUTexelCopyTextureInfo* source,
        WGPUTexelCopyTextureInfo* destination,
        WGPUExtent3D* copySize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuCommandEncoderBeginRenderPass(nint encoder, WGPURenderPassDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuCommandEncoderFinish(nint encoder, WGPUCommandBufferDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuCommandEncoderRelease(nint encoder);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuCommandBufferRelease(nint commandBuffer);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderSetPipeline(nint pass, nint pipeline);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderSetBindGroup(
        nint pass,
        uint groupIndex,
        nint group,
        nuint dynamicOffsetCount,
        uint* dynamicOffsets);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderSetViewport(
        nint pass,
        float x,
        float y,
        float width,
        float height,
        float minDepth,
        float maxDepth);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderDraw(
        nint pass,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderDrawIndexed(
        nint pass,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int baseVertex,
        uint firstInstance);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderSetVertexBuffer(
        nint pass,
        uint slot,
        nint buffer,
        ulong offset,
        ulong size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderSetIndexBuffer(
        nint pass,
        nint buffer,
        WGPUIndexFormat format,
        ulong offset,
        ulong size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderSetScissorRect(
        nint pass,
        uint x,
        uint y,
        uint width,
        uint height);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderEnd(nint pass);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPassEncoderRelease(nint pass);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint wgpuTextureCreateView(nint texture, WGPUTextureViewDescriptor* descriptor);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuTextureViewRelease(nint textureView);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuSamplerRelease(nint sampler);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuBindGroupRelease(nint bindGroup);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuBindGroupLayoutRelease(nint bindGroupLayout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuPipelineLayoutRelease(nint pipelineLayout);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuRenderPipelineRelease(nint renderPipeline);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void wgpuShaderModuleRelease(nint shaderModule);

#if XENGINE_WEB
    [DllImport("xengine_webgpu", EntryPoint = "xengine_webgpu_create_canvas_surface", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint CreateCanvasSurface(nint instance, byte* selector);
#endif
}
