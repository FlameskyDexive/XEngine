// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Unity-style Frame Debugger facade. When enabled, the next full frame is decoded at
/// <see cref="Graphics.Submit"/> into a frozen <see cref="FrameCapture"/>. Closed path is a
/// single boolean check with zero allocation.
///
/// Capture and truncate-replay are scoped: only command buffers submitted inside a
/// <see cref="BeginTargetScope"/> region (game view cameras, game UI, scene view) are recorded
/// and skipped. The editor's own UI always draws fully, so stepping never flickers the window.
/// </summary>
public static class FrameDebugger
{
    private const int ScopeKindCount = 4;

    private static int s_enabled; // 0/1
    private static int s_capturing; // 0/1 — true only during the capture frame
    private static int s_pendingCapture; // capture on next BeginFrame
    private static int s_selectedEvent;
    private static int s_insideFrame; // between BeginFrame and EndFrameAndWait
    private static int s_captureScope; // (int)FrameDebuggerCaptureScope
    private static int s_captureStale; // sticky until Refresh/Enable/Disable
    private static int s_mainThreadId = -1;
    private static FrameCapture? s_lastCapture;
    private static FrameCaptureBuilder? s_builder;

    // Truncate-replay counters, one per FrameDebuggerScopeKind. "live" counts leaf GPU ops the
    // backends see this frame; "allowed" is the per-scope prefix length implied by the selected
    // event. Backends run on the render thread, selection changes on the main thread.
    private static readonly int[] s_liveCounters = new int[ScopeKindCount];
    private static readonly int[] s_allowedCounts = new int[ScopeKindCount];

    // Main-thread scope stack (Begin/EndTargetScope + Submit all happen on the main thread;
    // submissions from other threads read scope None by design).
    private static readonly FrameDebuggerScopeKind[] s_scopeStack = new FrameDebuggerScopeKind[16];
    private static int s_scopeDepth;

    /// <summary>True while the debugger is armed (capture and/or truncate preview).</summary>
    public static bool IsEnabled => Volatile.Read(ref s_enabled) != 0;

    /// <summary>True only while the current frame is being decoded into a capture.</summary>
    public static bool IsCapturing => Volatile.Read(ref s_capturing) != 0;

    /// <summary>True when replay frames no longer match the capture (scene changed) — the tree
    /// and truncation indices may be off until the user refreshes.</summary>
    public static bool IsCaptureStale => Volatile.Read(ref s_captureStale) != 0;

    /// <summary>What gets captured and truncated. Changing it while enabled re-captures.</summary>
    public static FrameDebuggerCaptureScope CaptureScope
    {
        get => (FrameDebuggerCaptureScope)Volatile.Read(ref s_captureScope);
        set
        {
            if (Volatile.Read(ref s_captureScope) == (int)value) return;
            Volatile.Write(ref s_captureScope, (int)value);
            if (IsEnabled)
                Refresh();
        }
    }

    /// <summary>Selected leaf event index for details + truncate preview.</summary>
    public static int SelectedEventIndex
    {
        get => Volatile.Read(ref s_selectedEvent);
        set
        {
            int max = s_lastCapture?.LeafEvents.Count ?? 0;
            int clamped = max <= 0 ? 0 : Math.Clamp(value, 0, max - 1);
            Volatile.Write(ref s_selectedEvent, clamped);
            RecomputeAllowedCounts();
        }
    }

    /// <summary>Most recent frozen capture, or null.</summary>
    public static FrameCapture? LastCapture => s_lastCapture;

    /// <summary>Marks subsequent main-thread submissions as belonging to a captured rendering
    /// scope (game view / scene view). Pair with <see cref="EndTargetScope"/> via try/finally.</summary>
    public static void BeginTargetScope(FrameDebuggerScopeKind kind)
    {
        if (s_scopeDepth < s_scopeStack.Length)
            s_scopeStack[s_scopeDepth] = kind;
        s_scopeDepth++;
    }

    public static void EndTargetScope()
    {
        if (s_scopeDepth > 0)
            s_scopeDepth--;
    }

    /// <summary>Scope stamped onto a command buffer at submit time. None whenever the debugger
    /// is off, the caller is not the main thread, or the submission is editor chrome.</summary>
    internal static FrameDebuggerScopeKind CurrentSubmitScope
    {
        get
        {
            if (Volatile.Read(ref s_enabled) == 0) return FrameDebuggerScopeKind.None;
            if (Environment.CurrentManagedThreadId != s_mainThreadId) return FrameDebuggerScopeKind.None;
            int depth = s_scopeDepth;
            if (depth > 0) return s_scopeStack[Math.Min(depth, s_scopeStack.Length) - 1];
            if (Volatile.Read(ref s_captureScope) == (int)FrameDebuggerCaptureScope.FullFrame
                && Volatile.Read(ref s_insideFrame) != 0)
                return FrameDebuggerScopeKind.EditorFrame;
            return FrameDebuggerScopeKind.None;
        }
    }

    /// <summary>Backends call this for each leaf GPU op (Draw/Clear/Blit/…). Returns true to
    /// skip the op. Ops outside a captured scope are never counted and never skipped.</summary>
    public static bool NoteLeafGpuOpAndShouldSkip(FrameDebuggerScopeKind scope)
    {
        if (scope == FrameDebuggerScopeKind.None) return false;
        if (Volatile.Read(ref s_enabled) == 0) return false;
        int k = (int)scope;
        if (Volatile.Read(ref s_capturing) != 0)
        {
            // Capture frame: count but never skip — OnEndFrame cross-checks these totals
            // against the tree so "leaf index == GPU skip index" is verified per backend.
            Interlocked.Increment(ref s_liveCounters[k]);
            return false;
        }
        if (s_lastCapture == null) return false;
        int idx = Interlocked.Increment(ref s_liveCounters[k]) - 1;
        return idx >= Volatile.Read(ref s_allowedCounts[k]);
    }

    public static void Enable()
    {
        PauseGameForCapture();
        Volatile.Write(ref s_enabled, 1);
        Volatile.Write(ref s_pendingCapture, 1);
        Volatile.Write(ref s_captureStale, 0);
    }

    public static void Disable()
    {
        Volatile.Write(ref s_enabled, 0);
        Volatile.Write(ref s_capturing, 0);
        Volatile.Write(ref s_pendingCapture, 0);
        Volatile.Write(ref s_captureStale, 0);
        s_builder = null;
        s_lastCapture = null;
        Volatile.Write(ref s_selectedEvent, 0);
        for (int k = 0; k < ScopeKindCount; k++)
        {
            Volatile.Write(ref s_liveCounters[k], 0);
            Volatile.Write(ref s_allowedCounts[k], 0);
        }
    }

    /// <summary>Discard the current capture and grab the next frame.</summary>
    public static void Refresh()
    {
        Volatile.Write(ref s_captureStale, 0);
        if (Volatile.Read(ref s_enabled) == 0)
        {
            Enable();
        }
        else
        {
            PauseGameForCapture();
            Volatile.Write(ref s_pendingCapture, 1);
        }
    }

    /// <summary>Unity behavior: arming the Frame Debugger pauses play mode so the captured
    /// frame stays frozen while stepping events (rendering keeps running; gameplay stops).
    /// Disable does not resume — the user unpauses via the editor, same as Unity.</summary>
    private static void PauseGameForCapture()
    {
        if (Application.IsPlaying && !Application.IsPaused)
            Application.IsPaused = true;
    }

    /// <summary>True when groups/ops should be recorded for the current main-thread region:
    /// capturing, and (in game mode) inside a target scope.</summary>
    private static bool ShouldRecordStructure =>
        Volatile.Read(ref s_capturing) != 0
        && (Volatile.Read(ref s_captureScope) == (int)FrameDebuggerCaptureScope.FullFrame
            || s_scopeDepth > 0);

    public static void BeginGroup(string label)
    {
        if (!ShouldRecordStructure) return;
        s_builder?.BeginGroup(label);
    }

    /// <summary>Unity-style camera group: "{PipelineType}.Render: {camera}". Allocating label
    /// builder — only called when capturing (not on the steady-state hot path).</summary>
    public static void BeginCameraGroup(Camera camera, RenderPipeline? pipeline = null)
    {
        if (!ShouldRecordStructure) return;
        string camName = camera.GameObject != null ? camera.GameObject.Name : "Camera";
        string pipelineName = pipeline?.GetType().Name ?? "RenderPipeline";
        s_builder?.BeginGroup(pipelineName + ".Render: " + camName);
    }

    /// <summary>Allocating label builder — only called when capturing.</summary>
    public static void BeginPassGroup(RenderPass pass)
    {
        if (!ShouldRecordStructure) return;
        s_builder?.BeginGroup(pass.ResolveDisplayName());
    }

    public static void EndGroup()
    {
        if (!ShouldRecordStructure) return;
        s_builder?.EndGroup();
    }

    /// <summary>Called from <see cref="Graphics.BeginFrame"/>.</summary>
    internal static void OnBeginFrame()
    {
        s_mainThreadId = Environment.CurrentManagedThreadId;
        Volatile.Write(ref s_insideFrame, 1);
        for (int k = 0; k < ScopeKindCount; k++)
            Volatile.Write(ref s_liveCounters[k], 0);
        if (Volatile.Read(ref s_enabled) == 0) return;
        if (Volatile.Read(ref s_pendingCapture) == 0) return;

        Volatile.Write(ref s_pendingCapture, 0);
        Volatile.Write(ref s_capturing, 1);
        s_builder = new FrameCaptureBuilder(CaptureScope);
    }

    /// <summary>Called from <see cref="Graphics.EndFrameAndWait"/> (render thread drained).</summary>
    internal static void OnEndFrame()
    {
        Volatile.Write(ref s_insideFrame, 0);
        if (Volatile.Read(ref s_capturing) != 0)
        {
            FrameCaptureBuilder? builder = s_builder;
            s_builder = null;
            Volatile.Write(ref s_capturing, 0);
            if (builder == null) return;
            FrameCapture capture = builder.Build();
            VerifyCaptureIndexMapping(capture);
            s_lastCapture = capture;
            if (Volatile.Read(ref s_selectedEvent) >= capture.LeafEvents.Count)
                Volatile.Write(ref s_selectedEvent, Math.Max(0, capture.LeafEvents.Count - 1));
            RecomputeAllowedCounts();
            Volatile.Write(ref s_captureStale, 0);
            return;
        }

        DetectStaleCapture();
    }

    /// <summary>Decode and attach a submitted command buffer while capturing.</summary>
    internal static void Ingest(CommandBuffer cmd)
    {
        if (Volatile.Read(ref s_capturing) == 0) return;
        if (cmd._fdScopeKind == FrameDebuggerScopeKind.None) return;
        s_builder?.Ingest(cmd);
    }

    /// <summary>Cross-backend invariant: the GPU op count seen by the executing backend during
    /// the capture frame must equal the tree's leaf count per scope, or truncation indices
    /// would land on the wrong op. All-zero live counters (headless / backend without hooks)
    /// means nothing executed — not a mismatch.</summary>
    private static void VerifyCaptureIndexMapping(FrameCapture capture)
    {
        int liveSum = 0;
        for (int k = 0; k < ScopeKindCount; k++)
            liveSum += Volatile.Read(ref s_liveCounters[k]);
        if (liveSum == 0) return;

        for (int k = 0; k < ScopeKindCount; k++)
        {
            int live = Volatile.Read(ref s_liveCounters[k]);
            int tree = k < capture.ScopeLeafTotals.Count ? capture.ScopeLeafTotals[k] : 0;
            if (live != tree)
            {
                capture.IndexMappingReliable = false;
                Debug.LogWarning(
                    $"FrameDebugger: capture index mismatch for scope {(FrameDebuggerScopeKind)k}: " +
                    $"backend executed {live} leaf ops but the tree recorded {tree}. " +
                    "Truncate preview may skip the wrong ops.");
            }
        }
    }

    /// <summary>Replay frames whose per-scope op counts differ from the capture mean the frame
    /// content changed (moving scene, resized view). A scope that did not render at all (0)
    /// is fine — e.g. a hidden scene view.</summary>
    private static void DetectStaleCapture()
    {
        if (Volatile.Read(ref s_enabled) == 0) return;
        FrameCapture? capture = s_lastCapture;
        if (capture == null) return;

        for (int k = 0; k < ScopeKindCount; k++)
        {
            int live = Volatile.Read(ref s_liveCounters[k]);
            if (live == 0) continue;
            int total = k < capture.ScopeLeafTotals.Count ? capture.ScopeLeafTotals[k] : 0;
            if (live != total)
            {
                Volatile.Write(ref s_captureStale, 1);
                return;
            }
        }
    }

    /// <summary>Per-scope allowed prefix implied by the selected event: how many leaf ops of
    /// each scope fall in the global leaf range [0..selected]. A scope with no events in the
    /// capture at all is left untouched (allowed = MaxValue) — e.g. a scene view that starts
    /// repainting after the capture must render normally, not go black.</summary>
    private static void RecomputeAllowedCounts()
    {
        FrameCapture? capture = s_lastCapture;
        Span<int> counts = stackalloc int[ScopeKindCount];
        counts.Clear();
        if (capture != null)
        {
            int sel = Volatile.Read(ref s_selectedEvent);
            IReadOnlyList<FrameEvent> leaves = capture.LeafEvents;
            for (int i = 0; i <= sel && i < leaves.Count; i++)
                counts[(int)leaves[i].Scope]++;
        }
        for (int k = 0; k < ScopeKindCount; k++)
        {
            bool scopeInCapture = capture != null
                && k < capture.ScopeLeafTotals.Count
                && capture.ScopeLeafTotals[k] > 0;
            Volatile.Write(ref s_allowedCounts[k], scopeInCapture ? counts[k] : int.MaxValue);
        }
    }

    /// <summary>Record draw identity while capturing (sidecar anchored at the current stream
    /// offset, not an opcode). Call immediately before encoding the draw.</summary>
    internal static void RecordDrawAnnotation(
        CommandBuffer cmd, Mesh? mesh, Material material, int passIndex, string? objectName = null)
    {
        if (Volatile.Read(ref s_capturing) == 0) return;
        cmd.AddDrawAnnotation(CreateAnnotation(mesh, material, passIndex, objectName));
    }

    internal static DrawAnnotation CreateAnnotation(
        Mesh? mesh, Material material, int passIndex, string? objectName = null)
    {
        string[] keywords = SnapshotKeywords(material);
        string passName = "";
        try
        {
            var pass = material.Shader?.GetPass(passIndex);
            passName = pass?.Name ?? "";
        }
        catch
        {
            // ignore
        }

        return new DrawAnnotation
        {
            MeshName = mesh?.Name ?? "",
            MaterialName = material.Name ?? "",
            ShaderName = material.Shader?.Name ?? "",
            PassName = passName,
            PassIndex = passIndex,
            Keywords = keywords,
            ObjectName = objectName ?? "",
            Mesh = new AssetRef<Mesh>(mesh),
        };
    }

    private static string[] SnapshotKeywords(Material material)
    {
        Dictionary<string, bool>? dict = material._localKeywords;
        if (dict == null || dict.Count == 0)
            return [];
        var list = new List<string>(dict.Count);
        foreach (KeyValuePair<string, bool> kv in dict)
        {
            if (kv.Value)
                list.Add(kv.Key);
        }
        list.Sort(StringComparer.Ordinal);
        return list.ToArray();
    }
}

/// <summary>Builds a <see cref="FrameCapture"/> during one frame.</summary>
internal sealed class FrameCaptureBuilder
{
    private readonly FrameEventNode _root = new() { Label = "Frame Overview" };
    private readonly Stack<FrameEventNode> _groupStack = new();
    private readonly List<FrameEvent> _leaves = new();
    private readonly List<CapturedOp> _ops = new();
    private readonly List<PropertyState> _owned = new();
    private readonly List<CapturedRenderTarget> _renderTargets = new();
    private readonly HashSet<string> _renderTargetNames = new(StringComparer.Ordinal);
    private readonly int[] _scopeLeafTotals = new int[4];
    private readonly FrameDebuggerCaptureScope _captureScope;
    private int _drawCount;

    public FrameCaptureBuilder(FrameDebuggerCaptureScope captureScope)
    {
        _captureScope = captureScope;
        _groupStack.Push(_root);
    }

    public void BeginGroup(string label)
    {
        var node = new FrameEventNode { Label = label ?? "" };
        Current.Children.Add(node);
        _groupStack.Push(node);
    }

    public void EndGroup()
    {
        if (_groupStack.Count <= 1) return;
        FrameEventNode node = _groupStack.Pop();
        node.Badge = CountDraws(node);
    }

    public void Ingest(CommandBuffer cmd)
    {
        string cbName = cmd.Name ?? "(unnamed)";
        FrameDebuggerScopeKind scope = cmd._fdScopeKind;
        // A CB named after its enclosing group (pass "ColorPass" submits CB "ColorPass")
        // would duplicate the row — Unity shows a single level.
        bool ownGroup = !string.Equals(Current.Label, cbName, StringComparison.Ordinal);
        if (ownGroup)
            BeginGroup(cbName);
        int startOp = _ops.Count;
        CommandBufferDecoder.Decode(cmd, cbName, cmd.DrawAnnotations, _ops, _owned);
        for (int i = startOp; i < _ops.Count; i++)
        {
            CapturedOp op = _ops[i];
            if (op.RenderTarget is { IsBackbuffer: false } rtInfo && _renderTargetNames.Add(rtInfo.Name))
                _renderTargets.Add(new CapturedRenderTarget { Info = rtInfo, Texture = op.RenderTargetTexture });
            if (!op.ShowInTree) continue;
            bool isDraw = op.VisibleKind is FrameEventKind.Draw or FrameEventKind.DrawInstanced;
            // Unity-style leaf label: "Draw Mesh <mesh>" when the recorder annotated identity,
            // raw GPU op label otherwise.
            if (isDraw && op.Annotation != null && !string.IsNullOrEmpty(op.Annotation.MeshName))
                op.Label = op.VisibleKind == FrameEventKind.DrawInstanced
                    ? $"Draw Mesh {op.Annotation.MeshName} (instanced)"
                    : $"Draw Mesh {op.Annotation.MeshName}";
            var leaf = new FrameEvent
            {
                Index = _leaves.Count,
                OpIndex = i,
                Kind = op.VisibleKind,
                Label = op.Label,
                CommandBufferName = cbName,
                Annotation = op.Annotation,
                Scope = scope,
            };
            _leaves.Add(leaf);
            _scopeLeafTotals[(int)scope]++;
            Current.Children.Add(new FrameEventNode
            {
                Label = leaf.Label,
                LeafEventIndex = leaf.Index,
                Badge = isDraw ? 1 : 0,
            });
            if (isDraw)
                _drawCount++;
        }
        if (ownGroup)
            EndGroup();
    }

    public FrameCapture Build()
    {
        while (_groupStack.Count > 1)
            EndGroup();
        _root.Badge = CountDraws(_root);
        return new FrameCapture
        {
            CapturedAt = DateTime.UtcNow,
            Root = _root,
            LeafEvents = _leaves,
            Ops = _ops,
            DrawCount = _drawCount,
            OwnedPropertyStates = _owned,
            CaptureScope = _captureScope,
            ScopeLeafTotals = _scopeLeafTotals,
            RenderTargets = _renderTargets,
        };
    }

    private FrameEventNode Current => _groupStack.Peek();

    private static int CountDraws(FrameEventNode node)
    {
        if (node.LeafEventIndex.HasValue)
            return node.Badge;
        int sum = 0;
        for (int i = 0; i < node.Children.Count; i++)
            sum += CountDraws(node.Children[i]);
        node.Badge = sum;
        return sum;
    }
}
