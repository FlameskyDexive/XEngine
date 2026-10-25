// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Binding path (2) frozen by I0: custom objc_msgSend P/Invoke.
// Do not reference Microsoft.iOS Metal types, Silk.NET.Metal, or SharpMetal.
// Selectors are compile-time constants; intern with sel_registerName; never reflect IMPs.
// Entry points only grow; do not change existing DllImport signatures or selector strings.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XEngine.Runtime.Backends.Metal.Native;

internal static unsafe class MetalNative
{
    internal const string HeaderRevision = "I0 path (2) objc_msgSend / Metal.framework";

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Metal = "/System/Library/Frameworks/Metal.framework/Metal";
    private static readonly bool s_integerStructsByValue =
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_retain")]
    private static extern nint objc_retain(nint value);

    [DllImport(ObjC, EntryPoint = "objc_release")]
    private static extern void objc_release(nint value);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint(nint receiver, nint selector, nint arg0);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nuint(nint receiver, nint selector, nuint arg0);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_texture2d(
        nint receiver,
        nint selector,
        nuint pixelFormat,
        nuint width,
        nuint height,
        byte mipmapped);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nuint2(nint receiver, nint selector, nuint arg0, nuint arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nuint2_byte(nint receiver, nint selector, nuint arg0, nuint arg1, byte arg2);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_replaceRegion(
        nint receiver,
        nint selector,
        ref MTLRegion region,
        nuint mip,
        nint bytes,
        nuint bytesPerRow);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_replaceRegionSlice(
        nint receiver,
        nint selector,
        ref MTLRegion region,
        nuint mip,
        nuint slice,
        nint bytes,
        nuint bytesPerRow,
        nuint bytesPerImage);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_getBytesSlice(
        nint receiver,
        nint selector,
        nint destination,
        nuint bytesPerRow,
        nuint bytesPerImage,
        ref MTLRegion region,
        nuint mip,
        nuint slice);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_getBytes(
        nint receiver,
        nint selector,
        nint destination,
        nuint bytesPerRow,
        ref MTLRegion region,
        nuint mip);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_copyTexture(
        nint receiver,
        nint selector,
        nint sourceTexture,
        nuint sourceSlice,
        nuint sourceLevel,
        ref MTLOrigin sourceOrigin,
        ref MTLSize sourceSize,
        nint destinationTexture,
        nuint destinationSlice,
        nuint destinationLevel,
        ref MTLOrigin destinationOrigin);

    // ARM64's indirect composite arguments are compatible with the existing ref
    // declarations. x64 places these 24–48 byte values on the stack; passing a
    // pointer there shifts the remaining arguments and corrupts texture regions.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_replaceRegion_x64(
        nint receiver, nint selector, MTLRegion region, nuint mip, nint bytes, nuint bytesPerRow);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_replaceRegionSlice_x64(
        nint receiver, nint selector, MTLRegion region, nuint mip, nuint slice,
        nint bytes, nuint bytesPerRow, nuint bytesPerImage);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_getBytesSlice_x64(
        nint receiver, nint selector, nint destination, nuint bytesPerRow, nuint bytesPerImage,
        MTLRegion region, nuint mip, nuint slice);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_getBytes_x64(
        nint receiver, nint selector, nint destination, nuint bytesPerRow, MTLRegion region, nuint mip);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_copyTexture_x64(
        nint receiver, nint selector, nint sourceTexture, nuint sourceSlice, nuint sourceLevel,
        MTLOrigin sourceOrigin, MTLSize sourceSize, nint destinationTexture,
        nuint destinationSlice, nuint destinationLevel, MTLOrigin destinationOrigin);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint3(
        nint receiver,
        nint selector,
        nint arg0,
        nint arg1,
        nint arg2);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_nint2(
        nint receiver,
        nint selector,
        nint arg0,
        nint arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_byte(nint receiver, nint selector, byte arg0);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_f64(nint receiver, nint selector, double arg0);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_clearColor(nint receiver, nint selector, MTLClearColor color);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_viewport(nint receiver, nint selector, MTLViewport viewport);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_size(nint receiver, nint selector, double width, double height);

    // Preserve ARM64's indirect integer composite binding; x64 needs a stack value.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_scissor(nint receiver, nint selector, ref MTLScissorRect rect);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_scissor_x64(nint receiver, nint selector, MTLScissorRect rect);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bufferAt(
        nint receiver,
        nint selector,
        nint buffer,
        nuint offset,
        nuint index);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_drawPrim(
        nint receiver,
        nint selector,
        nuint primitiveType,
        nuint vertexStart,
        nuint vertexCount,
        nuint instanceCount);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_drawIndexed(
        nint receiver,
        nint selector,
        nuint primitiveType,
        nuint indexCount,
        nuint indexType,
        nint indexBuffer,
        nuint indexBufferOffset,
        nuint instanceCount);

    [DllImport(Metal, EntryPoint = "MTLCreateSystemDefaultDevice")]
    private static extern nint MTLCreateSystemDefaultDevice();

    // libdispatch has no standalone on-disk dylib on modern macOS; its symbols ship in the dyld
    // shared cache under libSystem's install name (same resolution as libobjc.A.dylib above).
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dispatch_data_create")]
    private static extern nint dispatch_data_create(
        void* buffer,
        nuint size,
        nint queue,
        nint destructor);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dispatch_release")]
    private static extern void dispatch_release(nint data);

    private static readonly nint s_newCommandQueue;
    private static readonly nint s_commandBuffer;
    private static readonly nint s_presentDrawable;
    private static readonly nint s_commit;
    private static readonly nint s_waitUntilCompleted;
    private static readonly nint s_nextDrawable;
    private static readonly nint s_setLayerDevice;
    private static readonly nint s_setDrawableSize;
    private static readonly nint s_texture;
    private static readonly nint s_name;
    private static readonly nint s_utf8String;
    private static readonly nint s_supportsFamily;
    private static readonly nint s_newTextureWithDescriptor;
    private static readonly nint s_texture2DDescriptor;
    private static readonly nint s_setUsage;
    private static readonly nint s_setStorageMode;
    private static readonly nint s_newBuffer;
    private static readonly nint s_contents;
    private static readonly nint s_blitEncoder;
    private static readonly nint s_endEncoding;
    private static readonly nint s_generateMipmaps;
    private static readonly nint s_copyFromTexture;
    private static readonly nint s_replaceRegion;
    private static readonly nint s_replaceRegionSlice;
    private static readonly nint s_getBytesSlice;
    private static readonly nint s_textureCubeDescriptor;
    private static readonly nint s_getBytes;
    private static readonly nint s_newSampler;
    private static readonly nint s_new;
    private static readonly nint s_drain;
    private static nint s_autoreleasePoolClass;
    private static readonly nint s_setSAddress;
    private static readonly nint s_setTAddress;
    private static readonly nint s_setRAddress;
    private static readonly nint s_setMinFilter;
    private static readonly nint s_setMagFilter;
    private static readonly nint s_setMipFilter;
    private static readonly nint s_mipmapLevelCount;
    private static readonly nint s_alloc;
    private static readonly nint s_initWithUtf8;
    private static readonly nint s_newLibraryWithSource;
    private static readonly nint s_newLibraryWithData;
    private static readonly nint s_newFunctionWithName;
    private static readonly nint s_functionNames;
    private static readonly nint s_count;
    private static readonly nint s_localizedDescription;
    private static readonly nint s_newRenderPipelineState;
    private static readonly nint s_newDepthStencilState;
    private static readonly nint s_vertexDescriptorClass;
    private static readonly nint s_attributes;
    private static readonly nint s_layouts;
    private static readonly nint s_setAttributeFormat;
    private static readonly nint s_setAttributeOffset;
    private static readonly nint s_setAttributeBufferIndex;
    private static readonly nint s_setLayoutStride;
    private static readonly nint s_setLayoutStepFunction;
    private static readonly nint s_setLayoutStepRate;
    private static readonly nint s_setVertexDescriptor;
    private static readonly nint s_setVertexFunction;
    private static readonly nint s_setFragmentFunction;
    private static readonly nint s_colorAttachments;
    private static readonly nint s_objectAtIndexedSubscript;
    private static readonly nint s_setPixelFormat;
    private static readonly nint s_setBlendingEnabled;
    private static readonly nint s_setSourceRgbBlend;
    private static readonly nint s_setDestinationRgbBlend;
    private static readonly nint s_setRgbBlendOp;
    private static readonly nint s_setSourceAlphaBlend;
    private static readonly nint s_setDestinationAlphaBlend;
    private static readonly nint s_setAlphaBlendOp;
    private static readonly nint s_setWriteMask;
    private static readonly nint s_setDepthAttachmentPixelFormat;
    private static readonly nint s_setStencilAttachmentPixelFormat;
    private static readonly nint s_setDepthCompareFunction;
    private static readonly nint s_setDepthWriteEnabled;
    private static readonly nint s_renderPassDescriptor;
    private static readonly nint s_depthAttachment;
    private static readonly nint s_stencilAttachment;
    private static readonly nint s_setTexture;
    private static readonly nint s_setLoadAction;
    private static readonly nint s_setStoreAction;
    private static readonly nint s_setClearColor;
    private static readonly nint s_setClearDepth;
    private static readonly nint s_setLevel;
    private static readonly nint s_renderEncoder;
    private static readonly nint s_setPipeline;
    private static readonly nint s_setViewport;
    private static readonly nint s_setScissorRect;
    private static readonly nint s_setVertexBuffer;
    private static readonly nint s_setFragmentBuffer;
    private static readonly nint s_setVertexBytes;
    private static readonly nint s_setFragmentBytes;
    private static readonly nint s_setFragmentTexture;
    private static readonly nint s_setVertexTexture;
    private static readonly nint s_setFragmentSampler;
    private static readonly nint s_setCullMode;
    private static readonly nint s_setDepthStencilState;
    private static readonly nint s_setFrontFacing;
    private static readonly nint s_drawPrimitives;
    private static readonly nint s_drawIndexed;

    static MetalNative()
    {
        // Intern the I0-frozen surface up front so a missing selector fails at load, not mid-frame.
        s_newCommandQueue = sel_registerName(MetalSelectorNames.NewCommandQueue);
        s_newBuffer = sel_registerName(MetalSelectorNames.NewBufferWithLengthOptions);
        s_newTextureWithDescriptor = sel_registerName(MetalSelectorNames.NewTextureWithDescriptor);
        s_newSampler = sel_registerName(MetalSelectorNames.NewSamplerStateWithDescriptor);
        sel_registerName(MetalSelectorNames.NewLibraryWithSourceOptionsError);
        sel_registerName(MetalSelectorNames.NewLibraryWithDataError);
        sel_registerName(MetalSelectorNames.NewRenderPipelineStateWithDescriptorError);
        s_newDepthStencilState = sel_registerName(MetalSelectorNames.NewDepthStencilStateWithDescriptor);
        s_commandBuffer = sel_registerName(MetalSelectorNames.CommandBuffer);
        sel_registerName(MetalSelectorNames.RenderCommandEncoderWithDescriptor);
        s_blitEncoder = sel_registerName(MetalSelectorNames.BlitCommandEncoder);
        s_presentDrawable = sel_registerName(MetalSelectorNames.PresentDrawable);
        s_commit = sel_registerName(MetalSelectorNames.Commit);
        s_waitUntilCompleted = sel_registerName(MetalSelectorNames.WaitUntilCompleted);
        sel_registerName(MetalSelectorNames.Status);
        sel_registerName(MetalSelectorNames.SetRenderPipelineState);
        sel_registerName(MetalSelectorNames.SetViewport);
        sel_registerName(MetalSelectorNames.SetScissorRect);
        sel_registerName(MetalSelectorNames.SetVertexBufferOffsetAtIndex);
        sel_registerName(MetalSelectorNames.SetFragmentBufferOffsetAtIndex);
        sel_registerName(MetalSelectorNames.SetFragmentTextureAtIndex);
        sel_registerName(MetalSelectorNames.SetFragmentSamplerStateAtIndex);
        sel_registerName(MetalSelectorNames.SetCullMode);
        s_setDepthStencilState = sel_registerName(MetalSelectorNames.SetDepthStencilState);
        sel_registerName(MetalSelectorNames.DrawPrimitivesVertexStartVertexCountInstanceCount);
        sel_registerName(MetalSelectorNames.DrawIndexedPrimitivesIndexCountIndexTypeIndexBufferIndexBufferOffsetInstanceCount);
        s_endEncoding = sel_registerName(MetalSelectorNames.EndEncoding);
        sel_registerName(MetalSelectorNames.CopyFromBuffer);
        s_copyFromTexture = sel_registerName(MetalSelectorNames.CopyFromTexture);
        s_generateMipmaps = sel_registerName(MetalSelectorNames.GenerateMipmapsForTexture);
        s_nextDrawable = sel_registerName(MetalSelectorNames.NextDrawable);
        s_setLayerDevice = sel_registerName(MetalSelectorNames.SetLayerDevice);
        s_setDrawableSize = sel_registerName(MetalSelectorNames.SetDrawableSize);
        s_texture = sel_registerName(MetalSelectorNames.Texture);
        sel_registerName(MetalSelectorNames.NewTextureViewWithPixelFormat);
        sel_registerName(MetalSelectorNames.Retain);
        sel_registerName(MetalSelectorNames.Release);
        s_name = sel_registerName(MetalSelectorNames.Name);
        s_utf8String = sel_registerName(MetalSelectorNames.UTF8String);
        s_supportsFamily = sel_registerName(MetalSelectorNames.SupportsFamily);
        s_texture2DDescriptor = sel_registerName(MetalSelectorNames.Texture2DDescriptorWithPixelFormatWidthHeightMipmapped);
        s_textureCubeDescriptor = sel_registerName(MetalSelectorNames.TextureCubeDescriptorWithPixelFormatSizeMipmapped);
        s_replaceRegionSlice = sel_registerName(MetalSelectorNames.ReplaceRegionSliceMipmapLevelWithBytesBytesPerRowBytesPerImage);
        s_getBytesSlice = sel_registerName(MetalSelectorNames.GetBytesBytesPerRowFromRegionMipmapLevelSlice);
        s_setUsage = sel_registerName(MetalSelectorNames.SetUsage);
        s_setStorageMode = sel_registerName(MetalSelectorNames.SetStorageMode);
        sel_registerName(MetalSelectorNames.NewCounterSampleBufferWithDescriptorError);
        s_contents = sel_registerName(MetalSelectorNames.Contents);
        s_new = sel_registerName(MetalSelectorNames.New);
        s_drain = sel_registerName(MetalSelectorNames.Drain);
        s_setSAddress = sel_registerName(MetalSelectorNames.SetSAddressMode);
        s_setTAddress = sel_registerName(MetalSelectorNames.SetTAddressMode);
        s_setRAddress = sel_registerName(MetalSelectorNames.SetRAddressMode);
        s_setMinFilter = sel_registerName(MetalSelectorNames.SetMinFilter);
        s_setMagFilter = sel_registerName(MetalSelectorNames.SetMagFilter);
        s_setMipFilter = sel_registerName(MetalSelectorNames.SetMipFilter);
        s_mipmapLevelCount = sel_registerName(MetalSelectorNames.MipmapLevelCount);
        s_replaceRegion = sel_registerName(MetalSelectorNames.ReplaceRegionMipmapLevelWithBytesBytesPerRow);
        s_getBytes = sel_registerName(MetalSelectorNames.GetBytesBytesPerRowFromRegionMipmapLevel);
        sel_registerName(MetalSelectorNames.SetMipmapLevelCount);
        s_alloc = sel_registerName(MetalSelectorNames.Alloc);
        s_initWithUtf8 = sel_registerName(MetalSelectorNames.InitWithUTF8String);
        s_newLibraryWithSource = sel_registerName(MetalSelectorNames.NewLibraryWithSourceOptionsError);
        s_newLibraryWithData = sel_registerName(MetalSelectorNames.NewLibraryWithDataError);
        s_newFunctionWithName = sel_registerName(MetalSelectorNames.NewFunctionWithName);
        s_functionNames = sel_registerName(MetalSelectorNames.FunctionNames);
        s_count = sel_registerName(MetalSelectorNames.Count);
        s_localizedDescription = sel_registerName(MetalSelectorNames.LocalizedDescription);
        s_newRenderPipelineState = sel_registerName(MetalSelectorNames.NewRenderPipelineStateWithDescriptorError);
        s_vertexDescriptorClass = sel_registerName(MetalSelectorNames.VertexDescriptor);
        s_attributes = sel_registerName(MetalSelectorNames.Attributes);
        s_layouts = sel_registerName(MetalSelectorNames.Layouts);
        s_setAttributeFormat = sel_registerName(MetalSelectorNames.SetAttributeFormat);
        s_setAttributeOffset = sel_registerName(MetalSelectorNames.SetAttributeOffset);
        s_setAttributeBufferIndex = sel_registerName(MetalSelectorNames.SetAttributeBufferIndex);
        s_setLayoutStride = sel_registerName(MetalSelectorNames.SetLayoutStride);
        s_setLayoutStepFunction = sel_registerName(MetalSelectorNames.SetLayoutStepFunction);
        s_setLayoutStepRate = sel_registerName(MetalSelectorNames.SetLayoutStepRate);
        s_setVertexDescriptor = sel_registerName(MetalSelectorNames.SetVertexDescriptor);
        s_setVertexFunction = sel_registerName(MetalSelectorNames.SetVertexFunction);
        s_setFragmentFunction = sel_registerName(MetalSelectorNames.SetFragmentFunction);
        s_colorAttachments = sel_registerName(MetalSelectorNames.ColorAttachments);
        s_objectAtIndexedSubscript = sel_registerName(MetalSelectorNames.ObjectAtIndexedSubscript);
        s_setPixelFormat = sel_registerName(MetalSelectorNames.SetPixelFormat);
        s_setBlendingEnabled = sel_registerName(MetalSelectorNames.SetBlendingEnabled);
        s_setSourceRgbBlend = sel_registerName(MetalSelectorNames.SetSourceRGBBlendFactor);
        s_setDestinationRgbBlend = sel_registerName(MetalSelectorNames.SetDestinationRGBBlendFactor);
        s_setRgbBlendOp = sel_registerName(MetalSelectorNames.SetRgbBlendOperation);
        s_setSourceAlphaBlend = sel_registerName(MetalSelectorNames.SetSourceAlphaBlendFactor);
        s_setDestinationAlphaBlend = sel_registerName(MetalSelectorNames.SetDestinationAlphaBlendFactor);
        s_setAlphaBlendOp = sel_registerName(MetalSelectorNames.SetAlphaBlendOperation);
        s_setWriteMask = sel_registerName(MetalSelectorNames.SetWriteMask);
        s_setDepthAttachmentPixelFormat = sel_registerName(MetalSelectorNames.SetDepthAttachmentPixelFormat);
        s_setStencilAttachmentPixelFormat = sel_registerName(MetalSelectorNames.SetStencilAttachmentPixelFormat);
        s_renderPassDescriptor = sel_registerName(MetalSelectorNames.RenderPassDescriptor);
        s_depthAttachment = sel_registerName(MetalSelectorNames.DepthAttachment);
        s_stencilAttachment = sel_registerName(MetalSelectorNames.StencilAttachment);
        s_setTexture = sel_registerName(MetalSelectorNames.SetTexture);
        s_setLoadAction = sel_registerName(MetalSelectorNames.SetLoadAction);
        s_setStoreAction = sel_registerName(MetalSelectorNames.SetStoreAction);
        s_setClearColor = sel_registerName(MetalSelectorNames.SetClearColor);
        s_setClearDepth = sel_registerName(MetalSelectorNames.SetClearDepth);
        s_setLevel = sel_registerName(MetalSelectorNames.SetLevel);
        s_renderEncoder = sel_registerName(MetalSelectorNames.RenderCommandEncoderWithDescriptor);
        s_setPipeline = sel_registerName(MetalSelectorNames.SetRenderPipelineState);
        s_setViewport = sel_registerName(MetalSelectorNames.SetViewport);
        s_setScissorRect = sel_registerName(MetalSelectorNames.SetScissorRect);
        s_setVertexBuffer = sel_registerName(MetalSelectorNames.SetVertexBufferOffsetAtIndex);
        s_setFragmentBuffer = sel_registerName(MetalSelectorNames.SetFragmentBufferOffsetAtIndex);
        s_setVertexBytes = sel_registerName(MetalSelectorNames.SetVertexBytesLengthAtIndex);
        s_setFragmentBytes = sel_registerName(MetalSelectorNames.SetFragmentBytesLengthAtIndex);
        s_setFragmentTexture = sel_registerName(MetalSelectorNames.SetFragmentTextureAtIndex);
        s_setVertexTexture = sel_registerName(MetalSelectorNames.SetVertexTextureAtIndex);
        s_setFragmentSampler = sel_registerName(MetalSelectorNames.SetFragmentSamplerStateAtIndex);
        s_setCullMode = sel_registerName(MetalSelectorNames.SetCullMode);
        s_setDepthCompareFunction = sel_registerName(MetalSelectorNames.SetDepthCompareFunction);
        s_setDepthWriteEnabled = sel_registerName(MetalSelectorNames.SetDepthWriteEnabled);
        s_setFrontFacing = sel_registerName(MetalSelectorNames.SetFrontFacingWinding);
        s_drawPrimitives = sel_registerName(MetalSelectorNames.DrawPrimitivesVertexStartVertexCountInstanceCount);
        s_drawIndexed = sel_registerName(MetalSelectorNames.DrawIndexedPrimitivesIndexCountIndexTypeIndexBufferIndexBufferOffsetInstanceCount);
    }

    internal static nint CreateSystemDefaultDevice() => MTLCreateSystemDefaultDevice();

    internal static nint NewCommandQueue(nint device) => objc_msgSend(device, s_newCommandQueue);

    internal static nint NewCommandBuffer(nint queue) => objc_msgSend(queue, s_commandBuffer);

    // The managed window/render loops do not provide AppKit's per-event pool around
    // Metal work. Scope factory-created command buffers, encoders and descriptors to
    // each native operation instead of retaining them in a long-lived outer pool.
    internal static AutoreleasePool CreateAutoreleasePool()
    {
        // Resolve after Metal.framework has loaded Foundation. Concurrent first calls
        // may resolve the same immutable class pointer; native-sized stores are atomic.
        nint poolClass = s_autoreleasePoolClass;
        if (poolClass == 0)
            s_autoreleasePoolClass = poolClass = objc_getClass("NSAutoreleasePool");
        nint pool = objc_msgSend(poolClass, s_new);
        if (pool == 0)
            throw new InvalidOperationException("NSAutoreleasePool creation failed.");
        return new AutoreleasePool(pool);
    }

    internal readonly struct AutoreleasePool(nint pool) : IDisposable
    {
        // A pool must be drained on the creating thread, after owned references have
        // been retained and GPU work committed. This value stays in a local using.
        public void Dispose() => objc_msgSend(pool, s_drain);
    }

    internal static void PresentDrawable(nint commandBuffer, nint drawable) =>
        objc_msgSend_nint(commandBuffer, s_presentDrawable, drawable);

    internal static void Commit(nint commandBuffer) => objc_msgSend(commandBuffer, s_commit);

    internal static void WaitUntilCompleted(nint commandBuffer) =>
        objc_msgSend(commandBuffer, s_waitUntilCompleted);

    internal static nint NextDrawable(nint metalLayer) => objc_msgSend(metalLayer, s_nextDrawable);

    internal static void SetLayerDevice(nint metalLayer, nint device) =>
        objc_msgSend_nint(metalLayer, s_setLayerDevice, device);

    /// <summary>CGSize is a 2×double HFA — passed by value (arm64 SIMD register ABI).</summary>
    internal static void SetLayerDrawableSize(nint metalLayer, double width, double height) =>
        objc_msgSend_size(metalLayer, s_setDrawableSize, width, height);

    internal static nint DrawableTexture(nint drawable) => objc_msgSend(drawable, s_texture);

    internal static nint Retain(nint value)
    {
        if (value == 0)
            return 0;
        return objc_retain(value);
    }

    internal static void Release(nint value)
    {
        if (value == 0)
            return;
        objc_release(value);
    }

    internal static string? GetDeviceName(nint device)
    {
        nint ns = objc_msgSend(device, s_name);
        if (ns == 0)
            return null;
        nint utf8 = objc_msgSend(ns, s_utf8String);
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>
    /// <c>MTLDevice supportsFamily:</c>. ASTC LDR requires Apple GPU family 2 or newer.
    /// </summary>
    internal static bool SupportsFamily(nint device, MTLGPUFamily family)
    {
        if (device == 0)
            return false;
        return objc_msgSend_nint(device, s_supportsFamily, (nint)(long)family) != 0;
    }

    internal static nint NewScratchTexture(nint device, int width, int height)
    {
        nint cls = objc_getClass("MTLTextureDescriptor");
        if (cls == 0)
            return 0;

        nint descriptor = objc_msgSend_texture2d(
            cls,
            s_texture2DDescriptor,
            (nuint)MTLPixelFormat.BGRA8Unorm,
            (nuint)Math.Max(1, width),
            (nuint)Math.Max(1, height),
            0);
        if (descriptor == 0)
            return 0;

        objc_msgSend_nuint(descriptor, s_setUsage, (nuint)(MTLTextureUsage.ShaderRead | MTLTextureUsage.RenderTarget));
        objc_msgSend_nuint(descriptor, s_setStorageMode, (nuint)MTLStorageMode.Shared);

        nint texture = objc_msgSend_nint(device, s_newTextureWithDescriptor, descriptor);
        return texture;
    }

    internal static nint NewBuffer(nint device, nuint length, nuint options) =>
        objc_msgSend_nuint2(device, s_newBuffer, length, options);

    internal static nint BufferContents(nint buffer) => objc_msgSend(buffer, s_contents);

    internal static nint NewTexture2D(
        nint device,
        MTLPixelFormat format,
        uint width,
        uint height,
        bool mipmapped,
        MTLTextureUsage usage,
        MTLStorageMode storage)
    {
        nint cls = objc_getClass("MTLTextureDescriptor");
        if (cls == 0)
            return 0;

        nint descriptor = objc_msgSend_texture2d(
            cls,
            s_texture2DDescriptor,
            (nuint)format,
            Math.Max(1u, width),
            Math.Max(1u, height),
            mipmapped ? (byte)1 : (byte)0);
        if (descriptor == 0)
            return 0;

        objc_msgSend_nuint(descriptor, s_setUsage, (nuint)usage);
        objc_msgSend_nuint(descriptor, s_setStorageMode, (nuint)storage);
        return objc_msgSend_nint(device, s_newTextureWithDescriptor, descriptor);
    }

    internal static nuint GetMipmapLevelCount(nint texture) => (nuint)objc_msgSend(texture, s_mipmapLevelCount);

    /// <summary>
    /// Creates a 2D-array-free cube texture via
    /// <c>[MTLTextureDescriptor textureCubeDescriptorWithPixelFormat:size:mipmapped:]</c>.
    /// Storage mode is applied post-hoc (the convenience constructor fixes usage, not storage).
    /// </summary>
    internal static nint NewTextureCube(
        nint device,
        MTLPixelFormat format,
        uint size,
        bool mipmapped,
        MTLTextureUsage usage,
        MTLStorageMode storage)
    {
        nint cls = objc_getClass("MTLTextureDescriptor");
        if (cls == 0)
            return 0;

        nint descriptor = objc_msgSend_nuint2_byte(
            cls,
            s_textureCubeDescriptor,
            (nuint)format,
            Math.Max(1u, size),
            mipmapped ? (byte)1 : (byte)0);
        if (descriptor == 0)
            return 0;

        objc_msgSend_nuint(descriptor, s_setUsage, (nuint)usage);
        objc_msgSend_nuint(descriptor, s_setStorageMode, (nuint)storage);
        return objc_msgSend_nint(device, s_newTextureWithDescriptor, descriptor);
    }

    /// <summary>Cube-face upload: <c>replaceRegion:slice:mipmapLevel:withBytes:bytesPerRow:bytesPerImage:</c>
    /// where slice = face index and bytesPerImage equals bytesPerRow for a single 2D face.</summary>
    internal static void ReplaceRegionCube(
        nint texture,
        uint face,
        uint mip,
        uint width,
        uint height,
        nint bytes,
        nuint bytesPerRow)
    {
        MTLRegion region = new()
        {
            Origin = new MTLOrigin { X = 0, Y = 0, Z = 0 },
            Size = new MTLSize { Width = width, Height = height, Depth = 1 },
        };
        nuint bytesPerImage = checked(bytesPerRow * (nuint)height);
        if (s_integerStructsByValue)
            objc_msgSend_replaceRegionSlice_x64(texture, s_replaceRegionSlice, region, mip, face, bytes, bytesPerRow, bytesPerImage);
        else
            objc_msgSend_replaceRegionSlice(texture, s_replaceRegionSlice, ref region, mip, face, bytes, bytesPerRow, bytesPerImage);
    }

    /// <summary>Cube-face read-back: <c>getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:</c>.</summary>
    internal static void GetBytesCube(
        nint texture,
        uint face,
        uint mip,
        uint width,
        uint height,
        nint destination,
        nuint bytesPerRow)
    {
        MTLRegion region = new()
        {
            Origin = new MTLOrigin { X = 0, Y = 0, Z = 0 },
            Size = new MTLSize { Width = width, Height = height, Depth = 1 },
        };
        nuint bytesPerImage = checked(bytesPerRow * (nuint)height);
        if (s_integerStructsByValue)
            objc_msgSend_getBytesSlice_x64(texture, s_getBytesSlice, destination, bytesPerRow, bytesPerImage, region, mip, face);
        else
            objc_msgSend_getBytesSlice(texture, s_getBytesSlice, destination, bytesPerRow, bytesPerImage, ref region, mip, face);
    }

    internal static void ReplaceRegion(
        nint texture,
        uint mip,
        uint x,
        uint y,
        uint width,
        uint height,
        nint bytes,
        nuint bytesPerRow)
    {
        MTLRegion region = new()
        {
            Origin = new MTLOrigin { X = x, Y = y, Z = 0 },
            Size = new MTLSize { Width = width, Height = height, Depth = 1 },
        };
        if (s_integerStructsByValue)
            objc_msgSend_replaceRegion_x64(texture, s_replaceRegion, region, mip, bytes, bytesPerRow);
        else
            objc_msgSend_replaceRegion(texture, s_replaceRegion, ref region, mip, bytes, bytesPerRow);
    }

    internal static void GetBytes(
        nint texture,
        uint mip,
        uint x,
        uint y,
        uint width,
        uint height,
        nint destination,
        nuint bytesPerRow)
    {
        MTLRegion region = new()
        {
            Origin = new MTLOrigin { X = x, Y = y, Z = 0 },
            Size = new MTLSize { Width = width, Height = height, Depth = 1 },
        };
        if (s_integerStructsByValue)
            objc_msgSend_getBytes_x64(texture, s_getBytes, destination, bytesPerRow, region, mip);
        else
            objc_msgSend_getBytes(texture, s_getBytes, destination, bytesPerRow, ref region, mip);
    }

    internal static nint BlitCommandEncoder(nint commandBuffer) => objc_msgSend(commandBuffer, s_blitEncoder);

    internal static void EndEncoding(nint encoder) => objc_msgSend(encoder, s_endEncoding);

    internal static void GenerateMipmaps(nint blitEncoder, nint texture) =>
        objc_msgSend_nint(blitEncoder, s_generateMipmaps, texture);

    internal static void CopyTextureToTexture(
        nint blitEncoder,
        nint source,
        nint destination,
        uint width,
        uint height)
        => CopyTextureToTexture(blitEncoder, source, destination, 0, 0, 0, 0, width, height, 0, 0);

    internal static void CopyTextureToTexture(
        nint blitEncoder,
        nint source,
        nint destination,
        uint srcX,
        uint srcY,
        uint dstX,
        uint dstY,
        uint width,
        uint height,
        uint sourceLevel,
        uint destinationLevel)
    {
        MTLOrigin origin = new() { X = srcX, Y = srcY, Z = 0 };
        MTLSize size = new() { Width = width, Height = height, Depth = 1 };
        MTLOrigin destOrigin = new() { X = dstX, Y = dstY, Z = 0 };
        if (s_integerStructsByValue)
            objc_msgSend_copyTexture_x64(blitEncoder, s_copyFromTexture, source, 0, sourceLevel,
                origin, size, destination, 0, destinationLevel, destOrigin);
        else
            objc_msgSend_copyTexture(blitEncoder, s_copyFromTexture, source, 0, sourceLevel,
                ref origin, ref size, destination, 0, destinationLevel, ref destOrigin);
    }

    internal static nint NewSamplerState(
        nint device,
        MTLSamplerAddressMode addressS,
        MTLSamplerAddressMode addressT,
        MTLSamplerAddressMode addressR,
        MTLSamplerMinMagFilter min,
        MTLSamplerMinMagFilter mag,
        MTLSamplerMipFilter mip)
    {
        nint cls = objc_getClass("MTLSamplerDescriptor");
        if (cls == 0)
            return 0;
        nint descriptor = objc_msgSend(cls, s_new);
        if (descriptor == 0)
            return 0;

        objc_msgSend_nuint(descriptor, s_setSAddress, (nuint)addressS);
        objc_msgSend_nuint(descriptor, s_setTAddress, (nuint)addressT);
        objc_msgSend_nuint(descriptor, s_setRAddress, (nuint)addressR);
        objc_msgSend_nuint(descriptor, s_setMinFilter, (nuint)min);
        objc_msgSend_nuint(descriptor, s_setMagFilter, (nuint)mag);
        objc_msgSend_nuint(descriptor, s_setMipFilter, (nuint)mip);
        nint sampler = objc_msgSend_nint(device, s_newSampler, descriptor);
        Release(descriptor);
        return sampler;
    }

    internal static nint NewLibraryFromSource(nint device, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        nint nsSource = CreateNSString(source);
        if (nsSource == 0)
            throw new InvalidOperationException("Failed to create an NSString for Metal library source.");

        nint error = 0;
        nint library;
        try
        {
            library = objc_msgSend_nint3(device, s_newLibraryWithSource, nsSource, 0, (nint)(&error));
        }
        finally
        {
            Release(nsSource);
        }

        if (library == 0)
        {
            string detail = ReadNsError(error);
            throw new InvalidOperationException(
                string.IsNullOrEmpty(detail)
                    ? "MTLDevice newLibraryWithSource returned nil."
                    : $"MTLDevice newLibraryWithSource failed: {detail}");
        }

        return library;
    }

    /// <summary>
    /// Create an MTLLibrary from prebuilt metallib bytes (the shipping artifact format). The
    /// bytes are wrapped in a dispatch_data_t for <c>newLibraryWithData:error:</c>; MTLLibrary
    /// copies what it needs during the call, so the managed pin only has to span this call.
    /// </summary>
    internal static nint NewLibraryWithData(nint device, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
            throw new ArgumentException("Metallib payload is empty.", nameof(data));

        nint dispatchData;
        nint error = 0;
        nint library;
        fixed (byte* ptr = data)
        {
            // DISPATCH_DATA_DESTRUCTOR_DEFAULT (null) — no free() on release.
            dispatchData = dispatch_data_create(ptr, (nuint)data.Length, 0, 0);
            if (dispatchData == 0)
                throw new InvalidOperationException("dispatch_data_create returned nil for a metallib payload.");
            try
            {
                library = objc_msgSend_nint2(device, s_newLibraryWithData, dispatchData, (nint)(&error));
            }
            finally
            {
                dispatch_release(dispatchData);
            }
        }

        if (library == 0)
        {
            string detail = ReadNsError(error);
            throw new InvalidOperationException(
                string.IsNullOrEmpty(detail)
                    ? "MTLDevice newLibraryWithData returned nil."
                    : $"MTLDevice newLibraryWithData failed: {detail}");
        }

        return library;
    }

    /// <summary>
    /// All function names declared by the library, used to resolve slang's mangled entry-point
    /// name (e.g. <c>main0</c>) when no candidate matches.
    /// </summary>
    internal static string[] GetLibraryFunctionNames(nint library)
    {
        nint names = objc_msgSend(library, s_functionNames);
        if (names == 0)
            return [];
        nuint count = (nuint)objc_msgSend(names, s_count);
        if (count == 0)
            return [];
        var result = new string[(int)Math.Min(count, int.MaxValue)];
        for (nuint i = 0; i < count; i++)
        {
            nint nsName = objc_msgSend_nuint(names, s_objectAtIndexedSubscript, i);
            if (nsName == 0)
                continue;
            nint utf8 = objc_msgSend(nsName, s_utf8String);
            result[i] = utf8 == 0 ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        }
        return result;
    }

    internal static nint NewFunction(nint library, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        nint nsName = CreateNSString(name);
        if (nsName == 0)
            throw new InvalidOperationException("Failed to create an NSString for a Metal function name.");
        try
        {
            nint function = objc_msgSend_nint(library, s_newFunctionWithName, nsName);
            if (function == 0)
                throw new InvalidOperationException($"MTLLibrary newFunctionWithName returned nil for '{name}'.");
            return function;
        }
        finally
        {
            Release(nsName);
        }
    }

    /// <summary>
    /// MTLVertexDescriptor describing interleaved mesh and optional instance buffers, with
    /// attributes placed at their SHADER-reflected <c>[[attribute(location)]]</c> slots. The
    /// caller maps engine semantics to locations and skips unconsumed elements.
    /// </summary>
    internal static nint NewVertexDescriptor(
        MTLVertexFormat[] formats,
        ushort[] offsets,
        IReadOnlyList<uint> locations,
        IReadOnlyList<uint> bufferIndices,
        nuint vertexStride,
        nuint instanceStride)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(bufferIndices);
        if (formats.Length != offsets.Length || formats.Length != locations.Count ||
            formats.Length != bufferIndices.Count || formats.Length == 0)
            throw new ArgumentException("Vertex attribute formats, offsets, locations, and buffer indices must be non-empty and equal length.");

        nint cls = objc_getClass("MTLVertexDescriptor");
        if (cls == 0)
            throw new InvalidOperationException("objc_getClass(MTLVertexDescriptor) returned nil.");
        nint descriptor = objc_msgSend(cls, s_vertexDescriptorClass);
        if (descriptor == 0)
            throw new InvalidOperationException("MTLVertexDescriptor vertexDescriptor returned nil.");
        nint attributes = objc_msgSend(descriptor, s_attributes);
        for (int i = 0; i < formats.Length; i++)
        {
            // The attribute ARRAY INDEX is the shader location ([[attribute(n)]]): place each
            // entry at its reflected slot rather than packing them sequentially.
            nint attribute = objc_msgSend_nuint(attributes, s_objectAtIndexedSubscript, locations[i]);
            if (attribute == 0)
                throw new InvalidOperationException($"MTLVertexAttributeDescriptor {locations[i]} is nil.");
            objc_msgSend_nuint(attribute, s_setAttributeFormat, (nuint)formats[i]);
            objc_msgSend_nuint(attribute, s_setAttributeOffset, offsets[i]);
            objc_msgSend_nuint(attribute, s_setAttributeBufferIndex, bufferIndices[i]);
        }

        nint layouts = objc_msgSend(descriptor, s_layouts);
        SetLayout(
            layouts,
            MetalCommandTranslator.MetalVertexDataBufferIndex,
            vertexStride,
            MTLVertexStepFunction.PerVertex);
        if (instanceStride > 0)
        {
            SetLayout(
                layouts,
                MetalCommandTranslator.MetalInstanceDataBufferIndex,
                instanceStride,
                MTLVertexStepFunction.PerInstance);
        }
        // vertexDescriptor is an autoreleased factory result, but the device caches this
        // object across frames and releases it only when the device is disposed. Hold an
        // owned reference so an iOS autorelease-pool drain cannot recycle it before the
        // later render-pipeline validation reads its layouts.
        Retain(descriptor);
        return descriptor;

        static void SetLayout(nint layouts, uint index, nuint stride, MTLVertexStepFunction stepFunction)
        {
            nint layout = objc_msgSend_nuint(layouts, s_objectAtIndexedSubscript, index);
            if (layout == 0)
                throw new InvalidOperationException($"MTLVertexBufferLayoutDescriptor {index} is nil.");
            objc_msgSend_nuint(layout, s_setLayoutStride, stride);
            objc_msgSend_nuint(layout, s_setLayoutStepFunction, (nuint)stepFunction);
            objc_msgSend_nuint(layout, s_setLayoutStepRate, 1);
        }
    }

    internal static nint NewRenderPipelineState(
        nint device,
        nint vertexFunction,
        nint fragmentFunction,
        in RasterizerState raster,
        MTLPixelFormat colorFormat,
        MTLPixelFormat depthFormat,
        nint vertexDescriptor = 0)
    {
        if (vertexFunction == 0 || fragmentFunction == 0)
            throw new ArgumentOutOfRangeException(vertexFunction == 0 ? nameof(vertexFunction) : nameof(fragmentFunction));

        nint cls = objc_getClass("MTLRenderPipelineDescriptor");
        if (cls == 0)
            throw new InvalidOperationException("objc_getClass(MTLRenderPipelineDescriptor) returned nil.");
        nint descriptor = objc_msgSend(cls, s_new);
        if (descriptor == 0)
            throw new InvalidOperationException("MTLRenderPipelineDescriptor new returned nil.");

        try
        {
            objc_msgSend_nint(descriptor, s_setVertexFunction, vertexFunction);
            objc_msgSend_nint(descriptor, s_setFragmentFunction, fragmentFunction);
            if (vertexDescriptor != 0)
                objc_msgSend_nint(descriptor, s_setVertexDescriptor, vertexDescriptor);

            nint attachments = objc_msgSend(descriptor, s_colorAttachments);
            nint color = objc_msgSend_nuint(attachments, s_objectAtIndexedSubscript, 0);
            if (color == 0)
                throw new InvalidOperationException("MTLRenderPipelineColorAttachmentDescriptor 0 is nil.");
            objc_msgSend_nuint(color, s_setPixelFormat, (nuint)colorFormat);
            objc_msgSend_byte(color, s_setBlendingEnabled, raster.DoBlend ? (byte)1 : (byte)0);
            if (raster.DoBlend)
            {
                objc_msgSend_nuint(color, s_setSourceRgbBlend, (nuint)MetalFormats.ToBlendFactor(raster.BlendSrc));
                objc_msgSend_nuint(color, s_setDestinationRgbBlend, (nuint)MetalFormats.ToBlendFactor(raster.BlendDst));
                objc_msgSend_nuint(color, s_setRgbBlendOp, (nuint)MetalFormats.ToBlendOp(raster.Blend));
                objc_msgSend_nuint(color, s_setSourceAlphaBlend, (nuint)MetalFormats.ToBlendFactor(raster.BlendSrc));
                objc_msgSend_nuint(color, s_setDestinationAlphaBlend, (nuint)MetalFormats.ToBlendFactor(raster.BlendDst));
                objc_msgSend_nuint(color, s_setAlphaBlendOp, (nuint)MetalFormats.ToBlendOp(raster.Blend));
            }

            // Metal's bit order is the reverse of Vulkan/D3D/WebGPU: R=8, G=4, B=2, A=1.
            RasterizerState.ColorChannels channels = raster.ColorMask;
            nuint writeMask = ((channels & RasterizerState.ColorChannels.Red) != 0 ? 8u : 0u)
                | ((channels & RasterizerState.ColorChannels.Green) != 0 ? 4u : 0u)
                | ((channels & RasterizerState.ColorChannels.Blue) != 0 ? 2u : 0u)
                | ((channels & RasterizerState.ColorChannels.Alpha) != 0 ? 1u : 0u);
            objc_msgSend_nuint(color, s_setWriteMask, writeMask);

            if (depthFormat != MTLPixelFormat.Invalid)
            {
                objc_msgSend_nuint(descriptor, s_setDepthAttachmentPixelFormat, (nuint)depthFormat);
                if (MetalFormats.HasStencil(depthFormat))
                    objc_msgSend_nuint(descriptor, s_setStencilAttachmentPixelFormat, (nuint)depthFormat);
            }

            nint error = 0;
            nint pipeline = objc_msgSend_nint2(device, s_newRenderPipelineState, descriptor, (nint)(&error));
            if (pipeline == 0)
            {
                string detail = ReadNsError(error);
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(detail)
                        ? "MTLDevice newRenderPipelineStateWithDescriptor returned nil."
                        : $"MTLDevice newRenderPipelineStateWithDescriptor failed: {detail}");
            }

            return pipeline;
        }
        finally
        {
            Release(descriptor);
        }
    }

    internal static nint NewDepthStencilState(
        nint device,
        MTLCompareFunction depthCompareFunction,
        bool depthWriteEnabled)
    {
        nint cls = objc_getClass("MTLDepthStencilDescriptor");
        if (cls == 0)
            throw new InvalidOperationException("objc_getClass(MTLDepthStencilDescriptor) returned nil.");
        nint descriptor = objc_msgSend(cls, s_new);
        if (descriptor == 0)
            throw new InvalidOperationException("MTLDepthStencilDescriptor new returned nil.");

        try
        {
            objc_msgSend_nuint(descriptor, s_setDepthCompareFunction, (nuint)depthCompareFunction);
            objc_msgSend_byte(descriptor, s_setDepthWriteEnabled, depthWriteEnabled ? (byte)1 : (byte)0);
            nint state = objc_msgSend_nint(device, s_newDepthStencilState, descriptor);
            if (state == 0)
                throw new InvalidOperationException("MTLDevice newDepthStencilStateWithDescriptor returned nil.");
            return state;
        }
        finally
        {
            Release(descriptor);
        }
    }

    private static nint CreateNSString(string value)
    {
        nint utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            nint cls = objc_getClass("NSString");
            if (cls == 0)
                return 0;
            nint allocated = objc_msgSend(cls, s_alloc);
            if (allocated == 0)
                return 0;
            return objc_msgSend_nint(allocated, s_initWithUtf8, utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static string ReadNsError(nint error)
    {
        if (error == 0)
            return "";
        nint description = objc_msgSend(error, s_localizedDescription);
        if (description == 0)
            return "";
        nint utf8 = objc_msgSend(description, s_utf8String);
        return utf8 == 0 ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
    }

    internal static nint NewRenderPassDescriptor()
    {
        nint cls = objc_getClass("MTLRenderPassDescriptor");
        if (cls == 0)
            return 0;
        return objc_msgSend(cls, s_new);
    }

    internal static nint ColorAttachment(nint descriptor, int index)
    {
        nint attachments = objc_msgSend(descriptor, s_colorAttachments);
        return objc_msgSend_nuint(attachments, s_objectAtIndexedSubscript, (nuint)index);
    }

    internal static nint DepthAttachment(nint descriptor) => objc_msgSend(descriptor, s_depthAttachment);

    internal static nint StencilAttachment(nint descriptor) => objc_msgSend(descriptor, s_stencilAttachment);

    internal static void SetAttachmentTexture(nint attachment, nint texture, uint mip)
    {
        objc_msgSend_nint(attachment, s_setTexture, texture);
        objc_msgSend_nuint(attachment, s_setLevel, mip);
    }

    internal static void SetAttachmentLoadStore(
        nint attachment,
        MTLLoadAction load,
        MTLStoreAction store)
    {
        objc_msgSend_nuint(attachment, s_setLoadAction, (nuint)load);
        objc_msgSend_nuint(attachment, s_setStoreAction, (nuint)store);
    }

    internal static void SetClearColor(nint attachment, float r, float g, float b, float a)
    {
        MTLClearColor color = new() { Red = r, Green = g, Blue = b, Alpha = a };
        objc_msgSend_clearColor(attachment, s_setClearColor, color);
    }

    internal static void SetClearDepth(nint attachment, float depth) =>
        objc_msgSend_f64(attachment, s_setClearDepth, depth);

    internal static nint RenderCommandEncoder(nint commandBuffer, nint descriptor) =>
        objc_msgSend_nint(commandBuffer, s_renderEncoder, descriptor);

    internal static void SetRenderPipelineState(nint encoder, nint pipeline) =>
        objc_msgSend_nint(encoder, s_setPipeline, pipeline);

    internal static void SetViewport(nint encoder, float x, float y, float width, float height)
    {
        MTLViewport viewport = new()
        {
            OriginX = x,
            OriginY = y,
            Width = Math.Max(1f, width),
            Height = Math.Max(1f, height),
            ZNear = 0,
            ZFar = 1,
        };
        objc_msgSend_viewport(encoder, s_setViewport, viewport);
    }

    internal static void SetScissorRect(nint encoder, uint x, uint y, uint width, uint height)
    {
        MTLScissorRect rect = new()
        {
            X = x,
            Y = y,
            Width = Math.Max(1u, width),
            Height = Math.Max(1u, height),
        };
        if (s_integerStructsByValue)
            objc_msgSend_scissor_x64(encoder, s_setScissorRect, rect);
        else
            objc_msgSend_scissor(encoder, s_setScissorRect, ref rect);
    }

    internal static void SetCullMode(nint encoder, MTLCullMode mode) =>
        objc_msgSend_nuint(encoder, s_setCullMode, (nuint)mode);

    internal static void SetDepthStencilState(nint encoder, nint state) =>
        objc_msgSend_nint(encoder, s_setDepthStencilState, state);

    internal static void SetFrontFacing(nint encoder, MTLWinding winding) =>
        objc_msgSend_nuint(encoder, s_setFrontFacing, (nuint)winding);

    internal static void SetVertexBuffer(nint encoder, nint buffer, nuint offset, nuint index) =>
        objc_msgSend_bufferAt(encoder, s_setVertexBuffer, buffer, offset, index);

    internal static void SetFragmentBuffer(nint encoder, nint buffer, nuint offset, nuint index) =>
        objc_msgSend_bufferAt(encoder, s_setFragmentBuffer, buffer, offset, index);

    internal static void SetVertexBytes(nint encoder, ReadOnlySpan<byte> bytes, nuint index)
    {
        if (bytes.IsEmpty)
            return;
        fixed (byte* data = bytes)
            objc_msgSend_bufferAt(encoder, s_setVertexBytes, (nint)data, (nuint)bytes.Length, index);
    }

    internal static void SetFragmentBytes(nint encoder, ReadOnlySpan<byte> bytes, nuint index)
    {
        if (bytes.IsEmpty)
            return;
        fixed (byte* data = bytes)
            objc_msgSend_bufferAt(encoder, s_setFragmentBytes, (nint)data, (nuint)bytes.Length, index);
    }

    internal static void SetFragmentTexture(nint encoder, nint texture, nuint index) =>
        objc_msgSend_nuint2(encoder, s_setFragmentTexture, (nuint)texture, index);

    internal static void SetVertexTexture(nint encoder, nint texture, nuint index) =>
        objc_msgSend_nuint2(encoder, s_setVertexTexture, (nuint)texture, index);

    internal static void SetFragmentSampler(nint encoder, nint sampler, nuint index) =>
        objc_msgSend_nuint2(encoder, s_setFragmentSampler, (nuint)sampler, index);

    internal static void DrawPrimitives(nint encoder, MTLPrimitiveType type, uint start, uint count, uint instances) =>
        objc_msgSend_drawPrim(encoder, s_drawPrimitives, (nuint)type, start, count, Math.Max(1u, instances));

    internal static void DrawIndexed(
        nint encoder,
        MTLPrimitiveType type,
        uint indexCount,
        MTLIndexType indexType,
        nint indexBuffer,
        nuint indexOffset,
        uint instances)
        => objc_msgSend_drawIndexed(
            encoder,
            s_drawIndexed,
            (nuint)type,
            indexCount,
            (nuint)indexType,
            indexBuffer,
            indexOffset,
            Math.Max(1u, instances));
}
