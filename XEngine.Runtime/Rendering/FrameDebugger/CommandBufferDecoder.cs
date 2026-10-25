// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>Decodes a <see cref="CommandBuffer"/> opcode stream into <see cref="CapturedOp"/> entries.</summary>
public static class CommandBufferDecoder
{
    public static void Decode(
        CommandBuffer cmd,
        string? commandBufferName,
        IReadOnlyList<AnnotationRecord>? annotations,
        List<CapturedOp> destination,
        List<PropertyState> ownedPropertyStates)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(ownedPropertyStates);

        ReadOnlySpan<byte> stream = cmd._stream.AsSpan(0, cmd._streamPos);
        IReadOnlyList<object?> objects = cmd._objects;
        int pos = 0;
        int annotationIndex = 0;

        while (pos < stream.Length)
        {
            int opStart = pos;
            CommandOpcode op = ReadOpcode(stream, ref pos);
            CapturedOp captured = DecodeOne(op, stream, ref pos, objects, commandBufferName, ownedPropertyStates);
            // Single source of truth: the same classifier gates every backend's skip hook,
            // so tree leaf index == GPU skip index by construction.
            captured.ShowInTree = FrameDebuggerOpClassifier.IsLeafGpuOp(op);
            if (captured.ShowInTree &&
                (captured.VisibleKind == FrameEventKind.Draw || captured.VisibleKind == FrameEventKind.DrawInstanced) &&
                annotations != null)
            {
                // Take the LAST record anchored at or before this draw's opcode. Records are
                // written immediately before their draw, so any record skipped over here
                // belonged to a draw that never got encoded (self-healing).
                DrawAnnotation? ann = null;
                while (annotationIndex < annotations.Count &&
                       annotations[annotationIndex].StreamOffset <= opStart)
                    ann = annotations[annotationIndex++].Annotation;
                if (ann != null)
                {
                    captured.Annotation = ann;
                    if (!string.IsNullOrEmpty(ann.MeshName))
                        captured.Label = ann.MeshName;
                    else if (!string.IsNullOrEmpty(ann.MaterialName))
                        captured.Label = ann.MaterialName;
                }
            }

            destination.Add(captured);
        }
    }

    private static CapturedOp DecodeOne(
        CommandOpcode op,
        ReadOnlySpan<byte> stream,
        ref int pos,
        IReadOnlyList<object?> objects,
        string? cbName,
        List<PropertyState> owned)
    {
        var c = new CapturedOp { Opcode = op, CommandBufferName = cbName };
        switch (op)
        {
            case CommandOpcode.SetRenderTarget:
            {
                var fb = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                c.RenderTarget = SnapshotFramebuffer(fb, out c.RenderTargetTexture);
                c.VisibleKind = FrameEventKind.SetRenderTarget;                c.Label = c.RenderTarget.Value.IsBackbuffer ? "SetRenderTarget Backbuffer" : $"SetRenderTarget {c.RenderTarget.Value.Name}";
                break;
            }
            case CommandOpcode.SetRenderTargets:
            {
                var draw = (GraphicsFrameBuffer?)objects[ReadU16(stream, ref pos)];
                _ = objects[ReadU16(stream, ref pos)];
                c.RenderTarget = SnapshotFramebuffer(draw, out c.RenderTargetTexture);
                c.VisibleKind = FrameEventKind.SetRenderTarget;                c.Label = c.RenderTarget.Value.IsBackbuffer ? "SetRenderTargets Backbuffer" : $"SetRenderTargets {c.RenderTarget.Value.Name}";
                break;
            }
            case CommandOpcode.SetViewport:
            {
                c.ViewportX = ReadI32(stream, ref pos);
                c.ViewportY = ReadI32(stream, ref pos);
                c.ViewportW = ReadU32(stream, ref pos);
                c.ViewportH = ReadU32(stream, ref pos);
                c.Label = $"SetViewport {c.ViewportW}x{c.ViewportH}";
                break;
            }
            case CommandOpcode.SetScissor:
            {
                c.ScissorEnabled = true;
                c.ScissorX = ReadI32(stream, ref pos);
                c.ScissorY = ReadI32(stream, ref pos);
                c.ScissorW = ReadU32(stream, ref pos);
                c.ScissorH = ReadU32(stream, ref pos);
                c.Label = "SetScissor";
                break;
            }
            case CommandOpcode.DisableScissor:
                c.ScissorEnabled = false;
                c.Label = "DisableScissor";
                break;
            case CommandOpcode.ClearRenderTarget:
            {
                c.ClearFlags = (ClearFlags)ReadU8(stream, ref pos);
                float r = ReadF32(stream, ref pos);
                float g = ReadF32(stream, ref pos);
                float b = ReadF32(stream, ref pos);
                float a = ReadF32(stream, ref pos);
                c.ClearColor = new Color(r, g, b, a);
                c.ClearDepth = ReadF32(stream, ref pos);
                c.ClearStencil = ReadI32(stream, ref pos);
                c.VisibleKind = FrameEventKind.Clear;                c.Label = $"Clear {c.ClearFlags}";
                break;
            }
            case CommandOpcode.BlitFramebuffer:
            {
                _ = ReadI32(stream, ref pos); _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos); _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos); _ = ReadI32(stream, ref pos);
                int dw = ReadI32(stream, ref pos);
                int dh = ReadI32(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                c.VisibleKind = FrameEventKind.Blit;                c.Label = $"BlitFramebuffer {dw}x{dh}";
                break;
            }
            case CommandOpcode.ResolveTexture:
            {
                var src = objects[ReadU16(stream, ref pos)] as GraphicsTexture;
                _ = objects[ReadU16(stream, ref pos)];
                c.VisibleKind = FrameEventKind.Blit;
                c.Label = src == null ? "ResolveTexture" : $"ResolveTexture {src.SampleCount}x";
                break;
            }
            case CommandOpcode.SetRasterState:
            {
                c.Raster = ReadStruct<RasterizerState>(stream, ref pos);
                c.VisibleKind = FrameEventKind.SetRaster;
                c.Label = "SetRasterState";
                break;
            }
            case CommandOpcode.SetShader:
            {
                object? shader = objects[ReadU16(stream, ref pos)];
                c.ShaderLabel = DescribeShader(shader);
                if (shader is ShaderVariant sv && sv.SourcePass != null)
                {
                    c.ShaderAssetName = sv.SourcePass.ShaderName;
                    c.ShaderPassName = sv.SourcePass.Name;
                    c.VariantKeywords = sv.SourceKeywords;
                }
                c.VisibleKind = FrameEventKind.SetShader;
                c.Label = $"SetShader {c.ShaderLabel}";
                break;
            }
            case CommandOpcode.SetProperties:
            {
                c.MaterialProperties = CloneOwned((PropertyState?)objects[ReadU16(stream, ref pos)], owned);
                c.Label = "SetProperties";
                break;
            }
            case CommandOpcode.SetMaterialProperties:
            {
                c.MaterialProperties = CloneOwned((PropertyState?)objects[ReadU16(stream, ref pos)], owned);
                var shader = (Shader?)objects[ReadU16(stream, ref pos)];
                c.ShaderAssetName = shader?.Name ?? "";
                c.Label = "SetMaterialProperties";
                break;
            }
            case CommandOpcode.ClearProperties:
                c.Label = "ClearProperties";
                break;
            case CommandOpcode.SetInstanceProperties:
                c.InstanceProperties = CloneOwned((PropertyState?)objects[ReadU16(stream, ref pos)], owned);
                c.Label = "SetInstanceProperties";
                break;
            case CommandOpcode.ClearInstanceProperties:
                c.Label = "ClearInstanceProperties";
                break;
            case CommandOpcode.SetGlobalTexture:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.TextureLabel = DescribeTexture(objects[ReadU16(stream, ref pos)]);
                c.Label = $"SetGlobalTexture {c.UniformName}";
                break;
            case CommandOpcode.ClearGlobalTexture:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.Label = $"ClearGlobalTexture {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalInt:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformInt = ReadI32(stream, ref pos);
                c.Label = $"SetGlobalInt {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalFloat:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformFloat = ReadF32(stream, ref pos);
                c.Label = $"SetGlobalFloat {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalVec2:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformVec2 = ReadStruct<Float2>(stream, ref pos);
                c.Label = $"SetGlobalVec2 {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalVec3:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformVec3 = ReadStruct<Float3>(stream, ref pos);
                c.Label = $"SetGlobalVec3 {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalVec4:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformVec4 = ReadStruct<Float4>(stream, ref pos);
                c.Label = $"SetGlobalVec4 {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalColor:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformColor = ReadStruct<Color>(stream, ref pos);
                c.Label = $"SetGlobalColor {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalMatrix:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformMatrix = ReadStruct<Float4x4>(stream, ref pos);
                c.Label = $"SetGlobalMatrix {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalMatrices:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                _ = ReadU16(stream, ref pos);
                c.Label = $"SetGlobalMatrices {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalBuffer:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.BufferLabel = DescribeBuffer(objects[ReadU16(stream, ref pos)]);
                c.BufferBinding = ReadU32(stream, ref pos);
                c.Label = $"SetGlobalBuffer {c.UniformName}";
                break;
            case CommandOpcode.SetGlobalTexture3D:
            case CommandOpcode.SetGlobalTextureCube:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.TextureLabel = DescribeTexture(objects[ReadU16(stream, ref pos)]);
                c.Label = $"{op} {c.UniformName}";
                break;
            case CommandOpcode.ClearAllGlobals:
                c.Label = "ClearAllGlobals";
                break;
            case CommandOpcode.SetUniformFloat:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformFloat = ReadF32(stream, ref pos);
                c.Label = $"SetUniformFloat {c.UniformName}";
                break;
            case CommandOpcode.SetUniformInt:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformInt = ReadI32(stream, ref pos);
                c.Label = $"SetUniformInt {c.UniformName}";
                break;
            case CommandOpcode.SetUniformVec2:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformVec2 = new Float2(ReadF32(stream, ref pos), ReadF32(stream, ref pos));
                c.Label = $"SetUniformVec2 {c.UniformName}";
                break;
            case CommandOpcode.SetUniformVec3:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformVec3 = new Float3(ReadF32(stream, ref pos), ReadF32(stream, ref pos), ReadF32(stream, ref pos));
                c.Label = $"SetUniformVec3 {c.UniformName}";
                break;
            case CommandOpcode.SetUniformVec4:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformVec4 = new Float4(
                    ReadF32(stream, ref pos), ReadF32(stream, ref pos),
                    ReadF32(stream, ref pos), ReadF32(stream, ref pos));
                c.Label = $"SetUniformVec4 {c.UniformName}";
                break;
            case CommandOpcode.SetUniformMatrix:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.UniformMatrix = ReadStruct<Float4x4>(stream, ref pos);
                c.Label = $"SetUniformMatrix {c.UniformName}";
                break;
            case CommandOpcode.SetUniformMatrixArray:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                _ = ReadU32(stream, ref pos);
                SkipBlob(stream, ref pos);
                c.Label = $"SetUniformMatrixArray {c.UniformName}";
                break;
            case CommandOpcode.SetUniformTexture:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.TextureLabel = DescribeTexture(objects[ReadU16(stream, ref pos)]);
                c.Label = $"SetUniformTexture {c.UniformName}";
                break;
            case CommandOpcode.SetUniformBuffer:
                c.UniformName = (string?)objects[ReadU16(stream, ref pos)];
                c.BufferLabel = DescribeBuffer(objects[ReadU16(stream, ref pos)]);
                c.BufferBinding = ReadU32(stream, ref pos);
                c.Label = $"SetUniformBuffer {c.UniformName}";
                break;
            case CommandOpcode.UpdateBuffer:
                _ = ReadU16(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                SkipBlob(stream, ref pos);
                c.VisibleKind = FrameEventKind.UpdateResource;
                c.Label = "UpdateBuffer";
                break;
            case CommandOpcode.UpdateTexture:
                c.TextureLabel = DescribeTexture(objects[ReadU16(stream, ref pos)]);
                _ = ReadI32(stream, ref pos); _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos); _ = ReadU32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                SkipBlob(stream, ref pos);
                c.VisibleKind = FrameEventKind.UpdateResource;
                c.Label = "UpdateTexture";
                break;
            case CommandOpcode.GenerateMipmap:
                c.TextureLabel = DescribeTexture(objects[ReadU16(stream, ref pos)]);
                c.VisibleKind = FrameEventKind.UpdateResource;
                c.Label = "GenerateMipmap";
                break;
            case CommandOpcode.DrawIndexed:
            {
                _ = objects[ReadU16(stream, ref pos)];
                c.Topology = (Topology)ReadU8(stream, ref pos);
                c.IndexCount = ReadU32(stream, ref pos);
                c.StartIndex = ReadU32(stream, ref pos);
                c.BaseVertex = ReadI32(stream, ref pos);
                c.Index32Bit = ReadU8(stream, ref pos) != 0;
                c.VisibleKind = FrameEventKind.Draw;                c.Label = $"DrawIndexed {c.Topology} x{c.IndexCount}";
                break;
            }
            case CommandOpcode.DrawIndexedInstanced:
            {
                _ = objects[ReadU16(stream, ref pos)];
                c.Topology = (Topology)ReadU8(stream, ref pos);
                c.IndexCount = ReadU32(stream, ref pos);
                c.InstanceCount = ReadU32(stream, ref pos);
                c.StartIndex = ReadU32(stream, ref pos);
                c.BaseVertex = ReadI32(stream, ref pos);
                c.Index32Bit = ReadU8(stream, ref pos) != 0;
                c.VisibleKind = FrameEventKind.DrawInstanced;                c.Label = $"DrawInstanced {c.Topology} x{c.IndexCount} *{c.InstanceCount}";
                break;
            }
            case CommandOpcode.DrawArrays:
            {
                _ = objects[ReadU16(stream, ref pos)];
                c.Topology = (Topology)ReadU8(stream, ref pos);
                c.DrawArraysFirst = ReadI32(stream, ref pos);
                c.DrawArraysCount = ReadU32(stream, ref pos);
                c.VisibleKind = FrameEventKind.Draw;                c.Label = $"DrawArrays {c.Topology} x{c.DrawArraysCount}";
                break;
            }
            case CommandOpcode.SetComputeShader:
            {
                object? shader = objects[ReadU16(stream, ref pos)];
                c.VisibleKind = FrameEventKind.SetShader;                c.Label = shader is ShaderVariant variant
                    ? $"SetComputeShader #{variant.Id}"
                    : "SetComputeShader";
                break;
            }
            case CommandOpcode.DispatchCompute:
            {
                uint x = ReadU32(stream, ref pos);
                uint y = ReadU32(stream, ref pos);
                uint z = ReadU32(stream, ref pos);
                c.VisibleKind = FrameEventKind.Dispatch;                c.Label = $"Dispatch {x}x{y}x{z}";
                break;
            }
            case CommandOpcode.DispatchIndirect:
            {
                _ = objects[ReadU16(stream, ref pos)];
                uint offset = ReadU32(stream, ref pos);
                c.VisibleKind = FrameEventKind.Dispatch;                c.Label = $"DispatchIndirect +{offset}";
                break;
            }
            case CommandOpcode.DrawIndexedIndirect:
            {
                _ = objects[ReadU16(stream, ref pos)];
                c.Topology = (Topology)ReadU8(stream, ref pos);
                _ = objects[ReadU16(stream, ref pos)];
                uint offset = ReadU32(stream, ref pos);
                c.Index32Bit = ReadU8(stream, ref pos) != 0;
                c.VisibleKind = FrameEventKind.Draw;                c.Label = $"DrawIndexedIndirect {c.Topology} +{offset}";
                break;
            }
            case CommandOpcode.ResourceBarrier:
            {
                GpuBarrierKind kind = (GpuBarrierKind)ReadU8(stream, ref pos);
                _ = objects[ReadU16(stream, ref pos)];
                c.VisibleKind = FrameEventKind.UpdateResource;                c.Label = $"ResourceBarrier {kind}";
                break;
            }
            case CommandOpcode.BeginSample:
            {
                string? label = objects[ReadU16(stream, ref pos)] as string;
                c.VisibleKind = FrameEventKind.BeginSample;
                c.Label = label ?? "BeginSample";
                break;
            }
            case CommandOpcode.EndSample:
                c.Label = "EndSample";
                break;
            default:
                // Resource lifecycle and any future opcodes: skip payloads carefully.
                SkipUnknown(op, stream, ref pos, objects);
                c.VisibleKind = FrameEventKind.UpdateResource;
                c.Label = op.ToString();
                break;
        }

        return c;
    }

    private static void SkipUnknown(CommandOpcode op, ReadOnlySpan<byte> stream, ref int pos, IReadOnlyList<object?> objects)
    {
        // Mirror OpenGLCommandExecutor payload sizes for lifecycle ops.
        switch (op)
        {
            case CommandOpcode.CreateBuffer:
                _ = ReadU16(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                SkipBlob(stream, ref pos);
                break;
            case CommandOpcode.DisposeBuffer:
            case CommandOpcode.CreateTexture:
            case CommandOpcode.DisposeTexture:
            case CommandOpcode.GenerateMipmap:
            case CommandOpcode.CreateFramebufferOp:
            case CommandOpcode.DisposeFramebuffer:
            case CommandOpcode.CompileShader:
            case CommandOpcode.DisposeShader:
            case CommandOpcode.DisposeVertexArray:
                _ = ReadU16(stream, ref pos);
                break;
            case CommandOpcode.AllocateTexture2D:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                SkipBlob(stream, ref pos);
                break;
            case CommandOpcode.AllocateTextureCubeFace:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                SkipBlob(stream, ref pos);
                break;
            case CommandOpcode.AllocateTexture3D:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                _ = ReadU32(stream, ref pos);
                SkipBlob(stream, ref pos);
                break;
            case CommandOpcode.UpdateTexture3D:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos); // mip
                _ = ReadI32(stream, ref pos); _ = ReadI32(stream, ref pos); _ = ReadI32(stream, ref pos);
                _ = ReadU32(stream, ref pos); _ = ReadU32(stream, ref pos); _ = ReadU32(stream, ref pos);
                SkipBlob(stream, ref pos);
                break;
            case CommandOpcode.SetTextureWrap:
                _ = ReadU16(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.SetTextureFiltersOp:
                _ = ReadU16(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.SetTextureCompareMode:
                _ = ReadU16(stream, ref pos);
                _ = ReadU8(stream, ref pos);
                break;
            case CommandOpcode.GetTextureData:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU16(stream, ref pos);
                break;
            case CommandOpcode.GetTextureCubeFaceData:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadU16(stream, ref pos);
                break;
            case CommandOpcode.GetTextureDataPtr:
                _ = ReadU16(stream, ref pos);
                _ = ReadI32(stream, ref pos);
                _ = ReadStruct<long>(stream, ref pos);
                break;
            case CommandOpcode.CreateVertexArrayOp:
                _ = ReadU16(stream, ref pos);
                break;
            default:
                if (ComputeCommandLayout.TrySkip(op, stream, ref pos, objects))
                    break;
                throw new InvalidOperationException($"FrameDebugger decoder cannot skip opcode {op} at pos {pos}.");
        }
    }

    internal static RenderTargetInfo SnapshotFramebuffer(GraphicsFrameBuffer? fb) =>
        SnapshotFramebuffer(fb, out _);

    internal static RenderTargetInfo SnapshotFramebuffer(GraphicsFrameBuffer? fb, out Resources.RenderTexture? liveTexture)
    {
        liveTexture = null;
        if (fb == null)
            return RenderTargetInfo.Backbuffer();

        liveTexture = RenderTargetRegistry.Resolve(fb);
        bool transient = liveTexture is { IsDisposed: false } && liveTexture.IsTransient;

        string colorFormat = "";
        string depthFormat = "";
        int colorCount = 0;
        string name = string.IsNullOrEmpty(fb.DebugName) ? $"FBO {fb.Width}x{fb.Height}" : fb.DebugName!;
        GraphicsFrameBuffer.Attachment[] attachments = fb.Attachments;
        for (int i = 0; i < attachments.Length; i++)
        {
            GraphicsFrameBuffer.Attachment a = attachments[i];
            GraphicsTexture? tex = a.Texture;
            string fmt = tex?.ImageFormat.ToString() ?? "?";
            if (a.IsDepth)
                depthFormat = fmt;
            else
            {
                if (colorCount == 0)
                    colorFormat = fmt;
                colorCount++;
            }
        }

        return new RenderTargetInfo(
            name, fb.Width, fb.Height, colorFormat, depthFormat, colorCount,
            isBackbuffer: false, isTransient: transient);
    }

    private static PropertyState? CloneOwned(PropertyState? source, List<PropertyState> owned)
    {
        if (source == null) return null;
        var clone = new PropertyState(source);
        owned.Add(clone);
        return clone;
    }

    private static string DescribeShader(object? shader) => shader switch
    {
        null => "(null)",
        GraphicsProgram p => $"Program#{p.Handle}",
        ShaderVariant v => v.SourcePass != null
            ? (string.IsNullOrEmpty(v.SourcePass.ShaderName)
                ? v.SourcePass.Name
                : $"{v.SourcePass.ShaderName}/{v.SourcePass.Name}")
            : $"Variant#{v.Id}",
        _ => shader.GetType().Name,
    };

    private static string DescribeTexture(object? obj) => obj switch
    {
        null => "(null)",
        Texture2D t => string.IsNullOrEmpty(t.Name) ? $"Texture2D {t.Width}x{t.Height}" : t.Name,
        Texture3D t => string.IsNullOrEmpty(t.Name) ? "Texture3D" : t.Name,
        Cubemap t => string.IsNullOrEmpty(t.Name) ? "Cubemap" : t.Name,
        GraphicsTexture t => $"GfxTex#{t.Handle} {t.ImageFormat}",
        _ => obj.GetType().Name,
    };

    private static string DescribeBuffer(object? obj) => obj switch
    {
        null => "(null)",
        GraphicsBuffer b => $"Buffer#{b.Handle} {b.SizeInBytes}B",
        _ => obj.GetType().Name,
    };

    private static void SkipBlob(ReadOnlySpan<byte> s, ref int pos) => _ = ReadStruct<TransientStore.Ref>(s, ref pos);

    private static CommandOpcode ReadOpcode(ReadOnlySpan<byte> s, ref int pos)
    {
        var v = MemoryMarshal.Read<CommandOpcode>(s.Slice(pos));
        pos += sizeof(ushort);
        return v;
    }

    private static byte ReadU8(ReadOnlySpan<byte> s, ref int pos) { byte v = s[pos]; pos += 1; return v; }
    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<ushort>(s.Slice(pos)); pos += sizeof(ushort); return v; }
    private static int ReadI32(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<int>(s.Slice(pos)); pos += sizeof(int); return v; }
    private static uint ReadU32(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<uint>(s.Slice(pos)); pos += sizeof(uint); return v; }
    private static float ReadF32(ReadOnlySpan<byte> s, ref int pos) { var v = MemoryMarshal.Read<float>(s.Slice(pos)); pos += sizeof(float); return v; }

    private static T ReadStruct<T>(ReadOnlySpan<byte> s, ref int pos) where T : unmanaged
    {
        var v = MemoryMarshal.Read<T>(s.Slice(pos));
        pos += UnsafeSizeOf<T>();
        return v;
    }

    private static int UnsafeSizeOf<T>() where T : unmanaged => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
}
