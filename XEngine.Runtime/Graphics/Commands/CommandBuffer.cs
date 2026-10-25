// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Records a sequence of GPU commands without touching the GL context. Submit the
/// buffer through <see cref="Graphics.Submit"/> to enqueue it for the render thread.
/// Buffers are pooled; the typical pattern is:
///
/// <code>
///   using var cmd = Graphics.GetCommandBuffer("My Pass");
///   cmd.SetRenderTarget(rt);
///   cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, Color.Black);
///   cmd.DrawMesh(mesh, material);
///   Graphics.Submit(cmd);
/// </code>
///
/// <para>Single-producer: only one thread encodes a given buffer. CBs execute on the
/// render thread in submit order, so an upload CB can be safely followed by a CB
/// that uses the uploaded data.</para>
///
/// <para><see cref="SetProperties"/>, <see cref="SetInstanceProperties"/>, and
/// <see cref="SetMaterialProperties"/> all snapshot the PropertyState at encode
/// time, so the caller is free to mutate the original immediately after.</para>
/// </summary>
public sealed class CommandBuffer : IDisposable
{
    // --- Stream backing ---
    // _stream holds fixed-size payloads packed end-to-end after each CommandHeader.
    // Object refs are encoded as ushort indices into _objects.
    // Blobs (matrix arrays, upload data) are parked into _store.
    internal byte[] _stream;
    internal int _streamPos;

    internal readonly List<object?> _objects = new(64);
    internal readonly Dictionary<string, ushort> _nameMap = new(32);
    internal readonly TransientStore _store = new();

    // PropertyState snapshots rented from the pool for SetMaterialProperties (and
    // any other "capture material state at encode time" operations). Returned to
    // the pool wholesale on buffer reset.
    private readonly List<PropertyState> _rentedSnapshots = new(8);

    /// <summary>Frame Debugger draw annotations, anchored to the stream offset they were
    /// recorded at (sidecar, not opcodes).</summary>
    private List<AnnotationRecord>? _drawAnnotations;

    /// <summary>Frame Debugger scope stamped by <see cref="Graphics.Submit"/>. Read by the
    /// backend translators; stable until the pool wipes the buffer after execution.</summary>
    internal FrameDebuggerScopeKind _fdScopeKind;

    /// <summary>Optional name for debug markers and RenderDoc captures.</summary>
    public string? Name { get; set; }

    /// <summary>Opt-in GPU diagnostic interval. Unsupported backends ignore this flag.</summary>
    public bool CaptureGpuTiming { get; set; }

    /// <summary>
    /// Presentation surface selected by the host that recorded this buffer. A default
    /// value preserves the primary-window submission path until a backend adopts targets.
    /// </summary>
    public PresentationSurfaceId PresentationSurface { get; private set; }

    internal bool _submitted;

    /// <summary>Set by Submit, cleared only by OnRent (NOT by OnReturn). Lets Dispose
    /// stay correct even if the render thread has already executed and re-rented this
    /// buffer to another caller before our using-block fires.</summary>
    internal bool _ownerReleased;

    internal bool _inPool;

    // Mutable frame globals must be captured at the submission boundary. Vulkan copies this value
    // into submission-owned transient storage instead of binding the shared host-visible buffer.
    internal GlobalUniformsData _globalUniformsSnapshot;
    internal bool _hasGlobalUniformsSnapshot;

    internal CommandBuffer()
    {
        _stream = ArrayPool<byte>.Shared.Rent(4096);
    }

    // ─────────────────────── Pool lifecycle ───────────────────────

    /// <summary>Pool calls this when handing the buffer back to a user.</summary>
    internal void OnRent(string? name)
    {
        Name = name;
        _submitted = false;
        _inPool = false;
        _ownerReleased = false;
        _hasGlobalUniformsSnapshot = false;
        _fdScopeKind = FrameDebuggerScopeKind.None;
        PresentationSurface = default;
    }

    /// <summary>Pool calls this after execution to wipe the buffer for reuse.</summary>
    internal void OnReturn()
    {
        _streamPos = 0;
        _objects.Clear();
        _nameMap.Clear();
        _store.Reset();
        for (int i = 0; i < _rentedSnapshots.Count; i++)
            PropertyStatePool.Return(_rentedSnapshots[i]);
        _rentedSnapshots.Clear();
        _drawAnnotations?.Clear();
        Name = null;
        CaptureGpuTiming = false;
        _submitted = false;
        _hasGlobalUniformsSnapshot = false;
        _fdScopeKind = FrameDebuggerScopeKind.None;
        PresentationSurface = default;
    }

    /// <summary>Selects the presentation surface for subsequent commands in this buffer.</summary>
    public void SetPresentationSurface(PresentationSurfaceId surface)
    {
        if (!surface.IsValid)
            throw new ArgumentException("Presentation surface id is invalid.", nameof(surface));
        if (_inPool || _submitted)
            throw new InvalidOperationException("CommandBuffer recording is closed.");
        PresentationSurface = surface;
    }

    internal void CaptureSubmissionState()
    {
        _globalUniformsSnapshot = GlobalUniforms.GetDataSnapshot();
        _hasGlobalUniformsSnapshot = true;
    }

    /// <summary>Final teardown when the pool drops the buffer entirely.</summary>
    internal void OnDestroy()
    {
        if (_stream != null)
        {
            ArrayPool<byte>.Shared.Return(_stream);
            _stream = null!;
        }
        _store.Dispose();
    }

    /// <summary>No-op if Submit was called (render thread returns the buffer).
    /// Returns to pool if the buffer was rented but never submitted.</summary>
    public void Dispose()
    {
        if (_ownerReleased) return;
        CommandBufferPool.Return(this);
    }

    // ─────────────────────── Render target / viewport / clear ───────────────────────

    /// <summary>Set the active draw + read framebuffer. null/default = backbuffer.</summary>
    public void SetRenderTarget(GraphicsFrameBuffer? fb)
    {
        WriteHeader(CommandOpcode.SetRenderTarget);
        Write(PushObject(fb));
    }

    /// <summary>Set draw and read framebuffers independently (used by blits).</summary>
    public void SetRenderTargets(GraphicsFrameBuffer? draw, GraphicsFrameBuffer? read)
    {
        WriteHeader(CommandOpcode.SetRenderTargets);
        Write(PushObject(draw));
        Write(PushObject(read));
    }

    public void SetViewport(int x, int y, uint w, uint h)
    {
        WriteHeader(CommandOpcode.SetViewport);
        Write(x); Write(y); Write(w); Write(h);
    }

    /// <summary>Enable the scissor test and set the clip rectangle, in framebuffer pixels
    /// (OpenGL bottom-left origin). Stays active until <see cref="DisableScissor"/>.</summary>
    public void SetScissor(int x, int y, uint w, uint h)
    {
        WriteHeader(CommandOpcode.SetScissor);
        Write(x); Write(y); Write(w); Write(h);
    }

    /// <summary>Disable the scissor test so subsequent draws are not clipped.</summary>
    public void DisableScissor()
    {
        WriteHeader(CommandOpcode.DisableScissor);
    }

    public void ClearRenderTarget(ClearFlags flags, Color color, float depth = 1f, int stencil = 0)
    {
        WriteHeader(CommandOpcode.ClearRenderTarget);
        Write((byte)flags);
        Write((float)color.R);
        Write((float)color.G);
        Write((float)color.B);
        Write((float)color.A);
        Write(depth);
        Write(stencil);
        RenderStats.RecordClear(flags);
    }

    public void BlitFramebuffer(int srcX, int srcY, int srcWidth, int srcHeight,
                                 int dstX, int dstY, int dstWidth, int dstHeight,
                                 ClearFlags mask, BlitFilter filter)
    {
        WriteHeader(CommandOpcode.BlitFramebuffer);
        Write(srcX); Write(srcY); Write(srcWidth); Write(srcHeight);
        Write(dstX); Write(dstY); Write(dstWidth); Write(dstHeight);
        Write((byte)mask);
        Write((byte)filter);
        RenderStats.RecordBlit(dstWidth, dstHeight);
    }

    /// <summary>
    /// Resolve one multisample color texture into a same-size single-sample texture. This is a
    /// fixed-function GPU operation (D3D12 <c>ResolveSubresource</c> / Vulkan
    /// <c>vkCmdResolveImage</c>), not a shader pass — it averages the samples of each pixel.
    /// Source and destination must share dimensions and format.
    /// </summary>
    internal void EncodeResolveTexture(GraphicsTexture source, GraphicsTexture destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        WriteHeader(CommandOpcode.ResolveTexture);
        Write(PushObject(source));
        Write(PushObject(destination));
    }

    /// <summary>
    /// Resolve every color attachment of a multisample render target into the matching attachment
    /// of a single-sample one. This replaces the downsampling blit the 2× SSAA MSAA stand-in used:
    /// the multisample target is rendered at native resolution, so only the sample buffers — not
    /// the pixel-shading cost — scale with the sample count.
    /// </summary>
    public void ResolveTexture(RenderTexture source, RenderTexture destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!source.frameBuffer.IsMultisampled)
            throw new ArgumentException("ResolveTexture source must be a multisample render target.", nameof(source));
        if (destination.frameBuffer.IsMultisampled)
            throw new ArgumentException("ResolveTexture destination must be single-sample.", nameof(destination));
        if (source.Width != destination.Width || source.Height != destination.Height)
            throw new ArgumentException("ResolveTexture requires source and destination of equal size.", nameof(destination));

        int count = Math.Min(source.InternalTextures.Length, destination.InternalTextures.Length);
        for (int i = 0; i < count; i++)
            EncodeResolveTexture(source.InternalTextures[i].Handle, destination.InternalTextures[i].Handle);
        RenderStats.RecordMsaaResolve();
    }

    // ─────────────────────── Pipeline state ───────────────────────

    /// <summary>While true, every raster state recorded via <see cref="SetRasterState"/> is forced
    /// to wireframe fill — the editor's Wireframe debug view. Scene-geometry passes toggle it on;
    /// grid/UI/post passes record with it off. Callers reset it after their draw block.</summary>
    public bool ForceWireframeFill;

    /// <summary>★ M7 — While true, every raster state recorded via <see cref="SetRasterState"/> is
    /// forced to additive blending with depth test and depth write off: the Overdraw debug view, where
    /// each fragment layer adds one step of the ramp regardless of occlusion. Set alongside the
    /// material override channel, and reset after the draw block like <see cref="ForceWireframeFill"/>.</summary>
    public bool ForceAdditiveNoDepth;

    public void SetRasterState(in RasterizerState state)
    {
        if (ForceWireframeFill || ForceAdditiveNoDepth)
        {
            RasterizerState patched = state;
            if (ForceWireframeFill)
                patched.FillMode = RasterizerState.FillModeKind.Wireframe;
            if (ForceAdditiveNoDepth)
            {
                patched.DepthTest = false;
                patched.DepthWrite = false;
                patched.DoBlend = true;
                patched.BlendSrc = RasterizerState.Blending.One;
                patched.BlendDst = RasterizerState.Blending.One;
                patched.Blend = RasterizerState.BlendMode.Add;
                patched.CullFace = RasterizerState.PolyFace.None; // count back faces too
            }
            WriteHeader(CommandOpcode.SetRasterState);
            Write(in patched);
            return;
        }
        WriteHeader(CommandOpcode.SetRasterState);
        Write(in state);
    }

    public void SetShader(GraphicsProgram program)
    {
        WriteHeader(CommandOpcode.SetShader);
        Write(PushObject(program));
    }

    public void SetShader(ShaderVariant variant)
    {
        WriteHeader(CommandOpcode.SetShader);
        Write(PushObject(variant));
    }

    /// <summary>Bind a compute <see cref="ShaderVariant"/> for subsequent dispatches.</summary>
    public void SetComputeShader(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        WriteHeader(CommandOpcode.SetComputeShader);
        Write(PushObject(variant));
    }

    // ─────────────────────── Property binding ───────────────────────

    /// <summary>Bind a PropertyState as the active material property set. Snapshotted
    /// at encode time so the caller can mutate the original after this call.</summary>
    public void SetProperties(PropertyState properties)
    {
        var snapshot = PropertyStatePool.RentSnapshot(properties);
        _rentedSnapshots.Add(snapshot);
        WriteHeader(CommandOpcode.SetProperties);
        Write(PushObject(snapshot));
    }

    /// <summary>Bind a Material's properties plus its shader's defaults. The
    /// material's properties are snapshotted at encode time so callers can mutate
    /// the material between Blit calls without affecting earlier-encoded draws.</summary>
    public void SetMaterialProperties(Material material)
    {
        Resources.Shader shader = material.Shader;
        var snapshot = PropertyStatePool.RentMaterialSnapshot(material._properties, shader);
        _rentedSnapshots.Add(snapshot);

        WriteHeader(CommandOpcode.SetMaterialProperties);
        Write(PushObject(snapshot));
        Write(PushObject(shader));
    }

    /// <summary>Clear the bound material properties.</summary>
    public void ClearProperties()
    {
        WriteHeader(CommandOpcode.ClearProperties);
    }

    /// <summary>Per-instance PropertyState applied AFTER material properties so it
    /// can override them. Snapshotted at encode time.</summary>
    public void SetInstanceProperties(PropertyState properties)
    {
        var snapshot = PropertyStatePool.RentSnapshot(properties);
        _rentedSnapshots.Add(snapshot);
        WriteHeader(CommandOpcode.SetInstanceProperties);
        Write(PushObject(snapshot));
    }

    public void ClearInstanceProperties()
    {
        WriteHeader(CommandOpcode.ClearInstanceProperties);
    }

    /// <summary>Set a global texture at execute time. Ordered against draws in this CB.</summary>
    public void SetGlobalTexture(string name, Texture2D? tex)
    {
        WriteHeader(CommandOpcode.SetGlobalTexture);
        Write(InternName(name));
        Write(PushObject(tex));
    }

    public void ClearGlobalTexture(string name)
    {
        WriteHeader(CommandOpcode.ClearGlobalTexture);
        Write(InternName(name));
    }

    public void SetGlobalInt(string name, int value)
    {
        WriteHeader(CommandOpcode.SetGlobalInt);
        Write(InternName(name));
        Write(value);
    }

    public void SetGlobalFloat(string name, float value)
    {
        WriteHeader(CommandOpcode.SetGlobalFloat);
        Write(InternName(name));
        Write(value);
    }

    public void SetGlobalVector(string name, Float2 value)
    {
        WriteHeader(CommandOpcode.SetGlobalVec2);
        Write(InternName(name));
        Write(in value);
    }

    public void SetGlobalVector(string name, Float3 value)
    {
        WriteHeader(CommandOpcode.SetGlobalVec3);
        Write(InternName(name));
        Write(in value);
    }

    public void SetGlobalVector(string name, Float4 value)
    {
        WriteHeader(CommandOpcode.SetGlobalVec4);
        Write(InternName(name));
        Write(in value);
    }

    public void SetGlobalColor(string name, Color value)
    {
        WriteHeader(CommandOpcode.SetGlobalColor);
        Write(InternName(name));
        Write(in value);
    }

    public void SetGlobalMatrix(string name, Float4x4 value)
    {
        WriteHeader(CommandOpcode.SetGlobalMatrix);
        Write(InternName(name));
        Write(in value);
    }

    public void SetGlobalMatrices(string name, Float4x4[] values)
    {
        WriteHeader(CommandOpcode.SetGlobalMatrices);
        Write(InternName(name));
        Write(PushObject(values));
    }

    public void SetGlobalBuffer(string name, GraphicsBuffer buffer, uint bindingPoint)
    {
        WriteHeader(CommandOpcode.SetGlobalBuffer);
        Write(InternName(name));
        Write(PushObject(buffer));
        Write(bindingPoint);
    }

    public void SetGlobalTexture3D(string name, Texture3D? tex)
    {
        WriteHeader(CommandOpcode.SetGlobalTexture3D);
        Write(InternName(name));
        Write(PushObject(tex));
    }

    public void SetGlobalTextureCube(string name, Cubemap? tex)
    {
        WriteHeader(CommandOpcode.SetGlobalTextureCube);
        Write(InternName(name));
        Write(PushObject(tex));
    }

    public void ClearAllGlobals()
    {
        WriteHeader(CommandOpcode.ClearAllGlobals);
    }

    // ─────────────────────── Per-uniform sugar ───────────────────────

    public void SetFloat(string name, float v)
    {
        WriteHeader(CommandOpcode.SetUniformFloat);
        Write(InternName(name));
        Write(v);
    }

    public void SetInt(string name, int v)
    {
        WriteHeader(CommandOpcode.SetUniformInt);
        Write(InternName(name));
        Write(v);
    }

    public void SetVector(string name, Float2 v)
    {
        WriteHeader(CommandOpcode.SetUniformVec2);
        Write(InternName(name));
        Write(v.X); Write(v.Y);
    }

    public void SetVector(string name, Float3 v)
    {
        WriteHeader(CommandOpcode.SetUniformVec3);
        Write(InternName(name));
        Write(v.X); Write(v.Y); Write(v.Z);
    }

    public void SetVector(string name, Float4 v)
    {
        WriteHeader(CommandOpcode.SetUniformVec4);
        Write(InternName(name));
        Write(v.X); Write(v.Y); Write(v.Z); Write(v.W);
    }

    public void SetColor(string name, Color c)
    {
        SetVector(name, new Float4((float)c.R, (float)c.G, (float)c.B, (float)c.A));
    }

    public void SetMatrix(string name, in Float4x4 m)
    {
        WriteHeader(CommandOpcode.SetUniformMatrix);
        Write(InternName(name));
        Write(in m);
    }

    public void SetMatrixArray(string name, ReadOnlySpan<Float4x4> ms)
    {
        WriteHeader(CommandOpcode.SetUniformMatrixArray);
        Write(InternName(name));
        Write((uint)ms.Length);
        var r = _store.Park(ms);
        Write(in r);
    }

    public void SetTexture(string name, Texture2D? tex)
    {
        WriteHeader(CommandOpcode.SetUniformTexture);
        Write(InternName(name));
        Write(PushObject(tex?.Handle));
    }

    public void SetTexture(string name, Texture3D? tex)
    {
        WriteHeader(CommandOpcode.SetUniformTexture);
        Write(InternName(name));
        Write(PushObject(tex?.Handle));
    }

    public void SetTexture(string name, GraphicsTexture? tex)
    {
        WriteHeader(CommandOpcode.SetUniformTexture);
        Write(InternName(name));
        Write(PushObject(tex));
    }

    public void SetBuffer(string name, GraphicsBuffer buf, uint bindingPoint = 0)
    {
        WriteHeader(CommandOpcode.SetUniformBuffer);
        Write(InternName(name));
        Write(PushObject(buf));
        Write(bindingPoint);
    }

    // ─────────────────────── Resource uploads ───────────────────────

    /// <summary>Upload <paramref name="data"/> into <paramref name="buf"/> at byte offset
    /// <paramref name="dstOffset"/>. The data is copied into the buffer's transient store
    /// at encode time, so the caller may release/reuse the source span immediately.</summary>
    public void UpdateBuffer<T>(GraphicsBuffer buf, ReadOnlySpan<T> data, uint dstOffset = 0) where T : unmanaged
    {
        WriteHeader(CommandOpcode.UpdateBuffer);
        Write(PushObject(buf));
        Write(dstOffset);
        var r = _store.Park(data);
        Write(in r);
    }

    /// <summary>Upload a 2D pixel rectangle into a texture's mip level.</summary>
    public void UpdateTexture<T>(Texture2D tex, int x, int y, uint w, uint h,
                                  ReadOnlySpan<T> data, int mip = 0) where T : unmanaged
    {
        WriteHeader(CommandOpcode.UpdateTexture);
        Write(PushObject(tex.Handle));
        Write(x); Write(y); Write(w); Write(h);
        Write(mip);
        var r = _store.Park(data);
        Write(in r);
    }

    public void GenerateMipmap(Texture2D tex)
    {
        WriteHeader(CommandOpcode.GenerateMipmap);
        Write(PushObject(tex.Handle));
    }

    public void GenerateMipmap(GraphicsTexture tex)
    {
        WriteHeader(CommandOpcode.GenerateMipmap);
        Write(PushObject(tex));
    }

    // ─────────────────────── Low-level draws ───────────────────────

    public void DrawIndexed(GraphicsVertexArray vao, Topology topo, uint indexCount,
                            uint startIndex = 0, int baseVertex = 0, bool index32bit = false)
    {
        WriteHeader(CommandOpcode.DrawIndexed);
        Write(PushObject(vao));
        Write((byte)topo);
        Write(indexCount);
        Write(startIndex);
        Write(baseVertex);
        Write(index32bit ? (byte)1 : (byte)0);

        RenderStats.RecordDraw(topo, indexCount);
    }

    public void DrawIndexedInstanced(GraphicsVertexArray vao, Topology topo,
                                      uint indexCount, uint instanceCount,
                                      uint startIndex = 0, int baseVertex = 0,
                                      bool index32bit = false)
    {
        WriteHeader(CommandOpcode.DrawIndexedInstanced);
        Write(PushObject(vao));
        Write((byte)topo);
        Write(indexCount);
        Write(instanceCount);
        Write(startIndex);
        Write(baseVertex);
        Write(index32bit ? (byte)1 : (byte)0);

        RenderStats.RecordDraw(topo, indexCount, instanceCount);
    }

    public void DrawArrays(GraphicsVertexArray vao, Topology topo, int first, uint count)
    {
        WriteHeader(CommandOpcode.DrawArrays);
        Write(PushObject(vao));
        Write((byte)topo);
        Write(first);
        Write(count);
    }

    public void DispatchCompute(uint groupCountX, uint groupCountY = 1, uint groupCountZ = 1)
    {
        WriteHeader(CommandOpcode.DispatchCompute);
        Write(groupCountX);
        Write(groupCountY);
        Write(groupCountZ);
    }

    public void DispatchIndirect(GraphicsBuffer args, uint offset = 0)
    {
        ArgumentNullException.ThrowIfNull(args);
        WriteHeader(CommandOpcode.DispatchIndirect);
        Write(PushObject(args));
        Write(offset);
    }

    public void DrawIndexedIndirect(GraphicsVertexArray vao, Topology topo, GraphicsBuffer args,
                                    uint offset = 0, bool index32bit = false)
    {
        ArgumentNullException.ThrowIfNull(vao);
        ArgumentNullException.ThrowIfNull(args);
        WriteHeader(CommandOpcode.DrawIndexedIndirect);
        Write(PushObject(vao));
        Write((byte)topo);
        Write(PushObject(args));
        Write(offset);
        Write(index32bit ? (byte)1 : (byte)0);
    }

    public void ResourceBarrier(GpuBarrierKind kind, object resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        WriteHeader(CommandOpcode.ResourceBarrier);
        Write((byte)kind);
        Write(PushObject(resource));
    }

    // ─────────────────────── High-level draws (encoder sugar) ───────────────────────

    /// <summary>Encode a single mesh draw. Sets the mesh-attribute keywords on
    /// <paramref name="material"/>, fetches the variant program, binds shader +
    /// raster state + properties, then issues the draw. The mesh must already be
    /// uploaded (call <c>mesh.Upload()</c> first; CB-friendly).</summary>
    public void DrawMesh(Mesh mesh, Material material, int passIndex = 0,
                         in Float4x4 model = default,
                         PropertyState? instanceProperties = null,
                         int subMeshIndex = -1,
                         RasterizerState? rasterOverride = null)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        if (material == null) throw new ArgumentNullException(nameof(material));

        mesh.Upload();
        if (mesh.VertexArrayObject == null || mesh.VertexCount <= 0) return;

        // Mesh-attribute keywords (mirrors RenderPipeline.DrawMeshNow today).
        material.SetKeyword("HAS_NORMALS", mesh.HasNormals);
        material.SetKeyword("HAS_TANGENTS", mesh.HasTangents);
        material.SetKeyword("HAS_UV", mesh.HasUV);
        material.SetKeyword("HAS_UV2", mesh.HasUV2);
        material.SetKeyword("HAS_COLORS", mesh.HasColors || mesh.HasColors32);
        material.SetKeyword("HAS_BONEINDICES", mesh.HasBoneIndices);
        material.SetKeyword("HAS_BONEWEIGHTS", mesh.HasBoneWeights);
        material.SetKeyword("SKINNED", mesh.HasBoneIndices && mesh.HasBoneWeights);
        material.SetKeyword("BLENDSHAPES", mesh.HasBlendShapes);

        var pass = material.Shader.GetPass(passIndex);
        if (!pass.TryGetMaterialVariant(material, out ShaderVariant? variant) || variant == null)
            return;

        SetShader(variant);
        SetRasterState(rasterOverride ?? pass.State);
        SetMaterialProperties(material);

        if (instanceProperties != null)
            SetInstanceProperties(instanceProperties);
        else
            ClearInstanceProperties();

        // Default(Float4x4) is all-zero; treat as "no model override".
        Float4x4 zero = default;
        bool hasModel = !model.Equals(zero);
        if (hasModel)
        {
            SetMatrix("xengine_ObjectToWorld", in model);
            Float4x4 inv = model.Invert();
            SetMatrix("xengine_WorldToObject", in inv);
        }

        // Annotation is recorded immediately before the draw opcode so the decoder's
        // stream-offset pairing anchors it to exactly this draw.
        FrameDebugger.RecordDrawAnnotation(this, mesh, material, passIndex);

        bool index32 = mesh.IndexFormat == IndexFormat.UInt32;
        if (subMeshIndex >= 0 && subMeshIndex < mesh.SubMeshCount)
        {
            var sub = mesh.GetSubMesh(subMeshIndex);
            DrawIndexed(mesh.VertexArrayObject, sub.Topology, (uint)sub.IndexCount, (uint)sub.IndexStart, 0, index32);
        }
        else
        {
            DrawIndexed(mesh.VertexArrayObject, mesh.MeshTopology, (uint)mesh.IndexCount, 0, 0, index32);
        }
    }

    /// <summary>Blit <paramref name="source"/> to <paramref name="destination"/>. If
    /// <paramref name="material"/> is null a default copy is performed. Null destination
    /// means the backbuffer.</summary>
    public void Blit(RenderTexture? source, RenderTexture? destination,
                     Material? material = null, int pass = 0,
                     bool clearDepth = false, bool clearColor = false, Color color = default)
    {
        int destX = 0;
        int destY = 0;
        uint destW;
        uint destH;
        if (destination != null && destination.IsValid())
        {
            destW = (uint)destination.Width;
            destH = (uint)destination.Height;
        }
        else
        {
            var fb = Window.InternalWindow.FramebufferSize;
            destW = (uint)fb.X;
            destH = (uint)fb.Y;
        }

        Blit(source, destination, destX, destY, destW, destH, material, pass, clearDepth, clearColor, color);
    }

    /// <summary>
    /// Blit into a destination rectangle (split-screen / Camera.Viewrect). Width/height of 0
    /// fall back to the full destination.
    /// </summary>
    public void Blit(RenderTexture? source, RenderTexture? destination,
                     int destX, int destY, uint destW, uint destH,
                     Material? material = null, int pass = 0,
                     bool clearDepth = false, bool clearColor = false, Color color = default)
    {
        bool defaultBlit = material == null;
        material ??= Rendering.RenderPipeline.GetBlitMaterial();
        if (source != null)
        {
            material.SetTexture("_MainTex", source.MainTexture);
            if (defaultBlit)
            {
                // Vulkan's swapchain copy needs the presentation conversion. Applying that
                // conversion to every intermediate copy makes output depend on pass count.
                // Editor GameView targets keep their existing render-texture convention.
                GraphicsBackend backend = Graphics.BackendDevice?.Backend ?? GraphicsBackend.Null;
                material.SetFloat("_FlipY", ResolveDefaultBlitFlipY(backend, destination == null));
            }
        }
        else if (defaultBlit)
        {
            material.SetFloat("_FlipY", 0f);
        }

        if (destination != null && destination.IsValid())
        {
            SetRenderTarget(destination.frameBuffer);
            if (destW == 0 || destH == 0)
            {
                destX = 0;
                destY = 0;
                destW = (uint)destination.Width;
                destH = (uint)destination.Height;
            }
        }
        else
        {
            SetRenderTarget(null);
            if (destW == 0 || destH == 0)
            {
                var fb = Window.InternalWindow.FramebufferSize;
                destX = 0;
                destY = 0;
                destW = (uint)fb.X;
                destH = (uint)fb.Y;
            }
        }

        SetViewport(destX, destY, destW, destH);
        DisableScissor();

        if (clearDepth || clearColor)
        {
            ClearFlags clear = 0;
            if (clearDepth) clear |= ClearFlags.Depth;
            if (clearColor) clear |= ClearFlags.Color;
            ClearRenderTarget(clear | ClearFlags.Stencil, color);
        }

        DrawMesh(Mesh.GetFullscreenQuad(), material, pass);
    }

    /// <summary>Separate swapchain presentation from intermediate render-texture copies.</summary>
    internal static float ResolveDefaultBlitFlipY(
        GraphicsBackend backend,
        bool presenting) =>
        backend switch
        {
            GraphicsBackend.Vulkan => presenting ? 1f : 0f,
            // Metal textures and CAMetalLayer drawables share the same top-left origin;
            // flipping either an intermediate copy or the final presentation turns the
            // Player image upside down relative to its GameView render target.
            GraphicsBackend.Metal => 0f,
            _ => 1f,
        };

    public void Blit(Texture2D source, RenderTexture destination,
                     Material? material = null, int pass = 0)
    {
        bool defaultBlit = material == null;
        material ??= Rendering.RenderPipeline.GetBlitMaterial();
        material.SetTexture("_MainTex", source);
        if (defaultBlit)
            material.SetFloat("_FlipY", 0f);
        SetRenderTarget(destination.frameBuffer);
        SetViewport(0, 0, (uint)destination.Width, (uint)destination.Height);
        DrawMesh(Mesh.GetFullscreenQuad(), material, pass);
    }

    /// <summary>Render a fullscreen quad with <paramref name="material"/> into
    /// <paramref name="destination"/>. Used by image effects whose material samples
    /// their own already-bound textures (no _MainTex copy from a source RT).</summary>
    public void Blit(RenderTexture destination, Material material, int pass = 0)
    {
        SetRenderTarget(destination.frameBuffer);
        SetViewport(0, 0, (uint)destination.Width, (uint)destination.Height);
        DrawMesh(Mesh.GetFullscreenQuad(), material, pass);
    }

    /// <summary>Fullscreen blit of <paramref name="material"/> into the currently bound
    /// render target. The caller is responsible for binding the target first.</summary>
    public void Blit(Material material, int pass = 0)
    {
        DrawMesh(Mesh.GetFullscreenQuad(), material, pass);
    }

    // ─────────────────────── Debug markers ───────────────────────

    public void BeginSample(string label)
    {
        WriteHeader(CommandOpcode.BeginSample);
        Write(PushObject(label));
    }

    // ─────────────────────── Internal resource-lifecycle encoders ───────────────────────
    // Called from resource constructors and Dispose paths. The wrappers never touch
    // GL directly all mutation routes through the executor on the render thread.

    internal void EncodeCreateBuffer(GraphicsBuffer buf, bool dynamic, ReadOnlySpan<byte> data)
    {
        WriteHeader(CommandOpcode.CreateBuffer);
        Write(PushObject(buf));
        Write((byte)(dynamic ? 1 : 0));
        var r = _store.Park(data);
        Write(in r);
    }

    internal void EncodeDisposeBuffer(GraphicsBuffer buf)
    {
        WriteHeader(CommandOpcode.DisposeBuffer);
        Write(PushObject(buf));
    }

    internal void EncodeCreateTexture(GraphicsTexture tex)
    {
        WriteHeader(CommandOpcode.CreateTexture);
        Write(PushObject(tex));
    }

    internal void EncodeDisposeTexture(GraphicsTexture tex)
    {
        WriteHeader(CommandOpcode.DisposeTexture);
        Write(PushObject(tex));
    }

    /// <summary>Allocate storage for a 2D texture. Data may be empty to allocate
    /// uninitialised storage (e.g. for render-target attachments or texture creation
    /// followed by sub-image uploads).</summary>
    internal void EncodeAllocateTexture2D(GraphicsTexture tex, int mip, uint width, uint height, int border, ReadOnlySpan<byte> data)
    {
        WriteHeader(CommandOpcode.AllocateTexture2D);
        Write(PushObject(tex));
        Write(mip);
        Write(width);
        Write(height);
        Write(border);
        var r = _store.Park(data);
        Write(in r);
    }

    /// <summary>Allocate (and optionally upload) one face of a cubemap at a mip level.
    /// <paramref name="face"/> is 0..5 in GL order (+X, -X, +Y, -Y, +Z, -Z).</summary>
    internal void EncodeAllocateTextureCubeFace(GraphicsTexture tex, int face, int mip, uint size, ReadOnlySpan<byte> data)
    {
        WriteHeader(CommandOpcode.AllocateTextureCubeFace);
        Write(PushObject(tex));
        Write(face);
        Write(mip);
        Write(size);
        var r = _store.Park(data);
        Write(in r);
    }

    internal void EncodeAllocateTexture3D(GraphicsTexture tex, int mip, uint width, uint height, uint depth, ReadOnlySpan<byte> data)
    {
        WriteHeader(CommandOpcode.AllocateTexture3D);
        Write(PushObject(tex));
        Write(mip);
        Write(width);
        Write(height);
        Write(depth);
        var r = _store.Park(data);
        Write(in r);
    }

    /// <summary>Upload a 2D pixel rect into a texture's mip level. Uses the same
    /// opcode as the public <see cref="UpdateTexture{T}(Texture2D, int, int, uint, uint, ReadOnlySpan{T}, int)"/>
    /// but takes a raw GraphicsTexture invoked by Graphics.TexSubImage2D and other
    /// facade paths that don't have a Texture2D wrapper handy.</summary>
    internal void EncodeUpdateTexture2D(GraphicsTexture tex, int mip, int x, int y, uint width, uint height, ReadOnlySpan<byte> data)
    {
        WriteHeader(CommandOpcode.UpdateTexture);
        Write(PushObject(tex));
        Write(x); Write(y); Write(width); Write(height);
        Write(mip);
        var r = _store.Park(data);
        Write(in r);
    }

    /// <summary>Upload a 3D pixel volume into a texture's mip level.</summary>
    internal void EncodeUpdateTexture3D(GraphicsTexture tex, int mip, int x, int y, int z, uint width, uint height, uint depth, ReadOnlySpan<byte> data)
    {
        WriteHeader(CommandOpcode.UpdateTexture3D);
        Write(PushObject(tex));
        Write(mip);
        Write(x); Write(y); Write(z);
        Write(width); Write(height); Write(depth);
        var r = _store.Park(data);
        Write(in r);
    }

    internal void EncodeSetTextureWrap(GraphicsTexture tex, byte axis, TextureWrap mode)
    {
        WriteHeader(CommandOpcode.SetTextureWrap);
        Write(PushObject(tex));
        Write(axis);
        Write((byte)mode);
    }

    internal void EncodeSetTextureFilters(GraphicsTexture tex, TextureMin min, TextureMag mag)
    {
        WriteHeader(CommandOpcode.SetTextureFiltersOp);
        Write(PushObject(tex));
        Write((byte)min);
        Write((byte)mag);
    }

    internal void EncodeSetTextureCompareMode(GraphicsTexture tex, bool enabled)
    {
        WriteHeader(CommandOpcode.SetTextureCompareMode);
        Write(PushObject(tex));
        Write((byte)(enabled ? 1 : 0));
    }

    /// <summary>Read a texture's pixels back. Caller MUST use SubmitAndWait the
    /// destination buffer is only filled when the op runs on the render thread.</summary>
    internal void EncodeGetTextureData(GraphicsTexture tex, int mip, byte[] destination)
    {
        WriteHeader(CommandOpcode.GetTextureData);
        Write(PushObject(tex));
        Write(mip);
        Write(PushObject(destination));
    }

    /// <summary>Read one cubemap face's mip back into <paramref name="destination"/>.
    /// Caller MUST use SubmitAndWait.</summary>
    internal void EncodeGetTextureCubeFaceData(GraphicsTexture tex, int face, int mip, byte[] destination)
    {
        WriteHeader(CommandOpcode.GetTextureCubeFaceData);
        Write(PushObject(tex));
        Write(face);
        Write(mip);
        Write(PushObject(destination));
    }

    /// <summary>Pointer-destination variant. Caller MUST use SubmitAndWait the
    /// pointer has to stay valid until the render thread runs the op.</summary>
    internal void EncodeGetTextureDataPtr(GraphicsTexture tex, int mip, nint destination)
    {
        WriteHeader(CommandOpcode.GetTextureDataPtr);
        Write(PushObject(tex));
        Write(mip);
        Write((long)destination);
    }

    /// <summary>Defers vertex-array creation to the selected backend executor.</summary>
    internal void EncodeCreateVertexArray(GraphicsVertexArray vao)
    {
        WriteHeader(CommandOpcode.CreateVertexArrayOp);
        Write(PushObject(vao));
    }

    internal void EncodeDisposeVertexArray(GraphicsVertexArray vao)
    {
        WriteHeader(CommandOpcode.DisposeVertexArray);
        Write(PushObject(vao));
    }

    internal void EncodeCreateFramebuffer(GraphicsFrameBuffer fb)
    {
        WriteHeader(CommandOpcode.CreateFramebufferOp);
        Write(PushObject(fb));
    }

    internal void EncodeDisposeFramebuffer(GraphicsFrameBuffer fb)
    {
        WriteHeader(CommandOpcode.DisposeFramebuffer);
        Write(PushObject(fb));
    }

    internal void EncodeCompileShader(GraphicsProgram program)
    {
        WriteHeader(CommandOpcode.CompileShader);
        Write(PushObject(program));
    }

    internal void EncodeDisposeShader(GraphicsProgram program)
    {
        WriteHeader(CommandOpcode.DisposeShader);
        Write(PushObject(program));
    }

    public void EndSample()
    {
        WriteHeader(CommandOpcode.EndSample);
    }

    // ─────────────────────── B7b occlusion queries ───────────────────────

    /// <summary>
    /// Begins an occlusion (ANY_SAMPLES_PASSED-style) query around the draws that follow until
    /// the matching <see cref="EndOcclusionQuery"/>. The pair must wrap the probe draw exactly —
    /// the result answers "did any sample of those draws pass the depth test". Only meaningful
    /// on devices whose <c>SupportsOcclusionQueries</c> is true; elsewhere the pair is skipped
    /// and the id never completes, so callers must treat a missing result as "no verdict".
    /// </summary>
    public void BeginOcclusionQuery(ulong queryId)
    {
        WriteHeader(CommandOpcode.BeginOcclusionQuery);
        Write(queryId);
    }

    public void EndOcclusionQuery(ulong queryId)
    {
        WriteHeader(CommandOpcode.EndOcclusionQuery);
        Write(queryId);
    }

    internal void AddDrawAnnotation(DrawAnnotation annotation)
    {
        _drawAnnotations ??= new List<AnnotationRecord>(8);
        _drawAnnotations.Add(new AnnotationRecord(_streamPos, annotation));
    }

    internal IReadOnlyList<AnnotationRecord>? DrawAnnotations => _drawAnnotations;

    // ─────────────────────── Internal: stream writers ───────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteHeader(CommandOpcode op)
    {
        // _inPool (not _submitted): OnReturn resets _submitted to false, so
        // checking it wouldn't catch the common "encoded after Submit" misuse.
        if (_inPool) throw new InvalidOperationException("CommandBuffer has been returned to the pool; encoding is closed. Did you encode after Graphics.Submit?");
        EnsureCapacity(sizeof(ushort));
        MemoryMarshal.Write(_stream.AsSpan(_streamPos), in op);
        _streamPos += sizeof(ushort);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write<T>(T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        EnsureCapacity(size);
        MemoryMarshal.Write(_stream.AsSpan(_streamPos), in value);
        _streamPos += size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        EnsureCapacity(size);
        MemoryMarshal.Write(_stream.AsSpan(_streamPos), in value);
        _streamPos += size;
    }

    private void EnsureCapacity(int extra)
    {
        if (_streamPos + extra <= _stream.Length) return;
        int newSize = Math.Max(_stream.Length * 2, _streamPos + extra);
        byte[] next = ArrayPool<byte>.Shared.Rent(newSize);
        Buffer.BlockCopy(_stream, 0, next, 0, _streamPos);
        ArrayPool<byte>.Shared.Return(_stream);
        _stream = next;
    }

    private ushort PushObject(object? obj)
    {
        int idx = _objects.Count;
        if (idx > ushort.MaxValue)
            throw new InvalidOperationException("CommandBuffer object reference table exceeded 65k entries.");
        _objects.Add(obj);
        return (ushort)idx;
    }

    private ushort InternName(string name)
    {
        if (_nameMap.TryGetValue(name, out ushort idx)) return idx;
        idx = PushObject(name);
        _nameMap[name] = idx;
        return idx;
    }
}
