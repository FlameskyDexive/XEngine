// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>Sticky GPU state reconstructed by walking a <see cref="FrameCapture"/> to a leaf event.</summary>
public sealed class GpuStateSnapshot
{
    public RenderTargetInfo RenderTarget { get; init; } = RenderTargetInfo.Backbuffer();
    public RasterizerState Raster { get; init; } = new();
    public string ShaderLabel { get; init; } = "";
    public string ShaderAssetName { get; init; } = "";
    public string ShaderPassName { get; init; } = "";
    public string VariantKeywords { get; init; } = "";
    public PropertyState? MaterialProperties { get; init; }
    public PropertyState? InstanceProperties { get; init; }
    public PropertyState Globals { get; init; } = new();
    public Dictionary<string, float> UniformFloats { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> UniformInts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Float2> UniformVec2 { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Float3> UniformVec3 { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Float4> UniformVec4 { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Float4x4> UniformMatrices { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UniformTextures { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UniformBuffers { get; } = new(StringComparer.Ordinal);
    public int ViewportX { get; init; }
    public int ViewportY { get; init; }
    public uint ViewportW { get; init; }
    public uint ViewportH { get; init; }
    public bool ScissorEnabled { get; init; }
    public Topology Topology { get; init; }
    public uint IndexCount { get; init; }
    public uint InstanceCount { get; init; }
    public DrawAnnotation? Annotation { get; init; }
    public FrameEventKind EventKind { get; init; }
    public string EventLabel { get; init; } = "";
    public string? CommandBufferName { get; init; }
    public bool ClearedColorInPass { get; init; }
    public bool ClearedDepthInPass { get; init; }
    public bool ClearedStencilInPass { get; init; }

    /// <summary>Derived attachment actions for the Unity-style "Color Actions / Depth Actions"
    /// rows. Store is always Store (no DontCare/memoryless in the engine).</summary>
    public FrameDebuggerLoadAction ColorLoadAction => ClearedColorInPass ? FrameDebuggerLoadAction.Clear : FrameDebuggerLoadAction.Load;
    public FrameDebuggerLoadAction DepthLoadAction => ClearedDepthInPass ? FrameDebuggerLoadAction.Clear : FrameDebuggerLoadAction.Load;
    public FrameDebuggerLoadAction StencilLoadAction => ClearedStencilInPass ? FrameDebuggerLoadAction.Clear : FrameDebuggerLoadAction.Load;

    /// <summary>Clear values recorded by the pass's ClearRenderTarget (valid for the components
    /// whose Cleared*InPass flag is set).</summary>
    public Color PassClearColor { get; init; }
    public float PassClearDepth { get; init; } = 1f;
    public int PassClearStencil { get; init; }
}

/// <summary>CPU sticky-state walk over a frozen <see cref="FrameCapture"/>.</summary>
public static class GpuStateRebuilder
{
    public static GpuStateSnapshot Rebuild(FrameCapture capture, int leafEventIndex)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.LeafEvents.Count == 0)
            return new GpuStateSnapshot();

        leafEventIndex = Math.Clamp(leafEventIndex, 0, capture.LeafEvents.Count - 1);
        FrameEvent leaf = capture.LeafEvents[leafEventIndex];
        int endOp = leaf.OpIndex;

        RenderTargetInfo rt = RenderTargetInfo.Backbuffer();
        RasterizerState raster = new();
        string shaderLabel = "";
        string shaderAsset = "";
        string shaderPassName = "";
        string variantKeywords = "";
        PropertyState? material = null;
        PropertyState? instance = null;
        var globals = new PropertyState();
        var floats = new Dictionary<string, float>(StringComparer.Ordinal);
        var ints = new Dictionary<string, int>(StringComparer.Ordinal);
        var v2 = new Dictionary<string, Float2>(StringComparer.Ordinal);
        var v3 = new Dictionary<string, Float3>(StringComparer.Ordinal);
        var v4 = new Dictionary<string, Float4>(StringComparer.Ordinal);
        var mats = new Dictionary<string, Float4x4>(StringComparer.Ordinal);
        var texs = new Dictionary<string, string>(StringComparer.Ordinal);
        var bufs = new Dictionary<string, string>(StringComparer.Ordinal);
        int vpX = 0, vpY = 0;
        uint vpW = 0, vpH = 0;
        bool scissor = false;
        Topology topo = Topology.Triangles;
        uint indexCount = 0, instanceCount = 0;
        bool clearedColor = false, clearedDepth = false, clearedStencil = false;
        Color passClearColor = default;
        float passClearDepth = 1f;
        int passClearStencil = 0;
        DrawAnnotation? annotation = null;
        CapturedOp? end = null;

        for (int i = 0; i <= endOp && i < capture.Ops.Count; i++)
        {
            CapturedOp op = capture.Ops[i];
            end = op;
            switch (op.Opcode)
            {
                case CommandOpcode.SetRenderTarget:
                case CommandOpcode.SetRenderTargets:
                    if (op.RenderTarget.HasValue)
                    {
                        rt = op.RenderTarget.Value;
                        clearedColor = false;
                        clearedDepth = false;
                        clearedStencil = false;
                    }
                    break;
                case CommandOpcode.SetViewport:
                    vpX = op.ViewportX; vpY = op.ViewportY; vpW = op.ViewportW; vpH = op.ViewportH;
                    break;
                case CommandOpcode.SetScissor:
                    scissor = true;
                    break;
                case CommandOpcode.DisableScissor:
                    scissor = false;
                    break;
                case CommandOpcode.ClearRenderTarget:
                    if ((op.ClearFlags & ClearFlags.Color) != 0)
                    {
                        clearedColor = true;
                        passClearColor = op.ClearColor;
                    }
                    if ((op.ClearFlags & ClearFlags.Depth) != 0)
                    {
                        clearedDepth = true;
                        passClearDepth = op.ClearDepth;
                    }
                    if ((op.ClearFlags & ClearFlags.Stencil) != 0)
                    {
                        clearedStencil = true;
                        passClearStencil = op.ClearStencil;
                    }
                    break;
                case CommandOpcode.SetRasterState:
                    if (op.Raster.HasValue) raster = op.Raster.Value;
                    break;
                case CommandOpcode.SetShader:
                    shaderLabel = op.ShaderLabel ?? "";
                    if (!string.IsNullOrEmpty(op.ShaderAssetName))
                        shaderAsset = op.ShaderAssetName!;
                    shaderPassName = op.ShaderPassName ?? "";
                    variantKeywords = op.VariantKeywords ?? "";
                    break;
                case CommandOpcode.SetProperties:
                case CommandOpcode.SetMaterialProperties:
                    material = op.MaterialProperties;
                    if (!string.IsNullOrEmpty(op.ShaderAssetName))
                        shaderAsset = op.ShaderAssetName!;
                    break;
                case CommandOpcode.ClearProperties:
                    material = null;
                    break;
                case CommandOpcode.SetInstanceProperties:
                    instance = op.InstanceProperties;
                    break;
                case CommandOpcode.ClearInstanceProperties:
                    instance = null;
                    break;
                case CommandOpcode.SetGlobalTexture:
                    if (op.UniformName != null)
                    {
                        // Track as texture label in globals-side map for UI.
                        texs["global:" + op.UniformName] = op.TextureLabel ?? "";
                    }
                    break;
                case CommandOpcode.ClearGlobalTexture:
                    if (op.UniformName != null)
                        texs.Remove("global:" + op.UniformName);
                    break;
                case CommandOpcode.SetGlobalInt:
                    if (op.UniformName != null) globals.SetInt(op.UniformName, op.UniformInt);
                    break;
                case CommandOpcode.SetGlobalFloat:
                    if (op.UniformName != null) globals.SetFloat(op.UniformName, op.UniformFloat);
                    break;
                case CommandOpcode.SetGlobalVec2:
                    if (op.UniformName != null) globals.SetVector(op.UniformName, op.UniformVec2);
                    break;
                case CommandOpcode.SetGlobalVec3:
                    if (op.UniformName != null) globals.SetVector(op.UniformName, op.UniformVec3);
                    break;
                case CommandOpcode.SetGlobalVec4:
                    if (op.UniformName != null) globals.SetVector(op.UniformName, op.UniformVec4);
                    break;
                case CommandOpcode.SetGlobalColor:
                    if (op.UniformName != null) globals.SetColor(op.UniformName, op.UniformColor);
                    break;
                case CommandOpcode.SetGlobalMatrix:
                    if (op.UniformName != null) globals.SetMatrix(op.UniformName, op.UniformMatrix);
                    break;
                case CommandOpcode.ClearAllGlobals:
                    globals = new PropertyState();
                    break;
                case CommandOpcode.SetUniformFloat:
                    if (op.UniformName != null) floats[op.UniformName] = op.UniformFloat;
                    break;
                case CommandOpcode.SetUniformInt:
                    if (op.UniformName != null) ints[op.UniformName] = op.UniformInt;
                    break;
                case CommandOpcode.SetUniformVec2:
                    if (op.UniformName != null) v2[op.UniformName] = op.UniformVec2;
                    break;
                case CommandOpcode.SetUniformVec3:
                    if (op.UniformName != null) v3[op.UniformName] = op.UniformVec3;
                    break;
                case CommandOpcode.SetUniformVec4:
                    if (op.UniformName != null) v4[op.UniformName] = op.UniformVec4;
                    break;
                case CommandOpcode.SetUniformMatrix:
                    if (op.UniformName != null) mats[op.UniformName] = op.UniformMatrix;
                    break;
                case CommandOpcode.SetUniformTexture:
                    if (op.UniformName != null) texs[op.UniformName] = op.TextureLabel ?? "";
                    break;
                case CommandOpcode.SetUniformBuffer:
                    if (op.UniformName != null) bufs[op.UniformName] = op.BufferLabel ?? "";
                    break;
                case CommandOpcode.SetGlobalBuffer:
                    if (op.UniformName != null) bufs["global:" + op.UniformName] = op.BufferLabel ?? "";
                    break;
                case CommandOpcode.DrawIndexed:
                case CommandOpcode.DrawIndexedInstanced:
                case CommandOpcode.DrawArrays:
                case CommandOpcode.DrawIndexedIndirect:
                    topo = op.Topology;
                    indexCount = op.IndexCount != 0 ? op.IndexCount : op.DrawArraysCount;
                    instanceCount = op.InstanceCount;
                    annotation = op.Annotation;
                    break;
                case CommandOpcode.SetComputeShader:
                case CommandOpcode.DispatchCompute:
                case CommandOpcode.DispatchIndirect:
                    shaderLabel = op.Label ?? shaderLabel;
                    break;
            }
        }

        var snap = new GpuStateSnapshot
        {
            RenderTarget = rt,
            Raster = raster,
            ShaderLabel = shaderLabel,
            ShaderAssetName = shaderAsset,
            ShaderPassName = shaderPassName,
            VariantKeywords = variantKeywords,
            MaterialProperties = material,
            InstanceProperties = instance,
            Globals = globals,
            ViewportX = vpX,
            ViewportY = vpY,
            ViewportW = vpW,
            ViewportH = vpH,
            ScissorEnabled = scissor,
            Topology = topo,
            IndexCount = indexCount,
            InstanceCount = instanceCount,
            Annotation = annotation ?? leaf.Annotation,
            EventKind = leaf.Kind,
            EventLabel = leaf.Label,
            CommandBufferName = leaf.CommandBufferName,
            ClearedColorInPass = clearedColor,
            ClearedDepthInPass = clearedDepth,
            ClearedStencilInPass = clearedStencil,
            PassClearColor = passClearColor,
            PassClearDepth = passClearDepth,
            PassClearStencil = passClearStencil,
        };
        foreach (var kv in floats) snap.UniformFloats[kv.Key] = kv.Value;
        foreach (var kv in ints) snap.UniformInts[kv.Key] = kv.Value;
        foreach (var kv in v2) snap.UniformVec2[kv.Key] = kv.Value;
        foreach (var kv in v3) snap.UniformVec3[kv.Key] = kv.Value;
        foreach (var kv in v4) snap.UniformVec4[kv.Key] = kv.Value;
        foreach (var kv in mats) snap.UniformMatrices[kv.Key] = kv.Value;
        foreach (var kv in texs) snap.UniformTextures[kv.Key] = kv.Value;
        foreach (var kv in bufs) snap.UniformBuffers[kv.Key] = kv.Value;
        _ = end;
        return snap;
    }
}
