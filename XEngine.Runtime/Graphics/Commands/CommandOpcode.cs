// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>
/// Identifies every kind of command that can be recorded into a <see cref="CommandBuffer"/>.
///
/// The executor switches on this value to decide how to decode the payload that follows
/// the header. Adding a new opcode requires:
///   1. An entry here.
///   2. A writer method on <see cref="CommandBuffer"/>.
///   3. A case in the active backend command translator.
///
/// Numbering is stable for the lifetime of the process; the wire format is in-memory only,
/// so changing values doesn't break anything as long as encoder and executor agree.
/// </summary>
internal enum CommandOpcode : ushort
{
    // Render target / viewport / clear
    SetRenderTarget = 1,
    SetRenderTargets,
    SetViewport,
    SetScissor,
    DisableScissor,
    ClearRenderTarget,
    BlitFramebuffer,

    // Pipeline state
    SetRasterState,
    SetShader,

    // Property binding (sticky on the executor)
    SetProperties,
    SetMaterialProperties,
    ClearProperties,
    SetInstanceProperties,
    ClearInstanceProperties,

    // Global property mutation, ordered against draws (the static PropertyState
    // dictionaries are mutated at EXECUTE time so mid-CB set/clear cycles work
    // AND so writes from main-thread setters can't race with render-thread reads).
    SetGlobalTexture,
    ClearGlobalTexture,
    SetGlobalInt,
    SetGlobalFloat,
    SetGlobalVec2,
    SetGlobalVec3,
    SetGlobalVec4,
    SetGlobalColor,
    SetGlobalMatrix,
    SetGlobalMatrices,
    SetGlobalBuffer,
    SetGlobalTexture3D,
    SetGlobalTextureCube,
    ClearAllGlobals,

    // Per-uniform sugar (applied immediately against the bound shader)
    SetUniformFloat,
    SetUniformInt,
    SetUniformVec2,
    SetUniformVec3,
    SetUniformVec4,
    SetUniformMatrix,
    SetUniformMatrixArray,
    SetUniformTexture,
    SetUniformBuffer,

    // Resource data uploads
    UpdateBuffer,
    UpdateTexture,
    GenerateMipmap,

    // Draws
    DrawIndexed,
    DrawIndexedInstanced,
    DrawArrays,

    // Compute / indirect. Append-only: unknown opcodes throw in every backend skip table.
    SetComputeShader,
    DispatchCompute,
    DispatchIndirect,
    DrawIndexedIndirect,
    ResourceBarrier,

    // (DrawMesh / DrawMeshInstanced / Blit are encoder sugar on CommandBuffer that
    //  expand inline to lower-level opcodes there's no executor case for them.)

    // Resource lifecycle. Constructors encode these into a one-op CB. The CPU
    // wrapper is allocated synchronously; the GL Handle is filled in when the
    // executor runs the opcode. Submit order guarantees later ops see a valid handle.
    CreateBuffer,
    DisposeBuffer,
    CreateTexture,
    AllocateTexture2D,
    AllocateTexture3D,
    AllocateTextureCubeFace,
    UpdateTexture3D,
    SetTextureWrap,
    SetTextureFiltersOp,
    SetTextureCompareMode,
    GetTextureData,
    GetTextureDataPtr,
    GetTextureCubeFaceData,
    DisposeTexture,
    CreateVertexArrayOp,
    DisposeVertexArray,
    CreateFramebufferOp,
    DisposeFramebuffer,
    CompileShader,
    DisposeShader,

    // Debug markers
    BeginSample,
    EndSample,

    /// <summary>
    /// Resolve a multisample color texture into a same-size single-sample texture
    /// (D3D12 <c>ResolveSubresource</c> / Vulkan <c>vkCmdResolveImage</c>). Backends without
    /// multisample support report a max sample count of 1 and therefore never see this opcode.
    /// </summary>
    ResolveTexture,

    /// <summary>★ B7b — begin an ANY_SAMPLES_PASSED-style occlusion query (operand: ulong query
    /// id). Recorded around a draw whose only purpose is a depth visibility test; backends
    /// without occlusion support skip the pair and never publish a result for the id. Append
    /// only: never renumber, every backend's skip table knows the operand size (8 bytes).</summary>
    BeginOcclusionQuery,
    EndOcclusionQuery,
}
