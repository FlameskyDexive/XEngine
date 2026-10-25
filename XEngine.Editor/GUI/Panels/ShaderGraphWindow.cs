// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Scribe;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// Shader Graph DockPanel (M26). Property sidebar + Origami NodeGraph canvas + compile preview.
/// </summary>
public sealed class ShaderGraphWindow : DockPanel, IScriptReloadCleanup
{
    public override string Title => "Shader Graph";
    public override string Icon => EditorIcons.WandMagicSparkles;

    private const float ToolbarHeight = 32f;
    private const float SplitterSize = 4f;
    private const float MinSidebar = 140f;
    private const float MaxSidebar = 360f;

    private ShaderGraphAsset? _graph;
    private bool _subscribed;
    private bool _graphDirty = true;
    private float _sidebarWidth = 200f;
    private string? _selectedNodeId;
    private string _compileError = "";
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphConnection> _wires = new();
    private readonly NodeGraphController _ctrl = new();
    private readonly List<string> _portScratchIn = new();
    private readonly List<string> _portScratchOut = new();

    public ShaderGraphAsset? Graph => _graph;
    public NodeGraphController GraphController => _ctrl;
    public IReadOnlyList<GraphNode> GraphNodes => _nodes;
    public IReadOnlyList<GraphConnection> Wires => _wires;
    public string? SelectedNodeId => _selectedNodeId;
    public string CompileError => _compileError;
    public float SidebarWidth
    {
        get => _sidebarWidth;
        set => _sidebarWidth = Math.Clamp(value, MinSidebar, MaxSidebar);
    }

    [MenuItem("Window/Rendering/Shader Graph", priority: 22)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(ShaderGraphWindow));

    public static void OpenGraph(ShaderGraphAsset? graph)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;
        app.OpenPanel(typeof(ShaderGraphWindow));
        if (app.FindOpenPanel(typeof(ShaderGraphWindow)) is ShaderGraphWindow win)
            win.SetGraph(graph);
    }

    public void OnScriptReloadCleanup() => SetGraph(null);

    public override void OnClosed()
    {
        if (_subscribed) { Selection.OnSelectionChanged -= TryLoadFromSelection; _subscribed = false; }
    }

    public override bool SerializeState(JsonObject state)
    {
        state["sidebarWidth"] = _sidebarWidth;
        return true;
    }

    public override void RestoreState(JsonObject state)
    {
        if (state.TryGetPropertyValue("sidebarWidth", out JsonNode? sw) && sw is JsonValue sv && sv.TryGetValue(out float sidebar))
            _sidebarWidth = Math.Clamp(sidebar, MinSidebar, MaxSidebar);
    }

    public void SetGraph(ShaderGraphAsset? graph)
    {
        _graph = graph;
        _selectedNodeId = null;
        _compileError = "";
        _graphDirty = true;
        if (_graph != null)
            EnsureDefaultLayout(_graph);
        RebuildGraph();
    }

    public string CompilePreview()
    {
        if (_graph == null)
            return "";
        try
        {
            _compileError = "";
            return ShaderGraphCompiler.CompileToSource(_graph);
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
        for (int i = _graph.Edges.Count - 1; i >= 0; i--)
        {
            ShaderGraphEdge e = _graph.Edges[i];
            if (string.Equals(e.ToNode, toNode, StringComparison.Ordinal)
                && string.Equals(e.ToPort, toPort, StringComparison.Ordinal))
                _graph.Edges.RemoveAt(i);
        }
        _graph.Edges.Add(new ShaderGraphEdge
        {
            Id = "e" + _graph.Edges.Count,
            FromNode = fromNode,
            FromPort = fromPort,
            ToNode = toNode,
            ToPort = toPort,
        });
        _graphDirty = true;
        RebuildGraph();
        return true;
    }

    public bool TryDisconnect(string toNode, string toPort)
    {
        if (_graph == null) return false;
        int removed = 0;
        for (int i = _graph.Edges.Count - 1; i >= 0; i--)
        {
            ShaderGraphEdge e = _graph.Edges[i];
            if (string.Equals(e.ToNode, toNode, StringComparison.Ordinal)
                && string.Equals(e.ToPort, toPort, StringComparison.Ordinal))
            {
                _graph.Edges.RemoveAt(i);
                removed++;
            }
        }
        if (removed == 0) return false;
        _graphDirty = true;
        RebuildGraph();
        return true;
    }

    public void ApplyNodePosition(string nodeId, float x, float y)
    {
        if (_graph == null) return;
        for (int i = 0; i < _graph.Nodes.Count; i++)
        {
            if (!string.Equals(_graph.Nodes[i].Id, nodeId, StringComparison.Ordinal))
                continue;
            _graph.Nodes[i].PosX = x;
            _graph.Nodes[i].PosY = y;
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
        using (paper.Column("sg_root").Size(width, height).Enter())
        {
            DrawToolbar(paper, width, font);
            float bodyH = Math.Max(80f, height - ToolbarHeight);
            float sidebar = Math.Min(_sidebarWidth, Math.Max(MinSidebar, width - 180f));
            using (paper.Row("sg_body").Width(width).Height(bodyH).Enter())
            {
                DrawSidebar(paper, sidebar, bodyH, font);
                paper.Box("sg_split").Width(SplitterSize).Height(bodyH).BackgroundColor(EditorTheme.BorderSoft);
                DrawCanvas(paper, width - sidebar - SplitterSize, bodyH, font);
            }
        }
    }

    private void TryLoadFromSelection()
    {
        if (Selection.ActiveObject is ShaderGraphAsset graph)
        {
            if (graph != _graph)
                SetGraph(graph);
            return;
        }
        if (Selection.ActiveObject is not ContentItem ci || ci.IsFolder || ci.IsSubAsset) return;
        if (!ci.RelativePath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)) return;
        Guid guid = ci.Guid;
        if (guid == Guid.Empty)
        {
            var entry = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath);
            guid = entry?.Guid ?? Guid.Empty;
        }
        if (guid == Guid.Empty) return;
        if (AssetDatabase.Get(guid) is ShaderGraphAsset loaded && loaded != _graph)
            SetGraph(loaded);
    }

    private void DrawToolbar(Paper paper, float width, FontFile? font)
    {
        using (paper.Row("sg_tb").Width(width).Height(ToolbarHeight).Padding(8, 4, 4, 4).Enter())
        {
            string name = _graph == null ? "No graph" : _graph.ShaderName;
            if (font != null)
                paper.Box("sg_tb_name").Width(Math.Max(120f, width - 280f)).Height(24)
                    .Text(name, font).TextColor(EditorTheme.Ink500)
                    .Alignment(XEngine.PaperUI.TextAlignment.MiddleLeft);
            Origami.Button(paper, "sg_compile", "Compile", () => CompilePreview()).Show();
            if (!string.IsNullOrEmpty(_compileError) && font != null)
                paper.Box("sg_err").Width(160).Height(24).Text(_compileError, font).TextColor(EditorTheme.Red400);
        }
    }

    private void DrawSidebar(Paper paper, float w, float h, FontFile? font)
    {
        using (paper.Column("sg_side").Width(w).Height(h).Padding(8, 8, 8, 8).BackgroundColor(EditorTheme.Neutral300).Enter())
        {
            if (font != null)
                paper.Box("sg_side_h").Height(22).Text("Properties", font).TextColor(EditorTheme.Ink500);
            if (_graph == null)
            {
                if (font != null)
                    paper.Box("sg_side_empty").Height(40).Text("Open a .shadergraph", font).TextColor(EditorTheme.Ink400);
                return;
            }
            Origami.EnumDropdown(paper, "sg_target", _graph.Target, v => _graph.Target = v).Show();
            for (int i = 0; i < _graph.Properties.Count; i++)
            {
                ShaderGraphProperty p = _graph.Properties[i];
                if (font != null)
                    paper.Box($"sg_p{i}").Height(20).Text($"{p.Type}  {p.Name}", font).TextColor(EditorTheme.Ink400);
            }
            if (_selectedNodeId != null && font != null)
                paper.Box("sg_sel").Height(22).Text("Node " + _selectedNodeId, font).TextColor(EditorTheme.Ink500);
        }
    }

    private void DrawCanvas(Paper paper, float w, float h, FontFile? font)
    {
        if (_graph == null)
        {
            var empty = paper.Box("sg_canvas_empty").Width(w).Height(h)
                .BackgroundColor(EditorTheme.Neutral100)
                .TextColor(EditorTheme.Ink400)
                .Alignment(XEngine.PaperUI.TextAlignment.MiddleCenter);
            if (font != null)
                empty.Text("Select a Shader Graph asset", font);
            return;
        }

        Origami.NodeGraph(paper, "sg_graph", w, h)
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
                for (int i = 0; i < nodes.Count; i++)
                {
                    GraphNode moved = nodes[i];
                    for (int j = 0; j < _graph.Nodes.Count; j++)
                    {
                        if (!string.Equals(_graph.Nodes[j].Id, moved.Id, StringComparison.Ordinal))
                            continue;
                        _graph.Nodes[j].PosX = moved.Position.X;
                        _graph.Nodes[j].PosY = moved.Position.Y;
                        break;
                    }
                }
                _graphDirty = true;
                RebuildGraph();
            })
            .OnConnect(req => TryConnect(req.FromNode, req.FromPort, req.ToNode, req.ToPort))
            .OnDeleteSelection(sel =>
            {
                for (int i = 0; i < sel.Edges.Count; i++)
                    TryDisconnect(sel.Edges[i].ToNode, sel.Edges[i].ToPort);
            })
            .Show();
    }

    private void RebuildGraph()
    {
        _nodes.Clear();
        _wires.Clear();
        _graphDirty = false;
        if (_graph == null)
            return;
        for (int i = 0; i < _graph.Nodes.Count; i++)
        {
            ShaderGraphNode src = _graph.Nodes[i];
            ShaderGraphNodeLibrary.GetPorts(src.Type, _portScratchIn, _portScratchOut);
            var node = new GraphNode
            {
                Id = src.Id,
                Title = string.IsNullOrEmpty(src.Type) ? src.Id : src.Type,
                Position = new Float2(src.PosX, src.PosY),
                Width = 168f,
                Accent = AccentFor(src.Type),
            };
            for (int p = 0; p < _portScratchIn.Count; p++)
                node.Inputs.Add(new GraphPort(_portScratchIn[p], _portScratchIn[p]));
            for (int p = 0; p < _portScratchOut.Count; p++)
                node.Outputs.Add(new GraphPort(_portScratchOut[p], _portScratchOut[p]));
            _nodes.Add(node);
        }
        for (int i = 0; i < _graph.Edges.Count; i++)
        {
            ShaderGraphEdge e = _graph.Edges[i];
            _wires.Add(new GraphConnection(e.FromNode, e.FromPort, e.ToNode, e.ToPort));
        }
    }

    private static void EnsureDefaultLayout(ShaderGraphAsset graph)
    {
        bool allZero = true;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            if (graph.Nodes[i].PosX != 0f || graph.Nodes[i].PosY != 0f)
            {
                allZero = false;
                break;
            }
        }
        if (!allZero) return;
        for (int i = 0; i < graph.Nodes.Count; i++)
        {
            graph.Nodes[i].PosX = 32f + (i % 4) * 196f;
            graph.Nodes[i].PosY = 28f + (i / 4) * 108f;
        }
    }

    private static Color AccentFor(string type)
    {
        if (string.Equals(type, ShaderGraphNodeLibrary.MasterUnlit, StringComparison.Ordinal)
            || string.Equals(type, ShaderGraphNodeLibrary.MasterLit, StringComparison.Ordinal)
            || string.Equals(type, ShaderGraphNodeLibrary.MasterDecal, StringComparison.Ordinal)
            || string.Equals(type, ShaderGraphNodeLibrary.MasterFullscreen, StringComparison.Ordinal))
            return Color.FromArgb(255, 210, 90, 70);
        if (string.Equals(type, ShaderGraphNodeLibrary.Fresnel, StringComparison.Ordinal))
            return Color.FromArgb(255, 90, 180, 220);
        if (string.Equals(type, ShaderGraphNodeLibrary.SampleTexture2D, StringComparison.Ordinal)
            || string.Equals(type, ShaderGraphNodeLibrary.UV, StringComparison.Ordinal)
            || string.Equals(type, ShaderGraphNodeLibrary.TilingAndOffset, StringComparison.Ordinal))
            return Color.FromArgb(255, 120, 200, 140);
        return Color.FromArgb(255, 180, 180, 190);
    }
}
