using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.VFX;

using Color = System.Drawing.Color;

namespace XEngine.VFX.Editor;

/// <summary>
/// Visual Effect Graph DockPanel: Origami NodeGraph (contexts, blocks, operators), blackboard,
/// compile, and an embedded preview strip.
/// </summary>
public sealed class VfxGraphWindow : DockPanel, IScriptReloadCleanup
{
    public override string Title => "Visual Effect Graph";
    public override string Icon => EditorIcons.WandMagicSparkles;

    private const float ToolbarHeight = 32f;
    private const float SplitterSize = 4f;
    private const float MinSidebar = 140f;
    private const float MaxSidebar = 320f;
    private const float MinPreview = 140f;
    private const float MaxPreview = 280f;

    private VfxGraphAsset? _graph;
    private bool _subscribed;
    private bool _graphDirty = true;
    private float _sidebarWidth = 188f;
    private float _previewWidth = 168f;
    private string? _selectedNodeId;
    private string _compileError = "";
    private VfxCompiledSettings? _previewSettings;
    private PreviewRenderer? _gpuPreview;
    private VfxGraphAsset? _gpuPreviewGraph;
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphConnection> _wires = new();
    private readonly NodeGraphController _ctrl = new();
    private readonly List<string> _portScratchIn = new();
    private readonly List<string> _portScratchOut = new();

    public VfxGraphAsset? Graph => _graph;
    public NodeGraphController GraphController => _ctrl;
    public IReadOnlyList<GraphNode> GraphNodes => _nodes;
    public IReadOnlyList<GraphConnection> Wires => _wires;
    public string? SelectedNodeId => _selectedNodeId;
    public string CompileError => _compileError;
    public VfxCompiledSettings? PreviewSettings => _previewSettings;
    public float SidebarWidth
    {
        get => _sidebarWidth;
        set => _sidebarWidth = Math.Clamp(value, MinSidebar, MaxSidebar);
    }
    public float PreviewWidth
    {
        get => _previewWidth;
        set => _previewWidth = Math.Clamp(value, MinPreview, MaxPreview);
    }

    [MenuItem("Window/Effects/Visual Effect Graph", priority: 23)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(VfxGraphWindow));

    [AssetDoubleClickHandler(".vfx")]
    private static bool OnDoubleClick(string relativePath, Guid guid)
    {
        if (guid == Guid.Empty) return false;
        if (AssetDatabase.Get(guid) is not VfxGraphAsset graph) return false;
        OpenGraph(graph);
        return true;
    }

    public static void OpenGraph(VfxGraphAsset? graph)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;
        app.OpenPanel(typeof(VfxGraphWindow));
        if (app.FindOpenPanel(typeof(VfxGraphWindow)) is VfxGraphWindow win)
            win.SetGraph(graph);
    }

    public void OnScriptReloadCleanup()
    {
        DisposeGpuPreview();
        SetGraph(null);
    }

    public override void OnClosed()
    {
        if (_subscribed) { Selection.OnSelectionChanged -= TryLoadFromSelection; _subscribed = false; }
        DisposeGpuPreview();
    }

    public override bool SerializeState(JsonObject state)
    {
        state["sidebarWidth"] = _sidebarWidth;
        state["previewWidth"] = _previewWidth;
        return true;
    }

    public override void RestoreState(JsonObject state)
    {
        if (state.TryGetPropertyValue("sidebarWidth", out JsonNode? sw) && sw is JsonValue sv && sv.TryGetValue(out float sidebar))
            _sidebarWidth = Math.Clamp(sidebar, MinSidebar, MaxSidebar);
        if (state.TryGetPropertyValue("previewWidth", out JsonNode? pw) && pw is JsonValue pv && pv.TryGetValue(out float preview))
            _previewWidth = Math.Clamp(preview, MinPreview, MaxPreview);
    }

    public void SetGraph(VfxGraphAsset? graph)
    {
        _graph = graph;
        _selectedNodeId = null;
        _compileError = "";
        _graphDirty = true;
        _gpuPreviewGraph = null;
        RefreshPreviewSettings();
        RebuildGraph();
    }

    public bool TryAddProperty(string name, VfxValueType type, string defaultValue = "")
    {
        if (_graph == null)
            return false;
        Undo.Snapshot(_graph);
        _graph.AddProperty(name, type, defaultValue);
        RefreshPreviewSettings();
        return true;
    }

    public bool TryRemoveProperty(string name)
    {
        if (_graph == null)
            return false;
        Undo.Snapshot(_graph);
        bool removed = _graph.RemoveProperty(name);
        if (removed)
            RefreshPreviewSettings();
        return removed;
    }

    public string CompilePreview()
    {
        if (_graph == null)
            return "";
        try
        {
            _compileError = "";
            RefreshPreviewSettings();
            return VfxGraphCompiler.CompileToSource(_graph);
        }
        catch (Exception ex)
        {
            _compileError = ex.Message;
            return "";
        }
    }

    public bool TryConnect(string fromNode, string fromPort, string toNode, string toPort)
    {
        if (_graph == null) return false;
        Undo.Snapshot(_graph);
        for (int i = _graph.Edges.Count - 1; i >= 0; i--)
        {
            VfxEdge e = _graph.Edges[i];
            if (string.Equals(e.ToNode, toNode, StringComparison.Ordinal)
                && string.Equals(e.ToPort, toPort, StringComparison.Ordinal))
                _graph.Edges.RemoveAt(i);
        }
        _graph.Edges.Add(new VfxEdge
        {
            Id = "e" + _graph.Edges.Count,
            FromNode = fromNode,
            FromPort = fromPort,
            ToNode = toNode,
            ToPort = toPort,
        });
        _graphDirty = true;
        RefreshPreviewSettings();
        RebuildGraph();
        return true;
    }

    public bool TryDisconnect(string toNode, string toPort)
    {
        if (_graph == null) return false;
        Undo.Snapshot(_graph);
        int removed = 0;
        for (int i = _graph.Edges.Count - 1; i >= 0; i--)
        {
            VfxEdge e = _graph.Edges[i];
            if (string.Equals(e.ToNode, toNode, StringComparison.Ordinal)
                && string.Equals(e.ToPort, toPort, StringComparison.Ordinal))
            {
                _graph.Edges.RemoveAt(i);
                removed++;
            }
        }
        if (removed == 0) return false;
        _graphDirty = true;
        RefreshPreviewSettings();
        RebuildGraph();
        return true;
    }

    public void ApplyNodePosition(string nodeId, float x, float y)
    {
        if (_graph == null) return;
        Undo.Snapshot(_graph);
        for (int i = 0; i < _graph.Contexts.Count; i++)
        {
            if (!string.Equals(_graph.Contexts[i].Id, nodeId, StringComparison.Ordinal))
                continue;
            _graph.Contexts[i].PosX = x;
            _graph.Contexts[i].PosY = y;
            _graphDirty = true;
            RebuildGraph();
            return;
        }
        for (int i = 0; i < _graph.Blocks.Count; i++)
        {
            if (!string.Equals(_graph.Blocks[i].Id, nodeId, StringComparison.Ordinal))
                continue;
            _graph.Blocks[i].PosX = x;
            _graph.Blocks[i].PosY = y;
            _graphDirty = true;
            RebuildGraph();
            return;
        }
        for (int i = 0; i < _graph.Operators.Count; i++)
        {
            if (!string.Equals(_graph.Operators[i].Id, nodeId, StringComparison.Ordinal))
                continue;
            _graph.Operators[i].PosX = x;
            _graph.Operators[i].PosY = y;
            _graphDirty = true;
            RebuildGraph();
            return;
        }
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        if (!_subscribed)
        {
            Selection.OnSelectionChanged += TryLoadFromSelection;
            _subscribed = true;
            TryLoadFromSelection();
        }

        if (_graphDirty)
            RebuildGraph();

        var font = EditorTheme.DefaultFont;
        using (paper.Column("vfx_root").Size(width, height).Enter())
        {
            DrawToolbar(paper, width, font);
            float bodyH = Math.Max(80f, height - ToolbarHeight);
            float sidebar = Math.Min(_sidebarWidth, Math.Max(MinSidebar, width * 0.22f));
            float preview = Math.Min(_previewWidth, Math.Max(MinPreview, width * 0.2f));
            using (paper.Row("vfx_body").Width(width).Height(bodyH).Enter())
            {
                DrawBlackboard(paper, sidebar, bodyH, font);
                paper.Box("vfx_split0").Width(SplitterSize).Height(bodyH).BackgroundColor(EditorTheme.BorderSoft);
                DrawCanvas(paper, width - sidebar - preview - SplitterSize * 2f, bodyH, font);
                paper.Box("vfx_split1").Width(SplitterSize).Height(bodyH).BackgroundColor(EditorTheme.BorderSoft);
                DrawPreview(paper, preview, bodyH, font);
            }
        }
    }

    private void TryLoadFromSelection()
    {
        if (Selection.ActiveObject is VfxGraphAsset graph)
        {
            if (graph != _graph)
                SetGraph(graph);
            return;
        }
        if (Selection.ActiveObject is not ContentItem ci || ci.IsFolder || ci.IsSubAsset) return;
        if (!ci.RelativePath.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase)) return;
        Guid guid = ci.Guid;
        if (guid == Guid.Empty)
        {
            var entry = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath);
            guid = entry?.Guid ?? Guid.Empty;
        }
        if (guid == Guid.Empty) return;
        if (AssetDatabase.Get(guid) is VfxGraphAsset loaded && loaded != _graph)
            SetGraph(loaded);
    }

    private void DrawToolbar(Paper paper, float width, FontFile? font)
    {
        using (paper.Row("vfx_tb").Width(width).Height(ToolbarHeight).Padding(8, 4, 4, 4).Enter())
        {
            string name = _graph == null ? "No graph" : _graph.GraphName;
            if (font != null)
                paper.Box("vfx_tb_name").Width(Math.Max(120f, width - 280f)).Height(24)
                    .Text(name, font).TextColor(EditorTheme.Ink500)
                    .Alignment(XEngine.PaperUI.TextAlignment.MiddleLeft);
            Origami.Button(paper, "vfx_compile", "Compile", () => CompilePreview()).Show();
            if (!string.IsNullOrEmpty(_compileError) && font != null)
                paper.Box("vfx_err").Width(160).Height(24).Text(_compileError, font).TextColor(EditorTheme.Red400);
        }
    }

    private void DrawBlackboard(Paper paper, float w, float h, FontFile? font)
    {
        using (paper.Column("vfx_bb").Width(w).Height(h).Padding(8, 8, 8, 8).BackgroundColor(EditorTheme.Neutral300).Enter())
        {
            if (font != null)
                paper.Box("vfx_bb_h").Height(22).Text("Blackboard", font).TextColor(EditorTheme.Ink500);
            if (_graph == null)
            {
                if (font != null)
                    paper.Box("vfx_bb_empty").Height(40).Text("Open a .vfx", font).TextColor(EditorTheme.Ink400);
                return;
            }
            Origami.EnumDropdown(paper, "vfx_template", _graph.Template, v =>
            {
                Undo.Snapshot(_graph);
                _graph.Template = v;
            }).Show();
            if (font != null)
            {
                paper.Box("vfx_cap").Height(20)
                    .Text("Capacity " + _graph.Capacity, font).TextColor(EditorTheme.Ink400);
            }
            Origami.Button(paper, "vfx_add_float", "Add Float", () => TryAddProperty("NewFloat", VfxValueType.Float, "0")).Show();
            Origami.Button(paper, "vfx_add_vec", "Add Vector", () => TryAddProperty("NewVector", VfxValueType.Float3, "0,0,0")).Show();
            Origami.Button(paper, "vfx_add_tex", "Add Texture", () => TryAddProperty("NewTexture", VfxValueType.Texture, "")).Show();
            for (int i = 0; i < _graph.Properties.Count; i++)
            {
                VfxProperty p = _graph.Properties[i];
                int index = i;
                using (paper.Column($"vfx_prop_{i}").Width(w - 16f).Enter())
                {
                    Origami.TextField(paper, $"vfx_pn{i}", p.Name, v =>
                    {
                        Undo.Snapshot(_graph);
                        _graph.Properties[index].Name = v;
                        _graph.Properties[index].DisplayName = v;
                    }).Show();
                    Origami.EnumDropdown(paper, $"vfx_pt{i}", p.Type, v =>
                    {
                        Undo.Snapshot(_graph);
                        _graph.Properties[index].Type = v;
                    }).Show();
                    Origami.TextField(paper, $"vfx_pv{i}", p.DefaultValue, v =>
                    {
                        Undo.Snapshot(_graph);
                        _graph.Properties[index].DefaultValue = v;
                        RefreshPreviewSettings();
                    }).Show();
                    Origami.Button(paper, $"vfx_pr{i}", "Remove", () => TryRemoveProperty(p.Name)).Show();
                }
            }
            if (font != null && _selectedNodeId != null)
                paper.Box("vfx_sel").Height(22).Text("Node " + _selectedNodeId, font).TextColor(EditorTheme.Ink500);
        }
    }

    private void DrawCanvas(Paper paper, float w, float h, FontFile? font)
    {
        if (_graph == null)
        {
            var empty = paper.Box("vfx_canvas_empty").Width(w).Height(h)
                .BackgroundColor(EditorTheme.Neutral100)
                .TextColor(EditorTheme.Ink400)
                .Alignment(XEngine.PaperUI.TextAlignment.MiddleCenter);
            if (font != null)
                empty.Text("Select a Visual Effect Graph", font);
            return;
        }

        Origami.NodeGraph(paper, "vfx_graph", w, h)
            .Nodes(_nodes)
            .Connections(_wires)
            .Controller(_ctrl)
            .Grid(true)
            .InitialView(new Float2(20, 20), 0.85f)
            .OnSelectionChanged(sel =>
            {
                _selectedNodeId = sel.Nodes.Count > 0 ? sel.Nodes[0].Id : null;
            })
            .OnNodesMoved((nodes, _) =>
            {
                if (_graph == null) return;
                Undo.Snapshot(_graph);
                for (int i = 0; i < nodes.Count; i++)
                    ApplyNodePosition(nodes[i].Id, nodes[i].Position.X, nodes[i].Position.Y);
            })
            .OnConnect(req => TryConnect(req.FromNode, req.FromPort, req.ToNode, req.ToPort))
            .OnDeleteSelection(sel =>
            {
                for (int i = 0; i < sel.Edges.Count; i++)
                    TryDisconnect(sel.Edges[i].ToNode, sel.Edges[i].ToPort);
            })
            .Show();
    }

    private void DrawPreview(Paper paper, float w, float h, FontFile? font)
    {
        using (paper.Column("vfx_prev").Width(w).Height(h).Padding(8, 8, 8, 8).BackgroundColor(EditorTheme.Neutral200).Enter())
        {
            if (font != null)
                paper.Box("vfx_prev_h").Height(22).Text("Preview", font).TextColor(EditorTheme.Ink500);
            if (_previewSettings == null)
            {
                if (font != null)
                    paper.Box("vfx_prev_empty").Height(40).Text("No graph", font).TextColor(EditorTheme.Ink400);
                return;
            }
            if (font != null)
            {
                paper.Box("vfx_prev_rate").Height(18)
                    .Text("Rate " + _previewSettings.SpawnRate, font).TextColor(EditorTheme.Ink400);
                paper.Box("vfx_prev_burst").Height(18)
                    .Text("Burst " + _previewSettings.BurstCount, font).TextColor(EditorTheme.Ink400);
                paper.Box("vfx_prev_life").Height(18)
                    .Text("Life " + _previewSettings.Lifetime, font).TextColor(EditorTheme.Ink400);
            }
            float swatch = Math.Min(w - 16f, 72f);
            Color c = Color.FromArgb(255,
                (int)Math.Clamp(_previewSettings.StartColor.R * 255f, 0f, 255f),
                (int)Math.Clamp(_previewSettings.StartColor.G * 255f, 0f, 255f),
                (int)Math.Clamp(_previewSettings.StartColor.B * 255f, 0f, 255f));
            paper.Box("vfx_prev_swatch").Width(swatch).Height(swatch).BackgroundColor(c);

            float gpuH = Math.Max(80f, h - 130f);
            if (TryEnsureGpuPreview())
            {
                VfxGpuPreview.Tick(_gpuPreview!, 1f / 60f);
                _gpuPreview!.DrawPreview(paper, "vfx_prev_gpu", w - 16f, gpuH);
            }
            else if (font != null)
            {
                paper.Box("vfx_prev_cpu").Height(18)
                    .Text("CPU preview (no compute device)", font).TextColor(EditorTheme.Ink400);
            }
        }
    }

    private bool TryEnsureGpuPreview()
    {
        if (_graph == null || !VfxGpuPreview.DeviceReady)
            return false;
        try
        {
            if (_gpuPreview == null)
            {
                _gpuPreview = new PreviewRenderer(256, 256);
                _gpuPreview.ShowGrid = true;
            }
            if (!ReferenceEquals(_gpuPreviewGraph, _graph))
            {
                VfxGpuPreview.Setup(_gpuPreview, _graph);
                _gpuPreviewGraph = _graph;
            }
            return _gpuPreview.Subject != null;
        }
        catch
        {
            DisposeGpuPreview();
            return false;
        }
    }

    private void DisposeGpuPreview()
    {
        _gpuPreview?.Dispose();
        _gpuPreview = null;
        _gpuPreviewGraph = null;
    }

    private void RefreshPreviewSettings()
    {
        _previewSettings = _graph == null ? null : VfxGraphCompiler.CompileSettings(_graph);
    }

    private void RebuildGraph()
    {
        _nodes.Clear();
        _wires.Clear();
        _graphDirty = false;
        if (_graph == null)
            return;

        for (int i = 0; i < _graph.Contexts.Count; i++)
        {
            VfxContext ctx = _graph.Contexts[i];
            AddNode(ctx.Id, ctx.Type, ctx.PosX, ctx.PosY, 176f, AccentFor(ctx.Type), flow: true);
        }
        for (int i = 0; i < _graph.Blocks.Count; i++)
        {
            VfxBlock block = _graph.Blocks[i];
            Float2 pos = BlockPosition(block);
            AddNode(block.Id, block.Type, pos.X, pos.Y, 160f, AccentFor(block.Type), flow: false);
        }
        for (int i = 0; i < _graph.Operators.Count; i++)
        {
            VfxOperator op = _graph.Operators[i];
            AddNode(op.Id, op.Type, op.PosX, op.PosY, 140f, AccentFor(op.Type), flow: false);
        }
        for (int i = 0; i < _graph.Edges.Count; i++)
        {
            VfxEdge e = _graph.Edges[i];
            _wires.Add(new GraphConnection(e.FromNode, e.FromPort, e.ToNode, e.ToPort));
        }
    }

    private void AddNode(string id, string type, float x, float y, float width, Color accent, bool flow)
    {
        VfxNodeLibrary.GetPorts(type, _portScratchIn, _portScratchOut);
        var node = new GraphNode
        {
            Id = id,
            Title = string.IsNullOrEmpty(type) ? id : type,
            Position = new Float2(x, y),
            Width = width,
            Accent = accent,
        };
        for (int p = 0; p < _portScratchIn.Count; p++)
        {
            var port = new GraphPort(_portScratchIn[p], _portScratchIn[p]);
            if (flow || _portScratchIn[p] == VfxNodeLibrary.FlowPort)
            {
                port.Side = PortSide.Top;
                port.Shape = PortShape.Arrow;
            }
            node.Inputs.Add(port);
        }
        for (int p = 0; p < _portScratchOut.Count; p++)
        {
            var port = new GraphPort(_portScratchOut[p], _portScratchOut[p]);
            if (flow || _portScratchOut[p] == VfxNodeLibrary.FlowPort)
            {
                port.Side = PortSide.Bottom;
                port.Shape = PortShape.Arrow;
            }
            node.Outputs.Add(port);
        }
        _nodes.Add(node);
    }

    private Float2 BlockPosition(VfxBlock block)
    {
        if (block.PosX != 0f || block.PosY != 0f)
            return new Float2(block.PosX, block.PosY);
        for (int i = 0; i < _graph!.Contexts.Count; i++)
        {
            if (!string.Equals(_graph.Contexts[i].Id, block.ContextId, StringComparison.Ordinal))
                continue;
            return new Float2(_graph.Contexts[i].PosX + 12f, _graph.Contexts[i].PosY + 52f + block.Order * 40f);
        }
        return new Float2(12f, 52f + block.Order * 40f);
    }

    private static Color AccentFor(string type)
    {
        if (type == VfxNodeLibrary.Subgraph)
            return Color.FromArgb(255, 180, 120, 220);
        if (VfxNodeLibrary.IsContext(type))
            return Color.FromArgb(255, 210, 90, 70);
        if (type == VfxNodeLibrary.Gravity || type == VfxNodeLibrary.ColorOverLife
            || type == VfxNodeLibrary.SizeOverLife
            || type == VfxNodeLibrary.SizeOverSpeed
            || type == VfxNodeLibrary.MultiplySize
            || type == VfxNodeLibrary.MultiplyColor
            || type == VfxNodeLibrary.AddSize
            || type == VfxNodeLibrary.AddColor
            || type == VfxNodeLibrary.AddVelocity
            || type == VfxNodeLibrary.AddPosition
            || type == VfxNodeLibrary.SetPosition
            || type == VfxNodeLibrary.MultiplyVelocity
            || type == VfxNodeLibrary.OverwriteSize
            || type == VfxNodeLibrary.OverwriteColor
            || type == VfxNodeLibrary.OverwriteVelocity
            || type == VfxNodeLibrary.OverwriteLifetime
            || type == VfxNodeLibrary.AddLifetime
            || type == VfxNodeLibrary.MultiplyLifetime
            || type == VfxNodeLibrary.AddAge
            || type == VfxNodeLibrary.MultiplyAge
            || type == VfxNodeLibrary.SetAge
            || type == VfxNodeLibrary.ColorOverSpeed
            || type == VfxNodeLibrary.ScreenSpaceSize
            || type == VfxNodeLibrary.OrientLookAt
            || type == VfxNodeLibrary.CollideWithPlane
            || type == VfxNodeLibrary.CollisionFriction
            || type == VfxNodeLibrary.CollisionElasticity
            || type == VfxNodeLibrary.CollisionLifetimeLoss
            || type == VfxNodeLibrary.LimitVelocity
            || type == VfxNodeLibrary.KillSphere
            || type == VfxNodeLibrary.KillAABox
            || type == VfxNodeLibrary.KillPlane
            || type == VfxNodeLibrary.Attract
            || type == VfxNodeLibrary.ConformToSphere
            || type == VfxNodeLibrary.Vortex
            || type == VfxNodeLibrary.ConformToCylinder
            || type == VfxNodeLibrary.CollideWithSDF
            || type == VfxNodeLibrary.CollideWithDepth
            || type == VfxNodeLibrary.ConformToSDF
            || type == VfxNodeLibrary.VectorField
            || type == VfxNodeLibrary.SpawnOverDistance
            || type == VfxNodeLibrary.PeriodicBurst
            || type == VfxNodeLibrary.GPUEventTarget
            || type == VfxNodeLibrary.TriggerEventRate
            || type == VfxNodeLibrary.TriggerEventOverDistance
            || type == VfxNodeLibrary.TriggerEventOnce
            || type == VfxNodeLibrary.FlipbookMotion
            || type == VfxNodeLibrary.FlipbookRate
            || type == VfxNodeLibrary.FlipbookStart
            || type == VfxNodeLibrary.InheritVelocity
            || type == VfxNodeLibrary.AttributeMap
            || type == VfxNodeLibrary.Distort
            || type == VfxNodeLibrary.ParticleLight
            || type == VfxNodeLibrary.Sort
            || type == VfxNodeLibrary.Turbulence)
            return Color.FromArgb(255, 90, 180, 220);
        if (VfxNodeLibrary.IsOperator(type))
            return Color.FromArgb(255, 120, 200, 140);
        return Color.FromArgb(255, 180, 180, 190);
    }

    /// <summary>CPU preview strip used when GPU compute is unavailable. Prefer <see cref="TryFillPreviewRgba"/>.</summary>
    public static void FillPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int dots = Math.Clamp((int)settings.SpawnRate / 8 + (int)settings.BurstCount / 32, 4, 48);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        for (int i = 0; i < dots; i++)
        {
            int x = 12 + (i * 37) % Math.Max(8, w - 24);
            int y = 36 + (i * 19) % Math.Max(8, h - 48);
            int r = Math.Max(1, (int)(settings.StartSize * 40f));
            FillCircle(rgba, w, h, x, y, r, br, bg, bb);
        }
    }

    /// <summary>GPU RT capture when compute is live; otherwise the CPU stand-in.</summary>
    public static bool TryFillPreviewRgba(
        byte[] rgba,
        int w,
        int h,
        VfxCompiledSettings settings,
        VfxGraphAsset? graph,
        out bool usedGpu)
    {
        usedGpu = false;
        if (graph != null && VfxGpuPreview.DeviceReady)
        {
            try
            {
                using var preview = new PreviewRenderer(w, h);
                preview.ShowGrid = true;
                VfxGpuPreview.Setup(preview, graph);
                for (int i = 0; i < 8; i++)
                    VfxGpuPreview.Tick(preview, 1f / 30f);
                if (VfxGpuPreview.TryCaptureRgba(preview, rgba, w, h))
                {
                    usedGpu = true;
                    return true;
                }
            }
            catch
            {
                // Headless / missing PSO — CPU path below.
            }
        }

        FillPreviewRgba(rgba, w, h, settings);
        return true;
    }

    /// <summary>CPU stand-in that modulates billboards with a checker, matching Output <c>_MainTex</c> sampling.</summary>
    public static void FillTexturedPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        FillPreviewRgba(rgba, w, h, settings);
        int dots = Math.Clamp((int)settings.SpawnRate / 8 + (int)settings.BurstCount / 32, 4, 48);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        for (int i = 0; i < dots; i++)
        {
            int x = 12 + (i * 37) % Math.Max(8, w - 24);
            int y = 36 + (i * 19) % Math.Max(8, h - 48);
            int r = Math.Max(1, (int)(settings.StartSize * 40f));
            FillCircleChecker(rgba, w, h, x, y, r, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: cells start at an offset frame, then wrap.</summary>
    public static void FillFlipbookStartPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float cols = Math.Max(1f, settings.FlipbookColumns);
        float rows = Math.Max(1f, settings.FlipbookRows);
        float frames = cols * rows;
        if (frames < 4f)
            frames = 4f;
        float start = settings.FlipbookStart != 0f ? settings.FlipbookStart : 2f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = i / (float)(dots - 1);
            float frameF = VfxFlipbook.Frame(age, 1f, frames, 0f, start);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            int br = 80 + (int)(frameF * 28f);
            if (br > 255) br = 255;
            FillCircle(rgba, w, h, sx, sy, 6, (byte)br, 160, 220);
            FillCircle(rgba, w, h, sx, sy - 18 - (int)(frameF * 6f), 3, 255, 210, 80);
        }
    }

    /// <summary>CPU stand-in: cells advance by <c>age * rate</c> instead of lifetime.</summary>
    public static void FillFlipbookRatePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float cols = Math.Max(1f, settings.FlipbookColumns);
        float rows = Math.Max(1f, settings.FlipbookRows);
        float frames = cols * rows;
        if (frames < 4f)
            frames = 4f;
        float rate = settings.FlipbookRate > 0f ? settings.FlipbookRate : 8f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = i * 0.15f;
            float frameF = VfxFlipbook.Frame(age, 1f, frames, rate);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            int br = 80 + (int)(frameF * 28f);
            if (br > 255) br = 255;
            FillCircle(rgba, w, h, sx, sy, 6, (byte)br, 180, 90);
            FillCircle(rgba, w, h, sx, sy - 18 - (int)(frameF * 6f), 3, 255, 210, 80);
        }
    }

    /// <summary>CPU stand-in that tints each billboard by its flipbook cell (age along lifetime).</summary>
    public static void FillFlipbookPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        FillPreviewRgba(rgba, w, h, settings);
        int dots = Math.Clamp((int)settings.SpawnRate / 8 + (int)settings.BurstCount / 32, 4, 48);
        float cols = Math.Max(1f, settings.FlipbookColumns);
        float rows = Math.Max(1f, settings.FlipbookRows);
        float life = Math.Max(0.01f, settings.Lifetime);
        for (int i = 0; i < dots; i++)
        {
            int x = 12 + (i * 37) % Math.Max(8, w - 24);
            int y = 36 + (i * 19) % Math.Max(8, h - 48);
            int radius = Math.Max(1, (int)(settings.StartSize * 40f));
            float age = life * i / Math.Max(1, dots - 1);
            VfxFlipbook.FrameUv(0.5f, 0.5f, age, life, cols, rows, out float au, out float av);
            int br = (int)Math.Clamp(settings.StartColor.R * 255f * au, 40f, 255f);
            int bg = (int)Math.Clamp(settings.StartColor.G * 255f * av, 40f, 255f);
            int bb = (int)Math.Clamp(settings.StartColor.B * 255f * (1f - au), 40f, 255f);
            FillCircle(rgba, w, h, x, y, radius, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: adjacent flipbook cells mix when FlipbookBlend is on.</summary>
    public static void FillFlipbookBlendPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float cols = Math.Max(1f, settings.FlipbookColumns);
        float rows = Math.Max(1f, settings.FlipbookRows);
        float blend = settings.FlipbookBlend > 0f ? settings.FlipbookBlend : 1f;
        float life = Math.Max(0.01f, settings.Lifetime);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = life * (i + 0.5f) / dots;
            VfxFlipbook.FrameUvBlend(0.5f, 0.5f, age, life, cols, rows, blend,
                out float u0, out float v0, out float u1, out float v1, out float weight);
            int r0 = (int)Math.Clamp(40f + u0 * 200f, 40f, 255f);
            int g0 = (int)Math.Clamp(40f + v0 * 200f, 40f, 255f);
            int b0 = (int)Math.Clamp(40f + (1f - u0) * 160f, 40f, 255f);
            int r1 = (int)Math.Clamp(40f + u1 * 200f, 40f, 255f);
            int g1 = (int)Math.Clamp(40f + v1 * 200f, 40f, 255f);
            int b1 = (int)Math.Clamp(40f + (1f - u1) * 160f, 40f, 255f);
            float rr = r0, gg = g0, bb = b0, aa = 1f;
            VfxFlipbook.Blend(ref rr, ref gg, ref bb, ref aa, r1, g1, b1, 1f, weight);
            int sx = 24 + i * Math.Max(8, (w - 40) / dots);
            int sy = (int)(h * 0.55f);
            FillCircle(rgba, w, h, sx, sy, 12, (int)rr, (int)gg, (int)bb);
            FillCircle(rgba, w, h, sx, sy - 28, 5, r0, g0, b0);
            FillCircle(rgba, w, h, sx, sy + 28, 5, r1, g1, b1);
        }
    }

    /// <summary>CPU stand-in: flipbook cells warp along a motion-vector map before blending.</summary>
    public static void FillFlipbookMotionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float cols = Math.Max(1f, settings.FlipbookColumns);
        float rows = Math.Max(1f, settings.FlipbookRows);
        float blend = settings.FlipbookBlend > 0f ? settings.FlipbookBlend : 1f;
        float scale = settings.FlipbookMotionScale > 0f ? settings.FlipbookMotionScale : 1f;
        float life = Math.Max(0.01f, settings.Lifetime);
        VfxFlipbookMotion.Decode(1f, 0.5f, out float mx, out float my);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = life * (i + 0.5f) / dots;
            VfxFlipbook.FrameUvBlend(0.5f, 0.5f, age, life, cols, rows, blend,
                out float u0, out float v0, out float u1, out float v1, out float weight);
            VfxFlipbookMotion.WarpPair(u0, v0, u1, v1, mx, my, mx, my, weight, scale,
                out float au, out float av, out float bu, out float bv);
            int r0 = (int)Math.Clamp(40f + au * 200f, 40f, 255f);
            int g0 = (int)Math.Clamp(40f + av * 200f, 40f, 255f);
            int b0 = (int)Math.Clamp(40f + (1f - au) * 160f, 40f, 255f);
            int r1 = (int)Math.Clamp(40f + bu * 200f, 40f, 255f);
            int g1 = (int)Math.Clamp(40f + bv * 200f, 40f, 255f);
            int b1 = (int)Math.Clamp(40f + (1f - bu) * 160f, 40f, 255f);
            float rr = r0, gg = g0, bb = b0, aa = 1f;
            VfxFlipbook.Blend(ref rr, ref gg, ref bb, ref aa, r1, g1, b1, 1f, weight);
            int sx = 24 + i * Math.Max(8, (w - 40) / dots);
            int shift = (int)(au * 18f);
            int sy = (int)(h * 0.55f);
            FillCircle(rgba, w, h, sx + shift, sy, 12, (int)rr, (int)gg, (int)bb);
            FillCircle(rgba, w, h, sx, sy - 28, 5, r0, g0, b0);
            FillCircle(rgba, w, h, sx + 10, sy + 28, 5, r1, g1, b1);
        }
    }

    /// <summary>CPU stand-in: color fades out and size shrinks over normalized age.</summary>
    public static void FillOverLifePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float from = settings.SizeOverLifeFrom;
        float to = settings.SizeOverLifeTo;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxOverLife.Color(t, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, out float cr, out float cg, out float cb, out float ca);
            float size = VfxOverLife.Size(settings.StartSize <= 0f ? 0.15f : settings.StartSize, t, from, to);
            int rad = Math.Max(2, (int)(size * 80f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            FillCircle(rgba, w, h, sx, sy, rad,
                Math.Clamp((int)(br * cr * ca), 16, 255),
                Math.Clamp((int)(bg * cg * ca), 16, 255),
                Math.Clamp((int)(bb * cb * ca), 16, 255));
        }
    }

    /// <summary>CPU stand-in: size is scaled by a constant Amount.</summary>
    public static void FillMultiplySizePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.MultiplySizeAmount != 1f ? settings.MultiplySizeAmount : 2f;
        float start = settings.StartSize <= 0f ? 0.12f : settings.StartSize;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float size = VfxMultiplySize.Apply(start, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: particle color is scaled by a constant RGBA.</summary>
    public static void FillMultiplyColorPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float mr = settings.MultiplyColorR != 1f ? settings.MultiplyColorR : 1f;
        float mg = settings.MultiplyColorG != 1f ? settings.MultiplyColorG : 0.45f;
        float mb = settings.MultiplyColorB != 1f ? settings.MultiplyColorB : 0.15f;
        float ma = settings.MultiplyColorA != 1f ? settings.MultiplyColorA : 1f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxMultiplyColor.Apply(1f, 0.85f + t * 0.15f, 0.7f, 1f, mr, mg, mb, ma, out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            FillCircle(rgba, w, h, sx, sy, 11,
                Math.Clamp((int)(r * 255f), 16, 255),
                Math.Clamp((int)(g * 255f), 16, 255),
                Math.Clamp((int)(b * 255f), 16, 255));
        }
    }

    /// <summary>CPU stand-in: size is offset by a constant Amount.</summary>
    public static void FillAddSizePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.AddSizeAmount != 0f ? settings.AddSizeAmount : 0.08f;
        float start = settings.StartSize <= 0f ? 0.12f : settings.StartSize;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float size = VfxAddSize.Apply(start, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 120, 255, 140);
        }
    }

    /// <summary>CPU stand-in: particle color is offset by a constant RGBA.</summary>
    public static void FillAddColorPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float ar = settings.AddColorR != 0f ? settings.AddColorR : 0.35f;
        float ag = settings.AddColorG != 0f ? settings.AddColorG : 0.1f;
        float ab = settings.AddColorB != 0f ? settings.AddColorB : 0f;
        float aa = settings.AddColorA;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxAddColor.Apply(0.35f, 0.4f + t * 0.2f, 0.55f, 1f, ar, ag, ab, aa, out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            FillCircle(rgba, w, h, sx, sy, 11,
                Math.Clamp((int)(r * 255f), 16, 255),
                Math.Clamp((int)(g * 255f), 16, 255),
                Math.Clamp((int)(b * 255f), 16, 255));
        }
    }

    /// <summary>CPU stand-in: particles pick up a constant velocity kick.</summary>
    public static void FillAddVelocityPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float ax = settings.AddVelocityX != 0f ? settings.AddVelocityX : 1.2f;
        float ay = settings.AddVelocityY;
        float az = settings.AddVelocityZ;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float vx = 0f, vy = 0f, vz = 0f;
            VfxAddVelocity.Apply(ref vx, ref vy, ref vz, ax, ay, az);
            float t = i / (float)(dots - 1);
            int sx = 28 + (int)(t * (w - 56) + vx * 8f);
            int sy = (int)(h * 0.58f - vy * 10f);
            FillCircle(rgba, w, h, sx, sy, 8, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, 5, 255, 210, 80);
        }
    }

    /// <summary>CPU stand-in: particles are offset by a constant XYZ.</summary>
    public static void FillAddPositionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float ax = settings.AddPositionX != 0f ? settings.AddPositionX : 0.8f;
        float ay = settings.AddPositionY;
        float az = settings.AddPositionZ;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float px = i * 0.15f, py = 0f, pz = 0f;
            VfxAddPosition.Apply(ref px, ref py, ref pz, ax, ay, az);
            int sx = 28 + (int)(px * 28f);
            int sy = (int)(h * 0.58f - py * 20f);
            FillCircle(rgba, w, h, sx, sy, 8, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, 5, 120, 255, 160);
        }
    }

    /// <summary>CPU stand-in: particles snap to a constant XYZ.</summary>
    public static void FillSetPositionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float ax = settings.SetPositionX != 0f ? settings.SetPositionX : 1.2f;
        float ay = settings.SetPositionY;
        float az = settings.SetPositionZ;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float px = i * 0.15f, py = 0.4f, pz = 0f;
            VfxSetPosition.Apply(ref px, ref py, ref pz, ax, ay, az);
            int sx = 28 + (int)(px * 28f);
            int sy = (int)(h * 0.58f - py * 20f);
            FillCircle(rgba, w, h, sx, sy, 8, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, 5, 255, 140, 200);
        }
    }

    /// <summary>CPU stand-in: velocity is scaled by a constant Amount.</summary>
    public static void FillMultiplyVelocityPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.MultiplyVelocityAmount != 1f ? settings.MultiplyVelocityAmount : 2f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float vx = 0.4f, vy = 0f, vz = 0f;
            VfxMultiplyVelocity.Apply(ref vx, ref vy, ref vz, amount);
            float t = i / (float)(dots - 1);
            int sx = 28 + (int)(t * (w - 56) + vx * 18f);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, 8, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, 5, 180, 120, 255);
        }
    }

    /// <summary>CPU stand-in: size is overwritten by a constant Amount.</summary>
    public static void FillSetSizePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.OverwriteSizeAmount != 0f ? settings.OverwriteSizeAmount : 0.22f;
        float start = settings.StartSize <= 0f ? 0.08f : settings.StartSize;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float size = VfxSetSize.Apply(start, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 255, 160, 80);
        }
    }

    /// <summary>CPU stand-in: color is overwritten by a constant RGBA.</summary>
    public static void FillOverwriteColorPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float ar = settings.OverwriteColorR != 0f ? settings.OverwriteColorR : 0.95f;
        float ag = settings.OverwriteColorG != 0f ? settings.OverwriteColorG : 0.35f;
        float ab = settings.OverwriteColorB != 0f ? settings.OverwriteColorB : 0.15f;
        float aa = settings.OverwriteColorA != 0f ? settings.OverwriteColorA : 1f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxSetColor.Apply(0.35f, 0.4f + t * 0.2f, 0.55f, 1f, ar, ag, ab, aa, out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, 11, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, 8,
                (byte)Math.Clamp((int)(r * 255f), 16, 255),
                (byte)Math.Clamp((int)(g * 255f), 16, 255),
                (byte)Math.Clamp((int)(b * 255f), 16, 255));
        }
    }

    /// <summary>CPU stand-in: velocity is overwritten by a constant XYZ.</summary>
    public static void FillOverwriteVelocityPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float ax = settings.OverwriteVelocityX != 0f ? settings.OverwriteVelocityX : 1.2f;
        float ay = settings.OverwriteVelocityY;
        float az = settings.OverwriteVelocityZ;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float vx = 0.2f, vy = 0.4f, vz = 0f;
            VfxSetVelocity.Apply(ref vx, ref vy, ref vz, ax, ay, az);
            float t = i / (float)(dots - 1);
            int sx = 28 + (int)(t * (w - 56) + vx * 8f);
            int sy = (int)(h * 0.58f - vy * 10f);
            FillCircle(rgba, w, h, sx, sy, 8, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, 5, 255, 140, 80);
        }
    }

    /// <summary>CPU stand-in: lifetime is overwritten by a constant Amount.</summary>
    public static void FillOverwriteLifetimePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.OverwriteLifetimeAmount != 0f ? settings.OverwriteLifetimeAmount : 1.5f;
        float start = settings.Lifetime <= 0f ? 0.4f : settings.Lifetime;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float life = VfxSetLifetime.Apply(start, amount);
            int rad = Math.Max(2, (int)(life * 8f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 180, 255, 120);
        }
    }

    /// <summary>CPU stand-in: lifetime is offset by a constant Amount.</summary>
    public static void FillAddLifetimePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.AddLifetimeAmount != 0f ? settings.AddLifetimeAmount : 0.8f;
        float start = settings.Lifetime <= 0f ? 0.4f : settings.Lifetime;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float life = VfxAddLifetime.Apply(start, amount);
            int rad = Math.Max(2, (int)(life * 8f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 255, 200, 80);
        }
    }

    /// <summary>CPU stand-in: lifetime is scaled by a constant Amount.</summary>
    public static void FillMultiplyLifetimePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.MultiplyLifetimeAmount != 1f ? settings.MultiplyLifetimeAmount : 2f;
        float start = settings.Lifetime <= 0f ? 0.4f : settings.Lifetime;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float life = VfxMultiplyLifetime.Apply(start, amount);
            int rad = Math.Max(2, (int)(life * 8f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 255, 160, 80);
        }
    }

    /// <summary>CPU stand-in: age is offset by a constant Amount.</summary>
    public static void FillAddAgePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.AddAgeAmount != 0f ? settings.AddAgeAmount : 0.5f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = VfxAddAge.Apply(0.2f + i * 0.05f, amount);
            int rad = Math.Max(2, (int)(age * 10f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 200, 120, 255);
        }
    }

    /// <summary>CPU stand-in: age is scaled by a constant Amount.</summary>
    public static void FillMultiplyAgePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.MultiplyAgeAmount != 1f ? settings.MultiplyAgeAmount : 2f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = VfxMultiplyAge.Apply(0.2f + i * 0.05f, amount);
            int rad = Math.Max(2, (int)(age * 10f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 255, 180, 80);
        }
    }

    /// <summary>CPU stand-in: age is overwritten by a constant Amount.</summary>
    public static void FillSetAgePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.SetAgeAmount != 0f ? settings.SetAgeAmount : 0.8f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float age = VfxSetAge.Apply(0.2f + i * 0.05f, amount);
            int rad = Math.Max(2, (int)(age * 10f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), 180, 255, 120);
        }
    }

    /// <summary>CPU stand-in: size grows with velocity length.</summary>
    public static void FillSizeOverSpeedPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.SizeOverSpeedAmount > 0f ? settings.SizeOverSpeedAmount : 0.35f;
        float start = settings.StartSize <= 0f ? 0.12f : settings.StartSize;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float speed = i * 0.55f;
            float size = VfxSizeOverSpeed.Apply(start, speed, 0f, 0f, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: color mixes from rest to a speed tint.</summary>
    public static void FillColorOverSpeedPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float scale = settings.ColorOverSpeedScale > 0f ? settings.ColorOverSpeedScale : 4f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float speed = i * (scale / (dots - 1));
            VfxColorOverSpeed.Apply(
                speed, 0f, 0f, scale,
                1f, 1f, 1f, 1f,
                0.2f, 0.85f, 1f, 1f,
                out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            FillCircle(rgba, w, h, sx, sy, 11,
                Math.Clamp((int)(r * 255f), 16, 255),
                Math.Clamp((int)(g * 255f), 16, 255),
                Math.Clamp((int)(b * 255f), 16, 255));
        }
    }

    /// <summary>CPU stand-in: farther particles grow to hold screen size.</summary>
    public static void FillScreenSpaceSizePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.ScreenSpaceSizeAmount > 0f ? settings.ScreenSpaceSizeAmount : 2f;
        float start = settings.StartSize <= 0f ? 0.12f : settings.StartSize;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float dist = 0.5f + i * 0.4f;
            float size = VfxScreenSpaceSize.Apply(start, dist, 0f, 0f, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy, Math.Max(1, rad - 2), br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: encode reconstructed screen motion as RGB discs.</summary>
    public static void FillMotionVectorPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float mode = settings.MotionVectorMode > 0f ? settings.MotionVectorMode : 1f;
        float scale = settings.MotionVectorScale > 0f ? settings.MotionVectorScale : 8f;
        const float dt = 1f / 30f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float ang = i * 0.62831853f;
            float vx = MathF.Cos(ang) * 2.4f;
            float vy = MathF.Sin(ang) * 2.4f;
            VfxMotionVector.PrevPosition(0f, 0f, 0f, vx, vy, 0f, dt, out float qx, out float qy, out _);
            VfxMotionVector.NdcDelta(0f, 0f, 1f, qx, qy, 1f, out float dx, out float dy);
            float r = 0.35f, g = 0.38f, b = 0.42f;
            VfxMotionVector.Apply(ref r, ref g, ref b, 1f, dx, dy, mode, scale);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            int px = sx - (int)(vx * 6f);
            int py = sy + (int)(vy * 6f);
            FillCircle(rgba, w, h, px, py, 4, 70, 74, 82);
            FillCircle(rgba, w, h, sx, sy, 11,
                Math.Clamp((int)(r * 255f), 16, 255),
                Math.Clamp((int)(g * 255f), 16, 255),
                Math.Clamp((int)(b * 255f), 16, 255));
        }
    }

    /// <summary>CPU stand-in: quads stretch along velocity instead of facing the camera.</summary>
    public static void FillOrientPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float size = settings.StartSize > 0f ? settings.StartSize : 0.2f;
        const int dots = 8;
        for (int i = 0; i < dots; i++)
        {
            float ang = i * 0.78539816f;
            float vx = MathF.Cos(ang) * 2f;
            float vy = MathF.Sin(ang) * 2f;
            float cx = -1.2f + i * 0.34f;
            float cy = 0.1f * ((i % 3) - 1);
            VfxOrient.AlongVelocity(cx, cy, 0f, vx, vy, 0f, 0f, 0f, 8f, 0f, 1f, 0f, size,
                out float tx, out float ty, out _);
            VfxOrient.AlongVelocity(cx, cy, 0f, vx, vy, 0f, 0f, 0f, 8f, 0f, -1f, 0f, size,
                out float bx, out float by, out _);
            int x0 = 40 + (int)((tx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y0 = (int)(h * 0.55f) - (int)(ty * 50f);
            int x1 = 40 + (int)((bx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y1 = (int)(h * 0.55f) - (int)(by * 50f);
            int minX = Math.Min(x0, x1);
            int minY = Math.Min(y0, y1);
            Fill(rgba, w, minX, minY, Math.Max(3, Math.Abs(x1 - x0) + 2), Math.Max(3, Math.Abs(y1 - y0) + 2), 80, 190, 255);
            FillCircle(rgba, w, h, x0, y0, 4, 255, 210, 90);
            FillCircle(rgba, w, h, x1, y1, 3, 70, 170, 220);
        }
    }

    /// <summary>CPU stand-in: quads stretch toward a look-at target.</summary>
    public static void FillOrientLookAtPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float size = settings.StartSize > 0f ? settings.StartSize : 0.2f;
        float tx = settings.LookAtX;
        float ty = settings.LookAtY;
        float tz = settings.LookAtZ;
        FillCircle(rgba, w, h, w / 2, (int)(h * 0.42f), 6, 255, 170, 70);
        const int dots = 8;
        for (int i = 0; i < dots; i++)
        {
            float cx = -1.2f + i * 0.34f;
            float cy = 0.1f * ((i % 3) - 1);
            VfxOrientLookAt.Apply(cx, cy, 0f, tx, ty, tz, out float ax, out float ay, out float az);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, 1f, 0f, size,
                out float px, out float py, out _);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, -1f, 0f, size,
                out float bx, out float by, out _);
            int x0 = 40 + (int)((px + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y0 = (int)(h * 0.55f) - (int)(py * 50f);
            int x1 = 40 + (int)((bx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y1 = (int)(h * 0.55f) - (int)(by * 50f);
            int minX = Math.Min(x0, x1);
            int minY = Math.Min(y0, y1);
            Fill(rgba, w, minX, minY, Math.Max(3, Math.Abs(x1 - x0) + 2), Math.Max(3, Math.Abs(y1 - y0) + 2), 255, 140, 70);
            FillCircle(rgba, w, h, x0, y0, 4, 255, 220, 90);
            FillCircle(rgba, w, h, x1, y1, 3, 220, 90, 70);
        }
    }

    /// <summary>CPU stand-in: quads stretch along a fixed axis instead of velocity.</summary>
    public static void FillFixedAxisPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float size = settings.StartSize > 0f ? settings.StartSize : 0.2f;
        float mode = settings.FixedAxisMode >= 1.5f ? settings.FixedAxisMode : 2f;
        float fx = settings.FixedAxisX;
        float fy = settings.FixedAxisY;
        float fz = settings.FixedAxisZ;
        const int dots = 8;
        for (int i = 0; i < dots; i++)
        {
            float ang = i * 0.78539816f;
            float vx = MathF.Cos(ang) * 2f;
            float vy = MathF.Sin(ang) * 2f;
            float cx = -1.2f + i * 0.34f;
            float cy = 0.1f * ((i % 3) - 1);
            VfxOrient.ResolveAlong(mode, vx, vy, 0f, fx, fy, fz, out float ax, out float ay, out float az);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, 1f, 0f, size,
                out float tx, out float ty, out _);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, -1f, 0f, size,
                out float bx, out float by, out _);
            int x0 = 40 + (int)((tx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y0 = (int)(h * 0.55f) - (int)(ty * 50f);
            int x1 = 40 + (int)((bx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y1 = (int)(h * 0.55f) - (int)(by * 50f);
            int minX = Math.Min(x0, x1);
            int minY = Math.Min(y0, y1);
            Fill(rgba, w, minX, minY, Math.Max(3, Math.Abs(x1 - x0) + 2), Math.Max(3, Math.Abs(y1 - y0) + 2), 255, 140, 70);
            FillCircle(rgba, w, h, x0, y0, 4, 255, 220, 90);
            FillCircle(rgba, w, h, x1, y1, 3, 220, 90, 70);
        }
    }

    /// <summary>CPU stand-in: Init offsets inside a sphere / box / cone / circle.</summary>
    public static void FillPositionShapePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float mode = settings.PositionShapeMode > 0f ? settings.PositionShapeMode : 1f;
        float sx = settings.PositionShapeSx > 0f ? settings.PositionShapeSx : 0.7f;
        float sy = settings.PositionShapeSy > 0f ? settings.PositionShapeSy : 0.7f;
        float sz = settings.PositionShapeSz > 0f ? settings.PositionShapeSz : 0.7f;
        const int dots = 36;
        for (uint i = 0; i < dots; i++)
        {
            VfxPositionShape.Offset(i, mode, sx, sy, sz, out float px, out float py, out float pz);
            int cx = w / 2 + (int)(px * 90f);
            int cy = (int)(h * 0.58f) - (int)(py * 70f) + (int)(pz * 12f);
            byte r = (byte)Math.Clamp(80 + (int)((pz + sx) * 80f), 40, 255);
            byte g = (byte)Math.Clamp(140 + (int)((py + sy) * 50f), 40, 255);
            byte b = (byte)Math.Clamp(200 - (int)((px + sx) * 40f), 40, 255);
            FillCircle(rgba, w, h, cx, cy, 4, r, g, b);
        }
    }

    /// <summary>CPU stand-in: Sequential places particles evenly on a circle or line.</summary>
    public static void FillSequentialPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float mode = settings.SequentialMode > 0f ? settings.SequentialMode : 1f;
        float sx = settings.SequentialSx > 0f ? settings.SequentialSx : 0.7f;
        float sy = settings.SequentialSy > 0f ? settings.SequentialSy : 0.7f;
        float sz = settings.SequentialSz > 0f ? settings.SequentialSz : 12f;
        uint dots = (uint)Math.Max(1, (int)sz);
        for (uint i = 0; i < dots; i++)
        {
            VfxSequential.Offset(i, mode, sx, sy, sz, out float px, out float py, out float pz);
            int cx = w / 2 + (int)(px * 90f);
            int cy = (int)(h * 0.58f) - (int)(py * 70f) + (int)(pz * 12f);
            byte r = (byte)Math.Clamp(70 + (int)(i * 12f), 40, 255);
            byte g = (byte)Math.Clamp(200 - (int)(i * 8f), 40, 255);
            byte b = (byte)Math.Clamp(220 - (int)((px + sx) * 40f), 80, 255);
            FillCircle(rgba, w, h, cx, cy, 5, r, g, b);
        }
    }

    /// <summary>CPU stand-in: inherit emitter velocity kicks new particles along the source path.</summary>
    public static void FillInheritVelocityPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float amount = settings.InheritVelocityAmount > 0f ? settings.InheritVelocityAmount : 1f;
        float lastX = 0f, lastY = 0f, lastZ = 0f;
        bool has = false;
        const float dt = 1f / 30f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float x = i * 0.22f;
            float y = 0.15f * MathF.Sin(i * 0.7f);
            VfxInheritVelocity.Sample(has, lastX, lastY, lastZ, x, y, 0f, dt, out float ex, out float ey, out float ez);
            lastX = x;
            lastY = y;
            lastZ = 0f;
            has = true;
            float vx = 0.4f, vy = 1.2f, vz = 0f;
            VfxInheritVelocity.Add(ref vx, ref vy, ref vz, ex, ey, ez, amount);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.62f) - (int)(vy * 18f);
            int kick = (int)(vx * 6f);
            FillCircle(rgba, w, h, sx + kick, sy, 7, 80, 220, 255);
            Fill(rgba, w, sx, (int)(h * 0.72f), 3, 3, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: higher linear drag shortens travel from the same initial velocity.</summary>
    public static void FillDragPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float dt = 1f / 30f;
        const int rows = 8;
        for (int i = 0; i < rows; i++)
        {
            float amount = settings.DragAmount > 0f ? settings.DragAmount * (i + 1) : (i * 1.4f);
            float px = -1.7f;
            float py = 0.85f - i * 0.22f;
            float vx = 5.2f;
            float vy = 0f;
            float vz = 0f;
            int y = (int)(h * 0.22f) + i * 18;
            Fill(rgba, w, 24, y + 5, Math.Max(8, w - 48), 1, 36, 40, 48);
            for (int step = 0; step < 36; step++)
            {
                VfxDrag.Apply(ref vx, ref vy, ref vz, amount, dt);
                px += vx * dt;
                py += vy * dt;
                if ((step & 3) == 0)
                {
                    int tx = 28 + (int)((px + 1.7f) * 0.22f * Math.Max(8, w - 56));
                    int ty = y + 5;
                    FillCircle(rgba, w, h, tx, ty, 2, 50, 90, 120);
                }
            }

            int cx = 28 + (int)((px + 1.7f) * 0.22f * Math.Max(8, w - 56));
            byte r = (byte)Math.Clamp(70 + i * 22, 40, 255);
            byte g = (byte)Math.Clamp(200 - i * 16, 40, 255);
            byte b = (byte)Math.Clamp(255 - i * 8, 80, 255);
            FillCircle(rgba, w, h, cx, y + 5, 5, r, g, b);
        }
    }

    /// <summary>CPU stand-in: stronger constant force travels farther from rest.</summary>
    public static void FillForcePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float dt = 1f / 30f;
        float baseX = settings.ForceX != 0f ? settings.ForceX : 1.6f;
        float baseY = settings.ForceY;
        const int rows = 8;
        for (int i = 0; i < rows; i++)
        {
            float fx = baseX * (i + 1);
            float fy = baseY;
            float px = -1.7f;
            float py = 0f;
            float vx = 0f;
            float vy = 0f;
            float vz = 0f;
            int y = (int)(h * 0.22f) + i * 18;
            Fill(rgba, w, 24, y + 5, Math.Max(8, w - 48), 1, 36, 40, 48);
            for (int step = 0; step < 36; step++)
            {
                VfxForce.Apply(ref vx, ref vy, ref vz, fx, fy, 0f, dt);
                px += vx * dt;
                py += vy * dt;
                if ((step & 3) == 0)
                {
                    int tx = 28 + (int)((px + 1.7f) * 0.18f * Math.Max(8, w - 56));
                    int ty = y + 5 - (int)(py * 8f);
                    FillCircle(rgba, w, h, tx, ty, 2, 70, 80, 50);
                }
            }

            int cx = 28 + (int)((px + 1.7f) * 0.18f * Math.Max(8, w - 56));
            byte r = (byte)Math.Clamp(255 - i * 8, 80, 255);
            byte g = (byte)Math.Clamp(180 - i * 10, 40, 255);
            byte b = (byte)Math.Clamp(70 + i * 18, 40, 255);
            FillCircle(rgba, w, h, cx, y + 5, 5, r, g, b);
        }
    }

    /// <summary>CPU stand-in: angular velocity spins local XY of each quad.</summary>
    public static void FillSpinPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float rate = settings.AngularVelocityRate != 0f ? settings.AngularVelocityRate : 1.2f;
        const int quads = 8;
        for (int i = 0; i < quads; i++)
        {
            float angle = 0f;
            VfxSpin.Integrate(ref angle, rate, i * 0.18f);
            int cx = 36 + i * 36;
            int cy = (int)(h * 0.55f);
            DrawSpunQuad(rgba, w, h, cx, cy, 14, angle, 80 + i * 18, 200 - i * 12, 255);
        }
    }

    private static void DrawSpunQuad(byte[] rgba, int w, int h, int cx, int cy, int size, float angle, int r, int g, int b)
    {
        float[] lx = { -1f, 1f, 1f, -1f };
        float[] ly = { -1f, -1f, 1f, 1f };
        int prevX = 0, prevY = 0;
        for (int i = 0; i < 5; i++)
        {
            int k = i & 3;
            VfxSpin.Local(lx[k], ly[k], angle, out float sx, out float sy);
            int x = cx + (int)(sx * size);
            int y = cy + (int)(sy * size);
            FillCircle(rgba, w, h, x, y, 3, r, g, b);
            if (i > 0)
            {
                int steps = Math.Max(Math.Abs(x - prevX), Math.Abs(y - prevY));
                for (int s = 0; s <= steps; s++)
                {
                    int px = prevX + (x - prevX) * s / Math.Max(1, steps);
                    int py = prevY + (y - prevY) * s / Math.Max(1, steps);
                    FillCircle(rgba, w, h, px, py, 1, r, g, b);
                }
            }

            prevX = x;
            prevY = y;
        }
    }

    /// <summary>CPU stand-in: custom HLSL kicks particles upward when the block has code.</summary>
    public static void FillCustomHlslPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float kick = settings.CustomHlsl > 0f ? 2.4f : 0f;
        float dt = 1f / 30f;
        const int rows = 8;
        for (int i = 0; i < rows; i++)
        {
            float px = -1.5f + i * 0.38f;
            float py = -0.35f;
            float vx = 0f;
            float vy = 0f;
            for (int step = 0; step < 20; step++)
            {
                vy += kick * dt;
                px += vx * dt;
                py += vy * dt;
            }

            int cx = 40 + (int)((px + 1.6f) * 0.28f * Math.Max(8, w - 64));
            int cy = (int)(h * 0.72f) - (int)((py + 0.4f) * 70f);
            byte r = (byte)Math.Clamp(80 + i * 20, 40, 255);
            byte g = (byte)Math.Clamp(60 + (int)(kick * 40f), 40, 255);
            byte b = (byte)Math.Clamp(220 - i * 8, 80, 255);
            Fill(rgba, w, cx - 1, cy, 2, Math.Max(4, (int)(h * 0.72f) - cy), 48, 52, 70);
            FillCircle(rgba, w, h, cx, cy, 6, r, g, b);
        }
    }

    /// <summary>CPU stand-in: bounce then lose tangent speed to friction.</summary>
    public static void FillCollisionFrictionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float amount = settings.CollisionFrictionAmount > 0f ? settings.CollisionFrictionAmount : 0.7f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float friction = t * amount;
            float vx = 2.4f;
            float vy = -1.2f;
            float vz = 0f;
            VfxCollisionFriction.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, friction);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            int rad = Math.Max(3, (int)(MathF.Abs(vx) * 6f));
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 8f), sy - 22, 4, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: bounce then lose outgoing normal to elasticity.</summary>
    public static void FillCollisionElasticityPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float amount = settings.CollisionElasticityAmount > 0f ? settings.CollisionElasticityAmount : 0.7f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float elastic = t * amount;
            float vx = 1.2f;
            float vy = 2.4f;
            float vz = 0f;
            VfxCollisionElasticity.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, elastic);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            int rad = Math.Max(3, (int)(MathF.Abs(vy) * 6f));
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 8f), sy - (int)(vy * 10f), 4, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: remaining life shrinks after a collision.</summary>
    public static void FillCollisionLifetimeLossPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float amount = settings.CollisionLifetimeLossAmount > 0f ? settings.CollisionLifetimeLossAmount : 0.7f;
        const int dots = 10;
        const float age = 0.4f;
        const float lifetime = 2f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float remain = VfxCollisionLifetimeLoss.Apply(age, lifetime, t * amount) - age;
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            int rad = Math.Max(2, (int)(remain * 10f));
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx, sy - 22 - (int)(remain * 12f), 4, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: incoming speed 5, clamp to a falling max.</summary>
    public static void FillLimitVelocityPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float maxSpeed = settings.LimitVelocitySpeed > 0f ? settings.LimitVelocitySpeed : 2.5f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float vx = 3f;
            float vy = 4f;
            float vz = 0f;
            VfxLimitVelocity.Apply(ref vx, ref vy, ref vz, t * maxSpeed);
            float spd = MathF.Sqrt(vx * vx + vy * vy);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            int rad = Math.Max(2, (int)(spd * 3f));
            FillCircle(rgba, w, h, sx, sy, rad, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 6f), sy - (int)(vy * 6f), 4, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: dots inside the kill sphere vanish.</summary>
    public static void FillKillSpherePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float radius = settings.KillSphereRadius > 0f ? settings.KillSphereRadius : 0.8f;
        int cx = w / 2;
        int cy = groundSy - 28;
        int ring = Math.Max(8, (int)(radius * 40f));
        FillCircle(rgba, w, h, cx, cy, ring, 40, 70, 90);
        const int dots = 12;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float py = 0f;
            bool kill = VfxKillSphere.Apply(px, py, 0f, settings.KillSphereX, settings.KillSphereY, settings.KillSphereZ, radius);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            if (kill)
                FillCircle(rgba, w, h, sx, sy, 3, 40, 44, 52);
            else
                FillCircle(rgba, w, h, sx, sy, 5, 80, 220, 255);
        }
    }

    /// <summary>CPU stand-in: dots inside the kill box vanish.</summary>
    public static void FillKillAABoxPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float sx = settings.KillAABoxSx > 0f ? settings.KillAABoxSx : 0.7f;
        float sy = settings.KillAABoxSy > 0f ? settings.KillAABoxSy : 0.4f;
        float sz = settings.KillAABoxSz > 0f ? settings.KillAABoxSz : 1f;
        int boxW = Math.Max(8, (int)(sx * 80f));
        int boxH = Math.Max(6, (int)(sy * 50f));
        Fill(rgba, w, w / 2 - boxW / 2, groundSy - 18 - boxH / 2, boxW, boxH, 40, 70, 90);
        const int dots = 12;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            bool kill = VfxKillAABox.Apply(px, 0f, 0f, settings.KillAABoxX, settings.KillAABoxY, settings.KillAABoxZ, sx, sy, sz);
            int dx = 24 + i * Math.Max(8, (w - 48) / dots);
            int dy = groundSy - 18;
            if (kill)
                FillCircle(rgba, w, h, dx, dy, 3, 40, 44, 52);
            else
                FillCircle(rgba, w, h, dx, dy, 5, 80, 220, 255);
        }
    }

    /// <summary>CPU stand-in: dots behind the kill plane vanish.</summary>
    public static void FillKillPlanePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float nx = settings.KillPlaneNx;
        float ny = settings.KillPlaneNy;
        float nz = settings.KillPlaneNz;
        if (nx * nx + ny * ny + nz * nz <= 1e-10f)
        {
            nx = 0f;
            ny = 1f;
            nz = 0f;
        }
        const int dots = 12;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float py = -0.6f + t * 1.2f;
            bool kill = VfxKillPlane.Apply(px, py, 0f, nx, ny, nz, settings.KillPlaneDistance);
            int dx = 24 + i * Math.Max(8, (w - 48) / dots);
            int dy = groundSy - 8 - (int)(py * 40f);
            if (kill)
                FillCircle(rgba, w, h, dx, dy, 3, 40, 44, 52);
            else
                FillCircle(rgba, w, h, dx, dy, 5, 80, 220, 255);
        }
    }

    /// <summary>CPU stand-in: dots accelerate toward a cylinder surface.</summary>
    public static void FillConformToCylinderPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float strength = settings.ConformCylinderStrength > 0f ? settings.ConformCylinderStrength : 2f;
        float radius = settings.ConformCylinderRadius != 0f ? settings.ConformCylinderRadius : 1f;
        float ay = settings.ConformCylinderAy != 0f || settings.ConformCylinderAx != 0f || settings.ConformCylinderAz != 0f
            ? settings.ConformCylinderAy
            : 1f;
        Fill(rgba, w, w / 2 - 1, 28, 2, groundSy - 32, 255, 170, 70);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxConformToCylinder.Apply(
                ref vx, ref vy, ref vz,
                px, 0.8f, 0f,
                settings.ConformCylinderX, settings.ConformCylinderY, settings.ConformCylinderZ,
                settings.ConformCylinderAx, ay, settings.ConformCylinderAz,
                radius, strength, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            FillCircle(rgba, w, h, sx, sy, 5, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 18f), sy - (int)(vy * 18f), 3, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: dots spin around a Y axis and pull inward.</summary>
    public static void FillVortexPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float rotation = settings.VortexRotation != 0f ? settings.VortexRotation : 2f;
        float attraction = settings.VortexAttraction != 0f ? settings.VortexAttraction : 1f;
        float ay = settings.VortexAy != 0f || settings.VortexAx != 0f || settings.VortexAz != 0f
            ? settings.VortexAy
            : 1f;
        FillCircle(rgba, w, h, w / 2, groundSy - 18, 4, 255, 170, 70);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxVortex.Apply(
                ref vx, ref vy, ref vz,
                px, 0f, 0f,
                settings.VortexX, settings.VortexY, settings.VortexZ,
                settings.VortexAx, ay, settings.VortexAz,
                rotation, attraction, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            FillCircle(rgba, w, h, sx, sy, 5, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 18f), sy - (int)(vz * 18f), 3, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: dots accelerate toward a sphere surface.</summary>
    public static void FillConformToSpherePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float strength = settings.ConformSphereStrength > 0f ? settings.ConformSphereStrength : 2f;
        float radius = settings.ConformSphereRadius != 0f ? settings.ConformSphereRadius : 1f;
        FillCircle(rgba, w, h, w / 2, groundSy - 18, 18, 40, 70, 90);
        FillCircle(rgba, w, h, w / 2, groundSy - 18, 4, 255, 170, 70);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxConformToSphere.Apply(
                ref vx, ref vy, ref vz,
                px, 0f, 0f,
                settings.ConformSphereX, settings.ConformSphereY, settings.ConformSphereZ,
                radius, strength, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            FillCircle(rgba, w, h, sx, sy, 5, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 18f), sy - (int)(vy * 18f), 3, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: dots accelerate toward the attractor.</summary>
    public static void FillAttractPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        float strength = settings.AttractStrength > 0f ? settings.AttractStrength : 2f;
        FillCircle(rgba, w, h, w / 2, groundSy - 18, 6, 255, 170, 70);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxAttract.Apply(ref vx, ref vy, ref vz, px, 0f, 0f, settings.AttractX, settings.AttractY, settings.AttractZ, strength, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = groundSy - 18;
            FillCircle(rgba, w, h, sx, sy, 5, 80, 220, 255);
            FillCircle(rgba, w, h, sx + (int)(vx * 18f), sy - (int)(vy * 18f), 3, 255, 170, 70);
        }
    }

    /// <summary>CPU stand-in: particles integrate under gravity and collide with the blackboard plane.</summary>
    public static void FillCollisionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int groundSy = (int)(h * 0.68f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        int dots = 24;
        float dt = 1f / 30f;
        float mode = settings.CollisionMode;
        float bounce = settings.CollisionBounce;
        Float4 plane = settings.CollisionPlane;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 40f));
        for (int i = 0; i < dots; i++)
        {
            float px = -1.6f + i * 0.14f;
            float py = 1.15f + (i % 5) * 0.12f;
            float pz = 0f;
            float vx = (i % 3 - 1) * 0.35f;
            float vy = 0.15f;
            float vz = 0f;
            uint alive = 1u;
            for (int step = 0; step < 48; step++)
            {
                vy += settings.Gravity.Y * dt;
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
                VfxCollision.CollidePlane(
                    ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                    plane.X, plane.Y, plane.Z, plane.W, mode, bounce);
                if (alive == 0u)
                    break;
            }
            if (alive == 0u)
                continue;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.28f));
            FillCircle(rgba, w, h, sx, sy, radius, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: particles integrate under gravity and collide with a sphere/box SDF.</summary>
    public static void FillSdfPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int groundSy = (int)(h * 0.68f);
        int cx = 12 + (int)((settings.SdfCenter.X + 2f) * 0.25f * Math.Max(8, w - 24));
        int cy = groundSy - (int)(settings.SdfCenter.Y * (h * 0.28f));
        int ring = Math.Max(8, (int)(settings.SdfSize.X * (h * 0.28f)));
        StrokeRing(rgba, w, h, cx, cy, ring, 70, 170, 210);
        int dots = 24;
        float dt = 1f / 30f;
        float mode = settings.SdfCollisionMode;
        float shape = settings.SdfShape;
        float bounce = settings.CollisionBounce;
        Float3 center = settings.SdfCenter;
        Float3 size = settings.SdfSize;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 40f));
        for (int i = 0; i < dots; i++)
        {
            float px = -1.6f + i * 0.14f;
            float py = 1.15f + (i % 5) * 0.12f;
            float pz = 0f;
            float vx = (i % 3 - 1) * 0.35f;
            float vy = 0.15f;
            float vz = 0f;
            uint alive = 1u;
            for (int step = 0; step < 48; step++)
            {
                vy += settings.Gravity.Y * dt;
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
                VfxCollision.CollideSdf(
                    ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                    center.X, center.Y, center.Z, size.X, size.Y, size.Z,
                    shape, mode, bounce, settings.StartSize * 0.5f);
                if (alive == 0u)
                    break;
            }
            if (alive == 0u)
                continue;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.28f));
            FillCircle(rgba, w, h, sx, sy, radius, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: particles bounce on a Y-capped cylinder and cone.</summary>
    public static void FillCylinderConePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.72f);
        const float cylX = -0.7f, coneX = 0.75f, cy = 0.45f, r = 0.28f, hh = 0.4f;
        int cylSx = 12 + (int)((cylX - r + 2f) * 0.25f * Math.Max(8, w - 24));
        int coneSx = 12 + (int)((coneX - r + 2f) * 0.25f * Math.Max(8, w - 24));
        int boxY = groundSy - (int)((cy + hh) * (h * 0.32f));
        int boxW = Math.Max(10, (int)(r * 2f * 0.25f * Math.Max(8, w - 24)));
        int boxH = Math.Max(12, (int)(hh * 2f * (h * 0.32f)));
        Stroke(rgba, w, cylSx, boxY, boxW, boxH, 80, 210, 230);
        int apexX = 12 + (int)((coneX + 2f) * 0.25f * Math.Max(8, w - 24));
        int apexY = boxY;
        int baseL = 12 + (int)((coneX - r + 2f) * 0.25f * Math.Max(8, w - 24));
        int baseR = 12 + (int)((coneX + r + 2f) * 0.25f * Math.Max(8, w - 24));
        int baseY = boxY + boxH;
        Stroke(rgba, w, baseL, baseY - 2, Math.Max(4, baseR - baseL), 2, 230, 170, 80);
        Stroke(rgba, w, Math.Min(apexX, baseL), apexY, 2, boxH, 230, 170, 80);
        Stroke(rgba, w, Math.Max(apexX, baseR) - 2, apexY, 2, boxH, 230, 170, 80);
        float bounce = settings.CollisionBounce > 0f ? settings.CollisionBounce : 0.5f;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 36f));
        float dt = 1f / 30f;
        for (int i = 0; i < 18; i++)
        {
            bool cone = i >= 9;
            float ox = cone ? coneX : cylX;
            float shape = cone ? VfxCollision.ShapeCone : VfxCollision.ShapeCylinder;
            float topR = cone ? 0.04f : r;
            float px = ox - 0.18f + (i % 9) * 0.045f;
            float py = 1.15f + (i % 3) * 0.06f;
            float pz = 0f;
            float vx = 0f, vy = 0.1f, vz = 0f;
            uint alive = 1u;
            for (int step = 0; step < 40; step++)
            {
                vy += settings.Gravity.Y * dt;
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
                VfxCollision.CollideSdf(
                    ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                    ox, cy, 0f, r, hh, topR, shape, VfxCollision.ModeBounce, bounce,
                    settings.StartSize * 0.5f);
                if (alive == 0u)
                    break;
            }
            if (alive == 0u)
                continue;
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            FillCircle(rgba, w, h, x, y, radius, cone ? 230 : 70, cone ? 170 : 210, cone ? 80 : 230);
            FillCircle(rgba, w, h, x, y, Math.Max(1, radius - 1), br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: particles collide with a baked Texture3D sphere SDF.</summary>
    public static void FillVolumeSdfPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int groundSy = (int)(h * 0.68f);
        int cx = 12 + (int)((settings.SdfCenter.X + 2f) * 0.25f * Math.Max(8, w - 24));
        int cy = groundSy - (int)(settings.SdfCenter.Y * (h * 0.28f));
        int ring = Math.Max(8, (int)(settings.SdfSize.X * (h * 0.28f)));
        StrokeRing(rgba, w, h, cx, cy, ring, 180, 120, 220);
        StrokeRing(rgba, w, h, cx, cy, Math.Max(4, ring - 4), 70, 170, 210);
        const int res = 12;
        float[] voxels = new float[res * res * res];
        Float3 center = settings.SdfCenter;
        Float3 size = settings.SdfSize.X < 0.15f
            ? new Float3(0.6f, 0.6f, 0.6f)
            : settings.SdfSize;
        VfxCollision.BakeSphereVolume(
            voxels, res,
            center.X, center.Y, center.Z, Math.Max(0.2f, size.X * 0.65f),
            center.X, center.Y, center.Z, size.X, size.Y, size.Z);
        int dots = 24;
        float dt = 1f / 30f;
        float bounce = settings.CollisionBounce;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 40f));
        for (int i = 0; i < dots; i++)
        {
            float px = -1.6f + i * 0.14f;
            float py = 1.15f + (i % 5) * 0.12f;
            float pz = 0f;
            float vx = (i % 3 - 1) * 0.35f;
            float vy = 0.15f;
            float vz = 0f;
            uint alive = 1u;
            for (int step = 0; step < 48; step++)
            {
                vy += settings.Gravity.Y * dt;
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
                VfxCollision.CollideVolumeSdf(
                    ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                    center.X, center.Y, center.Z, size.X, size.Y, size.Z,
                    VfxCollision.ModeBounce, bounce, voxels, res, res, res, settings.StartSize * 0.5f);
                if (alive == 0u)
                    break;
            }
            if (alive == 0u)
                continue;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.28f));
            FillCircle(rgba, w, h, sx, sy, radius, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: particles advect under curl-noise turbulence.</summary>
    public static void FillTurbulencePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int dots = 20;
        float dt = 1f / 30f;
        float intensity = settings.TurbulenceIntensity;
        float frequency = settings.TurbulenceFrequency;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 40f));
        for (int i = 0; i < dots; i++)
        {
            float px = -1.4f + (i % 5) * 0.7f;
            float py = 0.35f + (i / 5) * 0.35f;
            float pz = 0.1f * (i % 3);
            float vx = 0.15f;
            float vy = 0.05f;
            float vz = 0f;
            for (int step = 0; step < 36; step++)
            {
                VfxTurbulence.Apply(ref vx, ref vy, ref vz, px, py, pz, intensity, frequency, dt);
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
            }
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = (int)(h * 0.72f) - (int)(py * (h * 0.28f));
            FillCircle(rgba, w, h, sx, sy, radius, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: billboards fade against a fake ground-plane depth.</summary>
    public static void FillSoftParticlePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int groundSy = (int)(h * 0.72f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 10, 70, 170, 210);
        int dots = 20;
        float fadeDistance = settings.SoftParticleFade;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(2, (int)(settings.StartSize * 48f));
        const float sceneEye = 2f;
        for (int i = 0; i < dots; i++)
        {
            float px = -1.6f + (i % 10) * 0.34f;
            float py = 0.08f + (i / 10) * 0.55f + (i % 3) * 0.12f;
            float particleEye = sceneEye - py;
            float fade = VfxSoftParticles.Fade(particleEye, sceneEye, fadeDistance);
            if (fade <= 0.02f)
                continue;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.42f));
            FillCircle(rgba, w, h, sx, sy, radius,
                Math.Clamp((int)(br * fade), 20, 255),
                Math.Clamp((int)(bg * fade), 20, 255),
                Math.Clamp((int)(bb * fade), 20, 255));
        }
    }

    /// <summary>CPU stand-in: OnDie cyan sparks and OnCollide orange sparks at the trigger site.</summary>
    public static void FillGpuEventPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int groundSy = (int)(h * 0.72f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 3, 70, 170, 210);
        int dots = 18;
        float dt = 1f / 30f;
        float life = Math.Max(0.2f, settings.Lifetime * 0.35f);
        float kind = settings.EventKind;
        float count = settings.EventCount;
        Float4 plane = settings.CollisionPlane;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 36f));
        for (int i = 0; i < dots; i++)
        {
            float px = -1.7f + i * 0.19f;
            float py = 1.35f + (i % 4) * 0.08f;
            float pz = 0f;
            float vx = (i % 3 - 1) * 0.25f;
            float vy = 0.2f;
            float vz = 0f;
            float age = 0f;
            uint alive = 1u;
            for (int step = 0; step < 40; step++)
            {
                age += dt;
                bool died = false;
                bool collided = false;
                if (age >= life)
                {
                    died = true;
                    alive = 0u;
                }
                else
                {
                    vy += settings.Gravity.Y * dt;
                    px += vx * dt;
                    py += vy * dt;
                    pz += vz * dt;
                    uint before = alive;
                    VfxCollision.CollidePlane(
                        ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                        plane.X, plane.Y, plane.Z, plane.W, settings.CollisionMode, settings.CollisionBounce);
                    collided = py <= 0.02f || alive == 0u;
                    died = before != 0u && alive == 0u;
                }

                int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
                int sy = groundSy - (int)(py * (h * 0.32f));
                uint dieEvents = VfxGpuEvent.EmitCount(kind, count, died, collided: false);
                uint collideEvents = VfxGpuEvent.EmitCount(VfxGpuEvent.KindOnCollide, count, died: false, collided);
                if (dieEvents > 0)
                {
                    FillCircle(rgba, w, h, sx, sy, radius + 2, 80, 230, 255);
                    uint children = VfxGpuEvent.ChildSpawnCount(kind, count, died, collided: false);
                    for (uint k = 0; k < children; k++)
                    {
                        int ox = sx + (int)k * 5 - 4;
                        int oy = sy - 6 - (int)k * 3;
                        FillCircle(rgba, w, h, ox, oy, Math.Max(1, radius - 1), 180, 245, 255);
                    }
                    break;
                }
                if (collideEvents > 0)
                {
                    StrokeRing(rgba, w, h, sx, sy, radius + 3, 255, 160, 70);
                    FillCircle(rgba, w, h, sx, sy, radius, 255, 140, 50);
                    break;
                }
                if (alive == 0u)
                    break;
            }
            if (alive == 0u)
                continue;
            int lx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int ly = groundSy - (int)(py * (h * 0.32f));
            FillCircle(rgba, w, h, lx, ly, radius, br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: GPU events enqueue spawn on another VisualEffect.</summary>
    public static void FillGpuEventTargetPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int mid = w / 2;
        Fill(rgba, w, mid - 1, 28, 2, Math.Max(8, h - 40), 40, 46, 56);
        float count = settings.EventCount > 0.5f ? settings.EventCount : 3f;
        uint pending = 0;
        VfxGpuEvent.QueueTarget(ref pending, (uint)count, 64);
        uint n = VfxGpuEvent.Consume(ref pending);
        int srcX = w / 4;
        int dstX = (w * 3) / 4;
        int baseY = (int)(h * 0.62f);
        FillCircle(rgba, w, h, srcX, baseY, 8, 80, 230, 255);
        for (uint k = 0; k < n; k++)
        {
            int x = dstX - 10 + (int)k * 8;
            int y = baseY - 6 - (int)k * 7;
            FillCircle(rgba, w, h, x, y, 5, 255, 170, 70);
            FillCircle(rgba, w, h, x, y, 3, 255, 220, 120);
        }
        Fill(rgba, w, srcX + 10, baseY - 1, Math.Max(8, dstX - srcX - 24), 2, 90, 140, 180);
    }

    /// <summary>CPU stand-in: OnPlay burst at t=0, then Always pulses along the timeline.</summary>
    public static void FillGpuEventAlwaysPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int axisY = (int)(h * 0.62f);
        Fill(rgba, w, 16, axisY, Math.Max(8, w - 32), 2, 40, 46, 56);
        float count = settings.EventCount > 0.5f ? settings.EventCount : 3f;
        uint onPlay = VfxGpuEvent.EmitOnPlay(VfxGpuEvent.KindOnPlay, count);
        for (uint k = 0; k < onPlay; k++)
        {
            int x = 28 + (int)k * 8;
            int y = axisY - 22 - (int)k * 7;
            FillCircle(rgba, w, h, x, y, 6, 255, 170, 70);
            FillCircle(rgba, w, h, x, y, 3, 255, 220, 120);
        }
        const int ticks = 8;
        for (int i = 0; i < ticks; i++)
        {
            int x = 72 + i * Math.Max(8, (w - 96) / ticks);
            Fill(rgba, w, x, axisY - 4, 2, 10, 70, 80, 96);
            uint n = VfxGpuEvent.EmitAlways(VfxGpuEvent.KindAlways, count);
            for (uint k = 0; k < n; k++)
            {
                int y = axisY - 18 - (int)k * 8;
                FillCircle(rgba, w, h, x, y, 4, 80, 230, 160);
            }
        }
    }

    /// <summary>CPU stand-in: bounce against a reconstructed y=0 depth surface.</summary>
    public static void FillDepthCollisionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.72f);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 3, 40, 200, 180);
        int dots = 16;
        float dt = 1f / 30f;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 36f));
        float mode = settings.DepthCollisionMode < 0.5f ? VfxDepthCollision.ModeBounce : settings.DepthCollisionMode;
        for (int i = 0; i < dots; i++)
        {
            float px = -1.6f + i * 0.2f;
            float py = 1.25f + (i % 3) * 0.1f;
            float pz = 0f;
            float vx = 0f;
            float vy = 0.15f;
            float vz = 0f;
            uint alive = 1u;
            bool bounced = false;
            for (int step = 0; step < 48; step++)
            {
                vy += settings.Gravity.Y * dt;
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
                float sceneRaw = 1f;
                if (VfxDepthCollision.TryProject(px, 0f, pz, out _, out _, out float groundEye))
                    sceneRaw = VfxDepthCollision.RawFromEye(groundEye);
                uint before = alive;
                VfxDepthCollision.Collide(
                    ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                    sceneRaw, mode, settings.CollisionBounce);
                if (before != 0u && alive == 0u)
                    break;
                if (vy > 0.05f)
                    bounced = true;
            }
            if (alive == 0u)
                continue;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.32f));
            if (bounced)
            {
                StrokeRing(rgba, w, h, sx, sy, radius + 3, 80, 255, 210);
                FillCircle(rgba, w, h, sx, sy, radius, 40, 230, 190);
            }
            else
            {
                FillCircle(rgba, w, h, sx, sy, radius, br, bg, bb);
            }
        }
    }

    /// <summary>CPU stand-in: particles pulled onto the SDF sphere isosurface.</summary>
    public static void FillConformPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.72f);
        float cx = settings.SdfCenter.X;
        float cy = settings.SdfCenter.Y;
        float cz = settings.SdfCenter.Z;
        float sx = settings.SdfSize.X;
        int ringX = 12 + (int)((cx + 2f) * 0.25f * Math.Max(8, w - 24));
        int ringY = groundSy - (int)(cy * (h * 0.32f));
        int ringR = Math.Max(8, (int)(sx * (h * 0.32f)));
        StrokeRing(rgba, w, h, ringX, ringY, ringR, 70, 190, 255);
        int dots = 16;
        float dt = 1f / 30f;
        float attraction = settings.ConformAttraction <= 0f ? 4f : settings.ConformAttraction;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 36f));
        for (int i = 0; i < dots; i++)
        {
            float px = -1.6f + i * 0.21f;
            float py = 1.35f + (i % 4) * 0.08f;
            float pz = 0f;
            for (int step = 0; step < 36; step++)
            {
                py += settings.Gravity.Y * dt * 0.15f;
                VfxConform.Apply(
                    ref px, ref py, ref pz,
                    cx, cy, cz, sx, settings.SdfSize.Y, settings.SdfSize.Z, settings.SdfShape,
                    attraction, dt);
            }
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            FillCircle(rgba, w, h, x, y, radius, 80, 210, 255);
            FillCircle(rgba, w, h, x, y, Math.Max(1, radius - 1), br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: Texture3D vector-field force blowing particles +X.</summary>
    public static void FillVectorFieldPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.72f);
        float cx = settings.SdfCenter.X;
        float cy = settings.SdfCenter.Y;
        float cz = settings.SdfCenter.Z;
        float sx = settings.SdfSize.X;
        int boxX = 12 + (int)((cx - sx + 2f) * 0.25f * Math.Max(8, w - 24));
        int boxY = groundSy - (int)((cy + sx) * (h * 0.32f));
        int boxW = Math.Max(16, (int)(sx * 2f * 0.25f * Math.Max(8, w - 24)));
        int boxH = Math.Max(16, (int)(sx * 2f * (h * 0.32f)));
        Stroke(rgba, w, boxX, boxY, boxW, boxH, 80, 220, 180);
        Span<float> field = stackalloc float[3];
        VfxVectorField.BakeConstant(field, 1, 1f, 0f, 0f);
        float intensity = settings.VectorFieldIntensity <= 0f ? 2.5f : settings.VectorFieldIntensity;
        int dots = 16;
        float dt = 1f / 30f;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 36f));
        for (int i = 0; i < dots; i++)
        {
            float px = -0.55f;
            float py = 0.25f + (i % 8) * 0.08f;
            float pz = 0f;
            float vx = 0f, vy = 0f, vz = 0f;
            for (int step = 0; step < 28; step++)
            {
                VfxVectorField.Apply(
                    ref vx, ref vy, ref vz,
                    px, py, pz, cx, cy, cz, sx, settings.SdfSize.Y, settings.SdfSize.Z,
                    intensity, dt, field, 1, 1, 1);
                px += vx * dt;
                py += vy * dt;
                pz += vz * dt;
            }
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            FillCircle(rgba, w, h, x, y, radius, 70, 230, 190);
            FillCircle(rgba, w, h, x, y, Math.Max(1, radius - 1), br, bg, bb);
        }
    }

    /// <summary>CPU stand-in: ballistic point-light proxies around the emitter.</summary>
    public static void FillParticleLightPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.72f);
        Fill(rgba, w, 0, groundSy, w, Math.Max(1, h - groundSy), 28, 30, 36);
        float intensity = settings.ParticleLightIntensity <= 0f ? 2f : settings.ParticleLightIntensity;
        float range = settings.ParticleLightRange <= 0f ? 1.5f : settings.ParticleLightRange;
        int count = VfxParticleLight.ResolveCount(settings.ParticleLightCount <= 0f ? 8f : settings.ParticleLightCount);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        for (uint slot = 0; slot < (uint)count; slot++)
        {
            VfxParticleLight.TryEvaluate(
                slot, count, 0f, 0.55f, 0f, settings.StartSpeed,
                settings.Gravity.X, settings.Gravity.Y, settings.Gravity.Z,
                settings.Lifetime, settings.StartColor.R, settings.StartColor.G, settings.StartColor.B,
                intensity, range,
                out float px, out float py, out _, out _, out _, out _, out _, out float lightRange);
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            int rad = Math.Max(3, (int)(lightRange * 6f));
            FillCircle(rgba, w, h, x, y, rad, 255, 220, 90);
            FillCircle(rgba, w, h, x, y, Math.Max(1, rad - 2), br, bg, bb);
        }
        FillCircle(rgba, w, h, 12 + (int)(0.5f * Math.Max(8, w - 24)), groundSy - (int)(0.55f * (h * 0.32f)), 3, 90, 210, 255);
    }

    /// <summary>CPU stand-in: heat-haze refraction of a checker through particle discs.</summary>
    public static void FillDistortionPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        for (int y = 22; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool on = ((x / 16) + (y / 16)) % 2 == 0;
                Fill(rgba, w, x, y, 1, 1, on ? (byte)70 : (byte)36, on ? (byte)78 : (byte)40, on ? (byte)92 : (byte)48);
            }
        }
        float intensity = settings.DistortionIntensity <= 0f ? 0.35f : settings.DistortionIntensity;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(8, (int)(settings.StartSize * 72f));
        for (int i = 0; i < 6; i++)
        {
            int cx = 40 + i * 46;
            int cy = 70 + (i % 2) * 36;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius)
                        continue;
                    float su = 0.5f + dx / (float)(radius * 2);
                    float sv = 0.5f + dy / (float)(radius * 2);
                    float u = (cx + dx) / (float)Math.Max(1, w - 1);
                    float v = (cy + dy) / (float)Math.Max(1, h - 1);
                    VfxDistortion.OffsetUv(u, v, intensity, su, sv, out float ou, out float ov);
                    int sx = (int)(ou * (w - 1));
                    int sy = (int)(ov * (h - 1));
                    if ((uint)sx >= (uint)w || (uint)sy >= (uint)h)
                        continue;
                    int o = (sy * w + sx) * 4;
                    float r = rgba[o] / 255f, g = rgba[o + 1] / 255f, b = rgba[o + 2] / 255f;
                    VfxDistortion.Apply(ref r, ref g, ref b, br / 255f, bg / 255f, bb / 255f, 0.85f, intensity);
                    int px = cx + dx;
                    int py = cy + dy;
                    if ((uint)px >= (uint)w || (uint)py >= (uint)h)
                        continue;
                    Fill(rgba, w, px, py, 1, 1,
                        (byte)Math.Clamp((int)(r * 255f), 0, 255),
                        (byte)Math.Clamp((int)(g * 255f), 0, 255),
                        (byte)Math.Clamp((int)(b * 255f), 0, 255));
                }
            }
        }
    }

    /// <summary>CPU stand-in: Init samples a Texture2D attribute map for position and color.</summary>
    public static void FillAttributeMapPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.62f);
        const int mapW = 4, mapH = 2;
        Span<float> map = stackalloc float[mapW * mapH * 4];
        for (int i = 0; i < mapW * mapH; i++)
        {
            float ang = i * (MathF.PI * 2f / (mapW * mapH));
            map[i * 4] = 0.5f + MathF.Cos(ang) * 0.5f;
            map[i * 4 + 1] = 0.5f + MathF.Sin(ang) * 0.5f;
            map[i * 4 + 2] = 0.5f;
            map[i * 4 + 3] = 1f;
        }
        float mode = settings.AttributeMapMode < 0.5f ? VfxAttributeMap.ModePositionColor : settings.AttributeMapMode;
        float scale = settings.AttributeMapScale <= 0f ? 1.4f : settings.AttributeMapScale;
        for (uint slot = 0; slot < mapW * mapH; slot++)
        {
            float px = 0f, py = 0.4f, pz = 0f, size = settings.StartSize;
            float cr = settings.StartColor.R, cg = settings.StartColor.G, cb = settings.StartColor.B, ca = 1f;
            VfxAttributeMap.Apply(
                ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
                0f, 0.4f, 0f, settings.StartSize, mode, scale, map, mapW, mapH, slot);
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.38f));
            int r = Math.Clamp((int)(cr * 255f), 40, 255);
            int g = Math.Clamp((int)(cg * 255f), 40, 255);
            int b = Math.Clamp((int)(cb * 255f), 40, 255);
            int rad = Math.Max(2, (int)(size * 40f));
            FillCircle(rgba, w, h, x, y, rad, 90, 210, 255);
            FillCircle(rgba, w, h, x, y, Math.Max(1, rad - 1), r, g, b);
        }
        for (int i = 0; i < mapW * mapH; i++)
        {
            int tx = 8 + (i % mapW) * 10;
            int ty = 28 + (i / mapW) * 10;
            int r = Math.Clamp((int)(map[i * 4] * 255f), 40, 255);
            int g = Math.Clamp((int)(map[i * 4 + 1] * 255f), 40, 255);
            int b = Math.Clamp((int)(map[i * 4 + 2] * 255f), 40, 255);
            Fill(rgba, w, tx, ty, 8, 8, (byte)r, (byte)g, (byte)b);
        }
    }

    /// <summary>CPU stand-in: particles emitted along an emitter travel path.</summary>
    public static void FillSpawnOverDistancePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.62f);
        float rate = settings.SpawnOverDistance <= 0f ? 8f : settings.SpawnOverDistance;
        float leftover = 0f, lx = 0f, ly = 0f, lz = 0f;
        bool hasPrev = false;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(1, (int)(settings.StartSize * 36f));
        int prevX = 0, prevY = 0;
        for (int i = 0; i < 28; i++)
        {
            float t = i / 27f;
            float px = -1.7f + t * 3.4f;
            float py = 0.55f + MathF.Sin(t * 6.2f) * 0.42f;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.38f));
            if (i > 0)
                Stroke(rgba, w, Math.Min(prevX, sx), Math.Min(prevY, sy), Math.Max(2, Math.Abs(sx - prevX)), Math.Max(2, Math.Abs(sy - prevY)), 50, 70, 90);
            prevX = sx;
            prevY = sy;
            uint n = VfxSpawnOverDistance.Apply(
                ref leftover, ref lx, ref ly, ref lz, ref hasPrev, px, py, 0f, rate, 64);
            for (uint k = 0; k < n; k++)
            {
                float along = n == 1 ? 0f : k / (float)(n - 1);
                int dx = sx + (int)((along - 0.5f) * 4f);
                int dy = sy + (int)((k % 2) * 2f - 1f);
                FillCircle(rgba, w, h, dx, dy, radius, 90, 210, 255);
                FillCircle(rgba, w, h, dx, dy, Math.Max(1, radius - 1), br, bg, bb);
            }
        }
    }

    /// <summary>CPU stand-in: GPU events queued along an emitter travel path.</summary>
    public static void FillTriggerEventOverDistancePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.62f);
        float rate = settings.TriggerEventOverDistance > 0f ? settings.TriggerEventOverDistance : 8f;
        float leftover = 0f, track = 0f, lx = 0f, ly = 0f, lz = 0f;
        bool hasPrev = false;
        int prevX = 0, prevY = 0;
        for (int i = 0; i < 28; i++)
        {
            float t = i / 27f;
            float px = -1.7f + t * 3.4f;
            float py = 0.55f + MathF.Sin(t * 6.2f) * 0.42f;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.38f));
            if (i > 0)
                Stroke(rgba, w, Math.Min(prevX, sx), Math.Min(prevY, sy), Math.Max(2, Math.Abs(sx - prevX)), Math.Max(2, Math.Abs(sy - prevY)), 50, 70, 90);
            prevX = sx;
            prevY = sy;
            uint n = VfxTriggerEventOverDistance.Apply(
                ref leftover, hasPrev, lx, ly, lz, px, py, 0f, rate, 64);
            VfxSpawnOverDistance.Apply(
                ref track, ref lx, ref ly, ref lz, ref hasPrev, px, py, 0f, 0f, 64);
            for (uint k = 0; k < n; k++)
            {
                int dy = sy - 12 - (int)k * 8;
                FillCircle(rgba, w, h, sx, dy, 5, 80, 220, 255);
                FillCircle(rgba, w, h, sx, dy, 3, 180, 240, 255);
            }
        }
    }

    /// <summary>CPU stand-in: one GPU-event pulse on the first sample.</summary>
    public static void FillTriggerEventOncePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int axisY = (int)(h * 0.62f);
        Fill(rgba, w, 16, axisY, Math.Max(8, w - 32), 2, 50, 58, 70);
        float count = settings.TriggerEventOnceCount > 0f ? settings.TriggerEventOnceCount : 3f;
        bool fired = false;
        const int steps = 16;
        for (int i = 0; i < steps; i++)
        {
            int x = 24 + i * Math.Max(8, (w - 48) / steps);
            Fill(rgba, w, x, axisY - 4, 2, 10, 70, 80, 96);
            uint n = VfxTriggerEventOnce.Apply(ref fired, count, 64);
            for (uint k = 0; k < n; k++)
            {
                int cy = axisY - 18 - (int)k * 10;
                FillCircle(rgba, w, h, x + 1, cy, 5, 255, 170, 70);
                FillCircle(rgba, w, h, x + 1, cy, 3, 255, 220, 120);
            }
        }
    }

    /// <summary>CPU stand-in: GPU events queued at a steady rate along a time axis.</summary>
    public static void FillTriggerEventRatePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int axisY = (int)(h * 0.62f);
        Fill(rgba, w, 16, axisY, Math.Max(8, w - 32), 2, 50, 58, 70);
        float rate = settings.TriggerEventRate > 0f ? settings.TriggerEventRate : 8f;
        float leftover = 0f;
        const int steps = 16;
        for (int i = 0; i < steps; i++)
        {
            int x = 24 + i * Math.Max(8, (w - 48) / steps);
            Fill(rgba, w, x, axisY - 4, 2, 10, 70, 80, 96);
            uint n = VfxTriggerEventRate.Apply(ref leftover, 0.125f, rate, 64);
            for (uint k = 0; k < n; k++)
            {
                int cy = axisY - 18 - (int)k * 10;
                FillCircle(rgba, w, h, x + 1, cy, 5, 80, 220, 255);
                FillCircle(rgba, w, h, x + 1, cy, 3, 180, 240, 255);
            }
        }
    }

    /// <summary>CPU stand-in: periodic bursts along a time axis.</summary>
    public static void FillPeriodicBurstPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int axisY = (int)(h * 0.62f);
        Fill(rgba, w, 16, axisY, Math.Max(8, w - 32), 2, 50, 58, 70);
        float count = settings.PeriodicBurstCount > 0f ? settings.PeriodicBurstCount : 4f;
        float period = settings.PeriodicBurstPeriod > 0f ? settings.PeriodicBurstPeriod : 0.25f;
        float leftover = 0f;
        const int steps = 16;
        for (int i = 0; i < steps; i++)
        {
            int x = 24 + i * Math.Max(8, (w - 48) / steps);
            Fill(rgba, w, x, axisY - 4, 2, 10, 70, 80, 96);
            uint n = VfxPeriodicBurst.Apply(ref leftover, 0.125f, count, period, 64);
            for (uint k = 0; k < n; k++)
            {
                int cy = axisY - 18 - (int)k * 10;
                FillCircle(rgba, w, h, x + 1, cy, 5, 255, 170, 70);
                FillCircle(rgba, w, h, x + 1, cy, 3, 255, 220, 120);
            }
        }
    }

    /// <summary>CPU stand-in: camera-facing ribbon segments between neighboring particles.</summary>
    public static void FillStripPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int points = 14;
        float width = Math.Max(0.08f, settings.StartSize * 2.2f);
        const float camX = 0f, camY = 0.4f, camZ = 4f;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        for (int i = 0; i < points - 1; i++)
        {
            float t0 = i / (float)(points - 1);
            float t1 = (i + 1) / (float)(points - 1);
            float p0x = -1.7f + t0 * 3.4f;
            float p0y = 0.35f + MathF.Sin(t0 * 6.2f) * 0.45f;
            float p1x = -1.7f + t1 * 3.4f;
            float p1y = 0.35f + MathF.Sin(t1 * 6.2f) * 0.45f;
            float fade = 1f - t0 * 0.75f;
            int cr = Math.Clamp((int)(br * fade), 30, 255);
            int cg = Math.Clamp((int)(bg * fade), 30, 255);
            int cb = Math.Clamp((int)(bb * fade), 30, 255);
            for (int s = 0; s < 4; s++)
            {
                float along = s / 3f;
                VfxStrip.Extrude(
                    p0x, p0y, 0f, p1x, p1y, 0f, -0.5f, along, width, camX, camY, camZ,
                    out float lx, out float ly, out _);
                VfxStrip.Extrude(
                    p0x, p0y, 0f, p1x, p1y, 0f, 0.5f, along, width, camX, camY, camZ,
                    out float rx, out float ry, out _);
                int sx0 = 12 + (int)((lx + 2f) * 0.25f * Math.Max(8, w - 24));
                int sy0 = (int)(h * 0.72f) - (int)(ly * (h * 0.38f));
                int sx1 = 12 + (int)((rx + 2f) * 0.25f * Math.Max(8, w - 24));
                int sy1 = (int)(h * 0.72f) - (int)(ry * (h * 0.38f));
                FillCircle(rgba, w, h, sx0, sy0, 2, cr, cg, cb);
                FillCircle(rgba, w, h, sx1, sy1, 2, cr, cg, cb);
                FillCircle(rgba, w, h, (sx0 + sx1) / 2, (sy0 + sy1) / 2, 1, 80, 220, 255);
            }
        }
    }

    /// <summary>CPU stand-in: overlapping quads drawn far-to-near after camera-distance sort.</summary>
    public static void FillSortPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        float[] px = [-0.6f, 0.15f, 0.7f, -0.1f, 0.4f, -0.35f];
        float[] py = [0.2f, 0.05f, 0.15f, 0.35f, -0.1f, 0.0f];
        float[] pz = [2.4f, 0.6f, 1.8f, 0.9f, 3.1f, 1.2f];
        uint[] live = [0, 1, 2, 3, 4, 5];
        VfxSort.SortLiveByCamera(live, live.Length, px, py, pz, 0f, 0f, 0f);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        for (int i = 0; i < live.Length; i++)
        {
            int slot = (int)live[i];
            int cx = 40 + (int)((px[slot] + 1f) * 0.42f * Math.Max(8, w - 80));
            int cy = 48 + (int)((0.6f - py[slot]) * 0.55f * Math.Max(8, h - 70));
            int rad = Math.Max(10, 28 - (int)(pz[slot] * 5f));
            int fade = (int)Math.Clamp(40f + pz[slot] * 50f, 40f, 220f);
            FillCircle(rgba, w, h, cx, cy, rad, br * fade / 255, bg * fade / 255, bb * fade / 255);
            FillCircle(rgba, w, h, cx, cy, Math.Max(3, rad / 3), 230, 230, 235);
        }
    }

    /// <summary>CPU stand-in: billboards fade in near the camera and out toward far.</summary>
    public static void FillCameraFadePreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        Fill(rgba, w, 8, 36, 14, 28, 80, 200, 230);
        Fill(rgba, w, 22, 46, 10, 8, 80, 200, 230);
        float near = settings.CameraFadeNear > 0f ? settings.CameraFadeNear : 0.5f;
        float far = settings.CameraFadeFar > 0f ? settings.CameraFadeFar : 2.4f;
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(6, (int)(settings.StartSize * 56f));
        const int dots = 12;
        for (int i = 0; i < dots; i++)
        {
            float d = 0.12f + i * 0.24f;
            float fade = VfxCameraFade.Fade(d, near, far);
            if (fade <= 0.02f)
                continue;
            int sx = 36 + (int)((d / 3.0f) * Math.Max(8, w - 56));
            int sy = (int)(h * 0.58f) + ((i % 2) * 10 - 5);
            FillCircle(rgba, w, h, sx, sy, radius,
                Math.Clamp((int)(br * fade), 16, 255),
                Math.Clamp((int)(bg * fade), 16, 255),
                Math.Clamp((int)(bb * fade), 16, 255));
        }
    }

    /// <summary>CPU stand-in for OutputParticleDecal: quads projected onto the collision plane.</summary>
    public static void FillDecalPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 16, 18, 24);
        Fill(rgba, w, 0, 0, w, 22, 28, 32, 40);
        int groundSy = (int)(h * 0.72f);
        Fill(rgba, w, 0, groundSy, w, Math.Max(1, h - groundSy), 32, 34, 40);
        Fill(rgba, w, 8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210);
        int dots = Math.Clamp((int)settings.SpawnRate / 8 + 6, 6, 16);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        float nx = settings.CollisionPlane.X;
        float ny = settings.CollisionPlane.Y;
        float nz = settings.CollisionPlane.Z;
        float nd = settings.CollisionPlane.W;
        float size = settings.StartSize <= 0f ? 0.15f : settings.StartSize;
        for (int i = 0; i < dots; i++)
        {
            float px = -1.4f + i * 0.22f;
            float py = 0.35f + (i % 3) * 0.18f;
            float pz = ((i % 2) * 2 - 1) * 0.2f;
            VfxDecal.Project(px, py, pz, nx, ny, nz, nd, out float qx, out float qy, out _, out _, out _, out _);
            int cx = 16 + (int)((qx + 1.6f) * 0.28f * Math.Max(8, w - 32));
            int cy = groundSy - (int)(qy * (h * 0.12f)) - 4;
            int half = Math.Max(3, (int)(size * 48f));
            Fill(rgba, w, cx - half, cy - half / 2, half * 2, half, (byte)Math.Max(20, br * 55 / 100), (byte)Math.Max(20, bg * 55 / 100), (byte)Math.Max(20, bb * 55 / 100));
            Fill(rgba, w, cx - half + 1, cy - half / 2 + 1, Math.Max(2, half * 2 - 2), Math.Max(2, half - 2), (byte)br, (byte)bg, (byte)bb);
            int airX = 16 + (int)((px + 1.6f) * 0.28f * Math.Max(8, w - 32));
            int airY = groundSy - (int)(py * (h * 0.38f));
            FillCircle(rgba, w, h, airX, airY, 2, 90, 210, 255);
        }
    }

    /// <summary>CPU stand-in for OutputParticleMesh: isometric cubes instead of circular billboards.</summary>
    public static void FillMeshPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int dots = Math.Clamp((int)settings.SpawnRate / 4 + 6, 6, 24);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int size = Math.Max(4, (int)(settings.StartSize * 48f));
        for (int i = 0; i < dots; i++)
        {
            int x = 28 + (i * 41) % Math.Max(8, w - 40);
            int y = 48 + (i * 23) % Math.Max(8, h - 56);
            FillIsoCube(rgba, w, h, x, y, size, br, bg, bb);
        }
    }

    private static void FillIsoCube(byte[] rgba, int w, int h, int cx, int cy, int s, int r, int g, int b)
    {
        int hx = Math.Max(3, s);
        int hy = Math.Max(2, s * 2 / 3);
        Fill(rgba, w, cx - hx, cy - hy / 2, hx, hy, (byte)Math.Max(20, r * 70 / 100), (byte)Math.Max(20, g * 70 / 100), (byte)Math.Max(20, b * 70 / 100));
        Fill(rgba, w, cx, cy - hy / 2, hx, hy, (byte)Math.Max(20, r * 55 / 100), (byte)Math.Max(20, g * 55 / 100), (byte)Math.Max(20, b * 55 / 100));
        Fill(rgba, w, cx - hx / 2, cy - hy, hx, hy / 2 + 1, (byte)Math.Clamp(r, 40, 255), (byte)Math.Clamp(g, 40, 255), (byte)Math.Clamp(b, 40, 255));
    }

    /// <summary>CPU stand-in for a Loop host that inlines a Trail subgraph (nested frames + fade).</summary>
    public static void FillSubgraphPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        Stroke(rgba, w, 10, 30, w - 20, h - 40, 70, 170, 210);
        Stroke(rgba, w, 28, 48, w - 56, h - 76, 180, 120, 220);
        int dots = Math.Clamp((int)settings.SpawnRate / 8 + 6, 6, 28);
        int br = (int)Math.Clamp(settings.StartColor.R * 255f, 40f, 255f);
        int bg = (int)Math.Clamp(settings.StartColor.G * 255f, 40f, 255f);
        int bb = (int)Math.Clamp(settings.StartColor.B * 255f, 40f, 255f);
        int radius = Math.Max(2, (int)(settings.StartSize * 36f));
        int innerX = 36;
        int innerY = 56;
        int innerW = Math.Max(8, w - 72);
        int innerH = Math.Max(8, h - 92);
        for (int i = 0; i < dots; i++)
        {
            float t = dots <= 1 ? 1f : i / (float)(dots - 1);
            int x = innerX + 8 + (int)(t * Math.Max(8, innerW - 24));
            int y = innerY + 10 + (i * 17) % Math.Max(8, innerH - 20);
            int fade = (int)(255f * (1f - t * 0.85f));
            FillCircle(rgba, w, h, x, y, radius,
                Math.Clamp(br * fade / 255, 30, 255),
                Math.Clamp(bg * fade / 255, 30, 255),
                Math.Clamp(bb * fade / 255, 30, 255));
        }
        Fill(rgba, w, 14, 34, 52, 8, 70, 170, 210);
        Fill(rgba, w, 32, 52, 44, 8, 180, 120, 220);
        _ = settings.SubgraphCount;
    }

    /// <summary>CPU stand-in for lit Output: sphere-normal Lambert/Blinn discs.</summary>
    public static void FillLitPreviewRgba(byte[] rgba, int w, int h, VfxCompiledSettings settings)
    {
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 22, 32, 32, 38);
        int dots = Math.Clamp((int)settings.SpawnRate / 8 + 6, 6, 24);
        int radius = Math.Max(6, (int)(settings.StartSize * 72f));
        float br = settings.StartColor.R;
        float bg = settings.StartColor.G;
        float bb = settings.StartColor.B;
        float ba = settings.StartColor.A;
        float lx = settings.LightDirection.X;
        float ly = settings.LightDirection.Y;
        float lz = settings.LightDirection.Z;
        float cr = settings.LightColor.R;
        float cg = settings.LightColor.G;
        float cb = settings.LightColor.B;
        float intensity = settings.LightColor.A;
        float ar = settings.AmbientColor.R;
        float ag = settings.AmbientColor.G;
        float ab = settings.AmbientColor.B;
        for (int i = 0; i < dots; i++)
        {
            int cx = 28 + (i * 47) % Math.Max(8, w - 40);
            int cy = 52 + (i * 29) % Math.Max(8, h - 64);
            FillLitDisc(rgba, w, h, cx, cy, radius, br, bg, bb, ba, lx, ly, lz, cr, cg, cb, intensity, ar, ag, ab, settings.LitMode, settings.Shininess);
        }
    }

    private static void FillLitDisc(
        byte[] rgba, int w, int h, int cx, int cy, int radius,
        float br, float bg, float bb, float ba,
        float lx, float ly, float lz,
        float cr, float cg, float cb, float intensity,
        float ar, float ag, float ab,
        float mode, float shininess)
    {
        int r2 = radius * radius;
        for (int py = cy - radius; py <= cy + radius; py++)
        {
            if ((uint)py >= (uint)h) continue;
            int row = py * w * 4;
            for (int px = cx - radius; px <= cx + radius; px++)
            {
                if ((uint)px >= (uint)w) continue;
                int dx = px - cx, dy = py - cy;
                if (dx * dx + dy * dy > r2) continue;
                float nx = dx / (float)radius;
                float ny = -dy / (float)radius;
                float nz2 = 1f - nx * nx - ny * ny;
                float nz = nz2 > 0f ? MathF.Sqrt(nz2) : 0f;
                VfxLighting.Shade(
                    br, bg, bb, ba, nx, ny, nz, 0f, 0f, 1f,
                    lx, ly, lz, cr, cg, cb, intensity, ar, ag, ab, mode, shininess,
                    out float or, out float og, out float ob, out _);
                int i = row + px * 4;
                rgba[i] = (byte)Math.Clamp((int)(or * 255f), 0, 255);
                rgba[i + 1] = (byte)Math.Clamp((int)(og * 255f), 0, 255);
                rgba[i + 2] = (byte)Math.Clamp((int)(ob * 255f), 0, 255);
                rgba[i + 3] = 255;
            }
        }
    }

    private static void StrokeRing(byte[] rgba, int w, int h, int cx, int cy, int radius, byte r, byte g, byte b)
    {
        int outer2 = radius * radius;
        int inner = Math.Max(0, radius - 2);
        int inner2 = inner * inner;
        for (int py = cy - radius; py <= cy + radius; py++)
        {
            if ((uint)py >= (uint)h) continue;
            int row = py * w * 4;
            for (int px = cx - radius; px <= cx + radius; px++)
            {
                if ((uint)px >= (uint)w) continue;
                int dx = px - cx, dy = py - cy;
                int d2 = dx * dx + dy * dy;
                if (d2 > outer2 || d2 < inner2) continue;
                int i = row + px * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }
        }
    }

    private static void Stroke(byte[] rgba, int w, int x, int y, int bw, int bh, byte r, byte g, byte b)
    {
        Fill(rgba, w, x, y, bw, 2, r, g, b);
        Fill(rgba, w, x, y + bh - 2, bw, 2, r, g, b);
        Fill(rgba, w, x, y, 2, bh, r, g, b);
        Fill(rgba, w, x + bw - 2, y, 2, bh, r, g, b);
    }

    private static void Fill(byte[] rgba, int w, int x, int y, int bw, int bh, byte r, byte g, byte b)
    {
        int x1 = Math.Min(w, x + bw);
        int y1 = Math.Min(rgba.Length / (w * 4), y + bh);
        for (int py = Math.Max(0, y); py < y1; py++)
        {
            int row = py * w * 4;
            for (int px = Math.Max(0, x); px < x1; px++)
            {
                int i = row + px * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }
        }
    }

    private static void FillCircle(byte[] rgba, int w, int h, int cx, int cy, int radius, int r, int g, int b)
    {
        int r2 = radius * radius;
        for (int py = cy - radius; py <= cy + radius; py++)
        {
            if ((uint)py >= (uint)h) continue;
            int row = py * w * 4;
            for (int px = cx - radius; px <= cx + radius; px++)
            {
                if ((uint)px >= (uint)w) continue;
                int dx = px - cx, dy = py - cy;
                if (dx * dx + dy * dy > r2) continue;
                int i = row + px * 4;
                rgba[i] = (byte)r; rgba[i + 1] = (byte)g; rgba[i + 2] = (byte)b; rgba[i + 3] = 255;
            }
        }
    }

    private static void FillCircleChecker(byte[] rgba, int w, int h, int cx, int cy, int radius, int r, int g, int b)
    {
        int r2 = radius * radius;
        int cell = Math.Max(2, radius / 2);
        for (int py = cy - radius; py <= cy + radius; py++)
        {
            if ((uint)py >= (uint)h) continue;
            int row = py * w * 4;
            for (int px = cx - radius; px <= cx + radius; px++)
            {
                if ((uint)px >= (uint)w) continue;
                int dx = px - cx, dy = py - cy;
                if (dx * dx + dy * dy > r2) continue;
                bool dark = (((px / cell) + (py / cell)) & 1) == 0;
                int i = row + px * 4;
                rgba[i] = (byte)(dark ? r / 2 : r);
                rgba[i + 1] = (byte)(dark ? g / 2 : g);
                rgba[i + 2] = (byte)(dark ? b / 2 : b);
                rgba[i + 3] = 255;
            }
        }
    }
}
