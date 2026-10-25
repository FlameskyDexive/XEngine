// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>Which rendering scope a submitted command buffer belongs to. Editor chrome stays
/// <see cref="None"/> so truncate-replay never skips the editor's own drawing.</summary>
public enum FrameDebuggerScopeKind : byte
{
    None = 0,
    GameView = 1,
    SceneView = 2,
    /// <summary>Full-frame debug mode only: unscoped work inside BeginFrame..EndFrame.</summary>
    EditorFrame = 3,
}

/// <summary>What the Frame Debugger captures and truncates.</summary>
public enum FrameDebuggerCaptureScope : byte
{
    /// <summary>Unity behavior: game/scene view cameras, pipeline passes, and game UI only.</summary>
    GameRendering = 0,
    /// <summary>Debug mode: everything submitted inside the frame, editor UI included.</summary>
    FullFrame = 1,
}

/// <summary>Derived load action for a render-target attachment at the selected event.
/// The engine has no explicit load/store ops — Clear is derived from ClearRenderTarget
/// between the RT bind and the event; everything else displays as Load/Store.</summary>
public enum FrameDebuggerLoadAction : byte
{
    Load = 0,
    Clear = 1,
}

/// <summary>Kind of a tree-visible Frame Debugger leaf (or group marker).</summary>
public enum FrameEventKind : byte
{
    Group = 0,
    SetRenderTarget,
    Clear,
    Blit,
    Draw,
    DrawInstanced,
    SetShader,
    SetRaster,
    UpdateResource,
    BeginSample,
    Dispatch,
}

/// <summary>High-level mesh/material identity attached to a draw at record time.</summary>
public sealed class DrawAnnotation
{
    public string MeshName = "";
    public string MaterialName = "";
    public string ShaderName = "";
    public string PassName = "";
    public int PassIndex;
    public string[] Keywords = [];

    /// <summary>Owning GameObject name when the draw came from a scene renderable.</summary>
    public string ObjectName = "";

    /// <summary>Live mesh reference for the panel's mesh preview.</summary>
    public AssetRef<Mesh> Mesh;
}

/// <summary>A <see cref="DrawAnnotation"/> anchored to the command stream position it was
/// recorded at. The decoder attaches each draw leaf to the last record whose offset is at or
/// before the draw opcode — draw paths that never annotate can't shift the pairing.</summary>
public readonly struct AnnotationRecord
{
    public readonly int StreamOffset;
    public readonly DrawAnnotation Annotation;

    public AnnotationRecord(int streamOffset, DrawAnnotation annotation)
    {
        StreamOffset = streamOffset;
        Annotation = annotation;
    }
}

/// <summary>Copied render-target descriptor — safe after the live FBO is disposed.</summary>
public readonly struct RenderTargetInfo
{
    public readonly string Name;
    public readonly uint Width;
    public readonly uint Height;
    public readonly string ColorFormat;
    public readonly string DepthFormat;
    public readonly int ColorAttachmentCount;
    public readonly bool IsBackbuffer;

    /// <summary>Target came from the transient pool — its contents may be recycled.</summary>
    public readonly bool IsTransient;

    public RenderTargetInfo(
        string name, uint width, uint height, string colorFormat, string depthFormat,
        int colorAttachmentCount, bool isBackbuffer, bool isTransient = false)
    {
        Name = name ?? "";
        Width = width;
        Height = height;
        ColorFormat = colorFormat ?? "";
        DepthFormat = depthFormat ?? "";
        ColorAttachmentCount = colorAttachmentCount;
        IsBackbuffer = isBackbuffer;
        IsTransient = isTransient;
    }

    public static RenderTargetInfo Backbuffer(uint width = 0, uint height = 0) =>
        new("Backbuffer", width, height, "Default", "", 1, isBackbuffer: true);
}

/// <summary>A distinct render target seen during the capture, with a live texture reference
/// for the panel's RT dropdown preview (pinned by the capture until the next capture).</summary>
public sealed class CapturedRenderTarget
{
    public RenderTargetInfo Info { get; init; }
    public RenderTexture? Texture { get; init; }
}

/// <summary>One decoded command in capture order (includes sticky state ops not shown in the tree).</summary>
public sealed class CapturedOp
{
    internal CommandOpcode Opcode;
    public FrameEventKind VisibleKind;
    public bool ShowInTree;
    public string Label = "";
    public string? CommandBufferName;

    public RenderTargetInfo? RenderTarget;
    public RasterizerState? Raster;
    public ClearFlags ClearFlags;
    public Color ClearColor;
    public float ClearDepth;
    public int ClearStencil;

    public string? ShaderLabel;
    public PropertyState? MaterialProperties;
    public PropertyState? InstanceProperties;
    public string? ShaderAssetName;
    public string? ShaderPassName;
    public string? VariantKeywords;

    public string? UniformName;
    public float UniformFloat;
    public int UniformInt;
    public Float2 UniformVec2;
    public Float3 UniformVec3;
    public Float4 UniformVec4;
    public Color UniformColor;
    public Float4x4 UniformMatrix;
    public string? TextureLabel;
    public string? BufferLabel;
    public uint BufferBinding;

    public Topology Topology;
    public uint IndexCount;
    public uint InstanceCount;
    public uint StartIndex;
    public int BaseVertex;
    public int DrawArraysFirst;
    public uint DrawArraysCount;
    public bool Index32Bit;

    public DrawAnnotation? Annotation;

    /// <summary>Live texture of the render target bound by this op (SetRenderTarget ops only).
    /// Strong reference — pinned by the capture so the panel can always draw it safely.</summary>
    public RenderTexture? RenderTargetTexture;

    public int ViewportX, ViewportY;
    public uint ViewportW, ViewportH;
    public bool ScissorEnabled;
    public int ScissorX, ScissorY;
    public uint ScissorW, ScissorH;
}

/// <summary>Tree-visible leaf referencing a <see cref="CapturedOp"/>.</summary>
public sealed class FrameEvent
{
    public int Index;
    public int OpIndex;
    public FrameEventKind Kind;
    public string Label = "";
    public string? CommandBufferName;
    public DrawAnnotation? Annotation;
    public FrameDebuggerScopeKind Scope;
}

/// <summary>Hierarchical node for the editor tree (groups + leaves).</summary>
public sealed class FrameEventNode
{
    public string Label = "";
    public int Badge;
    public int? LeafEventIndex;
    public List<FrameEventNode> Children { get; } = new();
}

/// <summary>Frozen one-frame GPU event capture for the Frame Debugger.</summary>
public sealed class FrameCapture
{
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
    public FrameEventNode Root { get; init; } = new() { Label = "Frame Overview" };
    public IReadOnlyList<FrameEvent> LeafEvents { get; init; } = Array.Empty<FrameEvent>();
    public IReadOnlyList<CapturedOp> Ops { get; init; } = Array.Empty<CapturedOp>();
    public int DrawCount { get; init; }

    /// <summary>Mode this capture was recorded in.</summary>
    public FrameDebuggerCaptureScope CaptureScope { get; init; }

    /// <summary>Distinct offscreen render targets bound during the capture, in first-use order.
    /// Feeds the panel's RT dropdown.</summary>
    public IReadOnlyList<CapturedRenderTarget> RenderTargets { get; init; } = Array.Empty<CapturedRenderTarget>();

    /// <summary>Leaf count per <see cref="FrameDebuggerScopeKind"/> — replay frames are compared
    /// against these totals to detect a diverged (stale) capture.</summary>
    public IReadOnlyList<int> ScopeLeafTotals { get; init; } = Array.Empty<int>();

    /// <summary>False when the capture-frame GPU op count did not match the tree (backend hook
    /// drift) — truncation indices may land on the wrong op.</summary>
    public bool IndexMappingReliable { get; internal set; } = true;

    /// <summary>PropertyState clones owned by this capture (dispose/return not required — GC).</summary>
    internal List<PropertyState> OwnedPropertyStates { get; init; } = new();
}
