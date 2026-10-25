using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.Core;
using Projects = XEngine.Editor.Projects;
using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.FSM;
using XEngine.NodePlay.Dialogue;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Vector;

using Color = System.Drawing.Color;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// The NodePlay graph editor window: one DockPanel hosting the shared Origami NodeGraph canvas
/// over a persisted <see cref="NPGraphAsset"/>. Widget events are translated back into domain
/// edits here (the widget itself never mutates data); <c>Undo.Snapshot(_graph)</c> at frame start
/// makes every framed mutation one coalesced undo step — the VfxGraphWindow contract.
/// </summary>
public sealed class NodePlayWindow : DockPanel, IScriptReloadCleanup
{
    // Model mirror handed to the widget each frame; rebuilt only when StructureVersion changes.
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphConnection> _wires = new();
    private readonly List<GraphGroup> _groups = new();
    private readonly List<GraphSticky> _stickies = new();
    private readonly NodeGraphController _controller = new();

    private NPGraphAsset? _graph;
    private bool _dirty;
    private bool _live;
    private int _mirroredVersion = -1;
    private string _entryPath = "";
    private float _sidebarWidth = 280f;
    private string? _inspectId;
    private string? _selectedEdgeId;
    private string _search = "";
    private string _sideTab = "ins";

    public override string Title => "NodePlay";

    [MenuItem("Window/NodePlay/Graph Editor", priority: 6)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(NodePlayWindow));

    [AssetDoubleClickHandler(NPGraphAsset.FileExtension)]
    private static bool OnDoubleClick(string relativePath, Guid guid)
    {
        if (guid == Guid.Empty)
            return false;
        if (AssetDatabase.Get(guid) is NPGraphAsset graph)
        {
            OpenGraph(graph);
            return true;
        }

        return false;
    }

    public static void OpenGraph(NPGraphAsset? graph)
    {
        var app = EditorApplication.Instance;
        if (app == null)
            return;
        app.OpenPanel(typeof(NodePlayWindow));
        if (app.FindOpenPanel(typeof(NodePlayWindow)) is NodePlayWindow win)
            win.SetGraph(graph);
    }

    public void SetGraph(NPGraphAsset? graph)
    {
        _graph = graph;
        _inspectId = null;
        NPGraphConsole.Install();
        _mirroredVersion = -1;
        _dirty = false;
        _entryPath = graph != null && graph.AssetID != Guid.Empty
            ? EditorAssetBackend.Instance?.GuidToPath(graph.AssetID) ?? ""
            : "";
        _controller.ClearSelection();
    }

    public void OnScriptReloadCleanup() => SetGraph(null);

    public override bool SerializeState(JsonObject state)
    {
        state["sidebarWidth"] = _sidebarWidth;
        return true;
    }

    public override void RestoreState(JsonObject state)
    {
        if (state.TryGetPropertyValue("sidebarWidth", out System.Text.Json.Nodes.JsonNode? sw)
            && sw is System.Text.Json.Nodes.JsonValue sv && sv.TryGetValue(out float w))
            _sidebarWidth = Math.Clamp(w, 220f, 420f);
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        if (_graph == null)
        {
            Origami.Label(paper, "np_empty",
                    "No graph open - double-click a .nodeplay asset or create one under Assets > Create > NodePlay.").Show();
            return;
        }

        Undo.Snapshot(_graph);

        if (_mirroredVersion != _graph.StructureVersion)
            RebuildMirror();

        if (_live)
            ApplyLiveStatus();

        DrawToolbar(paper);
        float bodyH = height - 30f;
        DrawCanvasWithPanels(paper, width, bodyH - 86f);
        NodePlayMinimap.Draw(paper, "np_mini", width, _controller, _nodes);
    }

    private void DrawCanvasWithPanels(Paper paper, float width, float height)
    {
        float sidebar = Math.Clamp(_sidebarWidth, 220f, Math.Max(220f, width - 320f));

        using (paper.Row("np_body").Height(height).Enter())
        {
            using (paper.Column("np_side").Width(sidebar).Height(height).Enter())
            {
                // Tab strip caps the sidebar height budget: exactly one section per frame.
                using (paper.Row("np_tabs").Height(26).Enter())
                {
                    Origami.Button(paper, "np_tab_vars", "Vars", () => _sideTab = "vars").Show();
                    Origami.Button(paper, "np_tab_exp", "Find", () => _sideTab = "exp").Show();
                    Origami.Button(paper, "np_tab_ins", "Edit", () => _sideTab = "ins").Show();
                    Origami.Button(paper, "np_tab_log", "Log", () => _sideTab = "log").Show();
                    Origami.Button(paper, "np_tab_act", "Actors", () => _sideTab = "act").Show();
                }

                switch (_sideTab)
                {
                    case "act":
                    {
                        var actors = _graph is NPDialogueAsset dlg ? dlg.ActorParameters : new List<NPActorParameter>();
                        for (int aIdx = 0; aIdx < actors.Count; aIdx++)
                        {
                            NPActorParameter actor = actors[aIdx];
                            int row = aIdx;
                            Origami.TextField(paper, $"np_act_k{row}", actor.Key, v =>
                            {
                                actor.Key = v ?? "";
                                MarkDirty();
                            }).Show();
                            Origami.TextField(paper, $"np_act_n{row}", actor.Name, v =>
                            {
                                actor.Name = v ?? "";
                                MarkDirty();
                            }).Show();
                        }

                        Origami.Button(paper, "np_act_add", "+ actor", () =>
                        {
                            if (_graph is NPDialogueAsset dialogue)
                            {
                                dialogue.ActorParameters.Add(new NPActorParameter
                                {
                                    Key = "actor" + (dialogue.ActorParameters.Count + 1),
                                    Name = "New Actor",
                                });
                                MarkDirty();
                            }
                        }).Show();
                        break;
                    }

                    case "vars":
                    {
                        var variables = _graph!.LocalBlackboard.All;
                        for (int vIdx = 0; vIdx < variables.Count; vIdx++)
                        {
                            NPVariable variable = variables[vIdx];
                            Origami.Label(paper, $"np_var{vIdx}",
                                variable.Name + (variable.IsPublic ? " (pub)" : "") + " = " + variable.Value).Show();
                        }

                        NodePlayBlackboardPanel.DrawAddRow(paper, "np", _graph!, MarkDirty);
                        break;
                    }

                    case "exp":
                    {
                        Origami.TextField(paper, "np_search", _search, v => _search = v ?? "").Show();
                        var matches = NodePlayExplorer.Filter(_graph!.Nodes, _search);
                        for (int mIdx = 0; mIdx < Math.Min(matches.Count, 10); mIdx++)
                        {
                            NPNode match = matches[mIdx];
                            Origami.Button(paper, $"np_ex{mIdx}", match.DisplayName, () =>
                            {
                                _inspectId = match.Id;
                                _controller.FocusNode(match.Id);
                            }).Show();
                        }
                        break;
                    }

                    case "log":
                    {
                        Origami.Button(paper, "np_cons_clr", "clear", () => NPGraphConsole.Clear()).Show();
                        var entries = NPGraphConsole.All;
                        for (int cIdx = entries.Count - 1; cIdx >= 0 && cIdx >= entries.Count - 12; cIdx--)
                        {
                            var entry = entries[cIdx];
                            string nodeTag = entry.NodeId != null ? " @" + (Find(entry.NodeId)?.DisplayName ?? "?") : "";
                            string capturedId = entry.NodeId ?? "";
                            Origami.Button(paper, $"np_cons{cIdx}",
                                entry.Severity + ": " + entry.Message + nodeTag, () =>
                            {
                                if (capturedId.Length > 0)
                                {
                                    _inspectId = capturedId;
                                    _controller.FocusNode(capturedId);
                                }
                            }).Show();
                        }
                        break;
                    }

                    default:
                    {
                        if (_selectedEdgeId != null)
                        {
                            NPConnection? wire = FindWire(_selectedEdgeId);
                            Origami.Label(paper, "np_insp_edge",
                                wire is FSMConnection fsmWire
                                    ? (fsmWire.Condition != null ? "Transition (conditional)" : "Transition (OnFinish)")
                                    : "Wire").Show();
                            if (wire != null)
                                Origami.PropertyGrid(paper, "np_insp", wire, null).Show();
                        }
                        else
                        {
                            string? selectedId = _inspectId
                                ?? (_controller.SelectedNodes.Count > 0 ? _controller.SelectedNodes[0] : null);
                            NPNode? selected = selectedId != null ? Find(selectedId) : null;
                            if (selected != null)
                                Origami.PropertyGrid(paper, "np_insp", selected, null).Show();
                            else
                                Origami.Label(paper, "np_insp_none", "Select a node or transition.").Show();
                        }
                        break;
                    }
                }

                Origami.Button(paper, "np_side_drag", "◀▶", () =>
                {
                    _sidebarWidth = Math.Clamp(_sidebarWidth + 24f, 220f, 420f);
                }).Show();
            } // sidebar column

            DrawCanvas(paper, width - sidebar, height);
        } // np_body row
    }

    private void DrawToolbar(Paper paper)
    {
        string title = _graph!.GraphName.Length > 0 ? _graph.GraphName : "(untitled)";
        if (_dirty)
            title += "  *";
        using (paper.Row("np_tb").Height(30).Enter())
        {
            Origami.Label(paper, "np_title", title).Show();

            Origami.Button(paper, "np_save", "Save", Save).Show();
            Origami.Button(paper, "np_live", _live ? "Live: ON" : "Live: OFF", () => _live = !_live)
                .Tooltip("Follow a running runner and paint live node statuses").Show();
            Origami.Button(paper, "np_copy", "Copy", CopySelection).Show();
            Origami.Button(paper, "np_paste", "Paste", PasteClipboard).Show();
            Origami.Button(paper, "np_export", "Export JSON", ExportJson).Show();
            Origami.Button(paper, "np_import", "Import JSON", ImportJson).Show();
            Origami.Button(paper, "np_add_relay", "+ Relay", () =>
            {
                Float2 center = ViewCenter();
                var relay = _graph!.AddNode(new NPRelayNode { Position = center });
                _graph.BuildTopology();
                _nodes.Add(ToWidget(relay));
                MarkDirty();
            }).Show();
            Origami.Button(paper, "np_add_node", "+ Node", OpenAddNodeBrowser).Show();
            Origami.Button(paper, "np_wizard", "Wizard", NodePlayTaskWizard.Show)
                .Tooltip("Scaffold a new task source file into Assets/Scripts/NodePlay").Show();
        }
    }

    private void OpenAddNodeBrowser()
    {
        Type? baseType = _graph switch
        {
            NPBehaviourTreeAsset => typeof(BTNode),
            NPFSMAsset => typeof(NPNode),          // FSM base type lands with the FSM module
            NPDialogueAsset => typeof(NPNode),     // dialogue base type lands with the dialogue module
            _ => typeof(NPNode),
        };

        NodePlayBrowser.Show(baseType, type =>
        {
            if (Activator.CreateInstance(type) is not NPNode node)
                return;
            node.Position = ViewCenter();
            _graph!.AddNode(node);
            _graph.BuildTopology();
            _nodes.Add(ToWidget(node));
            MarkDirty();
        });
    }

    private void DrawCanvas(Paper paper, float width, float height)
    {
        Origami.NodeGraph(paper, "nodeplay_canvas", width, height)
            .Nodes(_nodes)
            .Connections(_wires)
            .Groups(_groups)
            .Stickies(_stickies)
            .Grid(true)
            .InitialView(new Float2(40f, 30f), 0.85f)
            .Controller(_controller)
            .OnNodesMoved(OnNodesMoved)
            .OnConnect(OnConnectRequested)
            .OnValidateConnection(CanConnectPreview)
            .OnDeleteSelection(OnDeleteSelection)
            .OnWireAddPoint(OnWirePointAdded)
            .OnWirePointMoved(OnWirePointChanged)
            .OnWireRemovePoint(OnWirePointRemoved)
            .OnGroupMoved((_, _, _) => WriteBackGroups())
            .OnGroupResized((_, _, _) => WriteBackGroups())
            .OnGroupRenamed((_, _) => WriteBackGroups())
            .OnStickyMoved((_, _) => WriteBackStickies())
            .OnStickyResized((_, _, _) => WriteBackStickies())
            .OnStickyEdited((_, _) => WriteBackStickies())
            .OnBackgroundContext(OnCanvasContext)
            .Show();
    }

    // ── mirroring ─────────────────────────────────────────────────────────────────────────

    private void RebuildMirror()
    {
        _nodes.Clear();
        _wires.Clear();
        _groups.Clear();
        _stickies.Clear();

        foreach (NPNode node in _graph!.Nodes)
            _nodes.Add(ToWidget(node));

        foreach (NPConnection wire in _graph.Connections)
        {
            var c = new GraphConnection(wire.FromNodeId, "out", wire.ToNodeId, "in")
            {
                UserData = wire.Id,
                Color = null, // TODO(M15b): conditional-edge tint once widget paint path validated
            };
            c.ControlPoints.AddRange(wire.Points);
            _wires.Add(c);
        }

        foreach (NPGroup group in _graph.Groups)
        {
            _groups.Add(new GraphGroup
            {
                Id = group.Id,
                Title = group.Title,
                Position = new Float2(group.X, group.Y),
                Size = new Float2(group.Width, group.Height),
                UserData = group,
            });
        }

        foreach (NPSticky sticky in _graph.Stickies)
        {
            _stickies.Add(new GraphSticky
            {
                Id = sticky.Id,
                Text = sticky.Text,
                Position = new Float2(sticky.X, sticky.Y),
                Size = new Float2(sticky.Width, sticky.Height),
                UserData = sticky,
            });
        }

        _mirroredVersion = _graph.StructureVersion;
    }

    internal static GraphNode ToWidget(NPNode node)
    {
        bool prime = node.Graph != null && ReferenceEquals(node, node.Graph.PrimeNode);
        bool fsm = node is FSM.FSMNode;
        var widget = new GraphNode
        {
            Id = node.Id,
            Title = node.DisplayName + (prime ? "  >>" : ""),
            Position = node.Position,
            Width = 168f,
            Accent = node.Disabled ? Color.FromArgb(90, 90, 96)
                   : fsm ? Color.FromArgb(0x64, 0xB5, 0xF6)
                   : Color.FromArgb(0xE4, 0xB9, 0x54),
            UserData = node,
        };

        PortSide inSide = fsm ? PortSide.Left : PortSide.Top;
        PortSide outSide = fsm ? PortSide.Right : PortSide.Bottom;
        widget.Inputs.Add(new GraphPort("in", "") { Side = inSide, Shape = PortShape.Arrow });
        widget.Outputs.Add(new GraphPort("out", "") { Side = outSide, Shape = PortShape.Arrow });
        if (node is Dialogue.DTNode)
            widget.Accent = Color.FromArgb(0x81, 0xC7, 0x84);
        return widget;
    }

    /// <summary>Pull everything the widget may have mutated back into the domain model.</summary>
    private void MirrorFromWidget()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].UserData is NPNode node)
                node.Position = _nodes[i].Position;
        }

        WriteBackGroups();
        WriteBackStickies();
    }

    private void WriteBackGroups()
    {
        if (_graph == null)
            return;
        foreach (GraphGroup g in _groups)
        {
            if (g.UserData is NPGroup domain)
            {
                domain.X = g.Position.X;
                domain.Y = g.Position.Y;
                domain.Width = g.Size.X;
                domain.Height = g.Size.Y;
                domain.Title = g.Title;
            }
        }
    }

    private void WriteBackStickies()
    {
        if (_graph == null)
            return;
        foreach (GraphSticky s in _stickies)
        {
            if (s.UserData is NPSticky domain)
            {
                domain.X = s.Position.X;
                domain.Y = s.Position.Y;
                domain.Width = s.Size.X;
                domain.Height = s.Size.Y;
                domain.Text = s.Text;
            }
        }
    }

    // ── canvas event handlers ──────────────────────────────────────────────────────────────

    private void OnSelectionChanged(GraphSelection selection)
    {
        _selectedEdgeId = selection.Edges.Count > 0 && selection.Edges[0].UserData is string id ? id : null;
        if (_selectedEdgeId != null)
            _inspectId = null;
    }

    private void OnNodesMoved(IReadOnlyList<GraphNode> moved, Float2 delta) => MarkDirty();

    private bool CanConnectPreview(ConnectionRequest request)
        => Find(request.FromNode) is { } from
           && Find(request.ToNode) is { } to
           && _graph!.IsConnectionAllowed(from, to);

    private void OnConnectRequested(ConnectionRequest request)
    {
        if (Find(request.FromNode) is not { } from || Find(request.ToNode) is not { } to)
            return;

        NPConnection? created = _graph!.Connect(from, to, out string? error);
        if (created == null)
        {
            Toasts.Warning("NodePlay", error ?? "Connection rejected.");
            return;
        }

        _wires.Add(new GraphConnection(request.FromNode, "out", request.ToNode, "in")
        {
            UserData = created.Id,
        });
        MarkDirty();
    }

    private void OnDeleteSelection(GraphSelection selection)
    {
        foreach (GraphNode widgetNode in selection.Nodes)
        {
            if (Find(widgetNode.Id) is { } node)
                _graph!.RemoveNode(node);
        }

        foreach (GraphConnection edge in selection.Edges)
            RemoveWire(edge);

        foreach (GraphGroup group in selection.Groups)
        {
            if (group.UserData is NPGroup domainGroup)
                _graph!.Groups.Remove(domainGroup);
            _groups.Remove(group);
        }

        foreach (GraphSticky sticky in selection.Stickies)
        {
            if (sticky.UserData is NPSticky domainSticky)
                _graph!.Stickies.Remove(domainSticky);
            _stickies.Remove(sticky);
        }

        RebuildMirror();
        MarkDirty();
    }

    private void RemoveWire(GraphConnection edge)
    {
        if (_graph == null || edge.UserData is not string id)
            return;
        for (int i = _graph.Connections.Count - 1; i >= 0; i--)
        {
            if (_graph.Connections[i].Id == id)
            {
                _graph.Disconnect(_graph.Connections[i]);
                break;
            }
        }
    }

    private void OnWirePointAdded(GraphConnection edge, int index, Float2 point)
        => MutateWirePoints(edge, pts => pts.Insert(index, point));

    private void OnWirePointChanged(GraphConnection edge, int index, Float2 point)
        => MutateWirePoints(edge, pts => pts[index] = point);

    private void OnWirePointRemoved(GraphConnection edge, int index)
        => MutateWirePoints(edge, pts => pts.RemoveAt(index));

    private void MutateWirePoints(GraphConnection edge, Action<List<Float2>> mutation)
    {
        if (edge.UserData is not string id || _graph == null)
            return;
        for (int i = 0; i < _graph.Connections.Count; i++)
        {
            if (_graph.Connections[i].Id == id)
            {
                mutation(_graph.Connections[i].Points);
                MarkDirty();
                return;
            }
        }
    }

    private void OnCanvasContext(Float2 at)
    {
        ContextMenu.ShowAtPointer(build =>
        {
            build.Item("Add Relay", () =>
            {
                var relay = _graph!.AddNode(new NPRelayNode { Position = at });
                _graph.BuildTopology();
                _nodes.Add(ToWidget(relay));
                MarkDirty();
            });
            build.Separator();
            build.Item("Add Group", () =>
            {
                var g = new NPGroup { X = at.X - 40f, Y = at.Y - 24f };
                _graph!.Groups.Add(g);
                _groups.Add(new GraphGroup
                {
                    Id = g.Id,
                    Title = g.Title,
                    Position = new Float2(g.X, g.Y),
                    Size = new Float2(g.Width, g.Height),
                    UserData = g,
                });
                MarkDirty();
            });
            build.Item("Add Sticky", () =>
            {
                var s = new NPSticky { X = at.X, Y = at.Y, Text = "Note" };
                _graph!.Stickies.Add(s);
                _stickies.Add(new GraphSticky
                {
                    Id = s.Id,
                    Text = s.Text,
                    Position = new Float2(at.X, at.Y),
                    UserData = s,
                });
                MarkDirty();
            });
        });
    }

    // ── clipboard / JSON round-trip ───────────────────────────────────────────────────────

    private void CopySelection()
    {
        if (_graph == null)
            return;

        var picked = CollectSelection();
        if (picked.Nodes.Count == 0 && picked.Groups.Count == 0 && picked.Stickies.Count == 0)
            return;

        NodePlayClipboard.Copy(_graph, picked.Nodes, picked.Groups, picked.Stickies);
        Toasts.Info("NodePlay", $"Copied {picked.Nodes.Count} node(s).");
    }

    private void PasteClipboard()
    {
        if (_graph == null)
            return;

        string text = NodePlayClipboard.LastText.Length > 0 ? NodePlayClipboard.LastText : Input.Clipboard;
        var newGroups = new List<NPGroup>();
        var newStickies = new List<NPSticky>();
        IReadOnlyList<NPNode> pasted = NodePlayClipboard.PasteInto(_graph, text, newGroups, newStickies);
        if (pasted.Count == 0)
        {
            Toasts.Info("NodePlay", "Clipboard has no NodePlay payload.");
            return;
        }

        RebuildMirror();
        MarkDirty();
        Toasts.Success("NodePlay", $"Pasted {pasted.Count} node(s).");
    }

    private (List<NPNode> Nodes, List<NPGroup> Groups, List<NPSticky> Stickies) CollectSelection()
    {
        var nodes = new List<NPNode>();
        var groups = new List<NPGroup>();
        var stickies = new List<NPSticky>();

        foreach (string id in _controller.SelectedNodes)
        {
            if (Find(id) is { } n)
                nodes.Add(n);
        }

        foreach (string gid in _controller.SelectedGroups)
        {
            if (_groups.Find(g => g.Id == gid)?.UserData is NPGroup dom)
                groups.Add(dom);
        }

        foreach (string sid in _controller.SelectedStickies)
        {
            if (_stickies.Find(s => s.Id == sid)?.UserData is NPSticky dom)
                stickies.Add(dom);
        }

        return (nodes, groups, stickies);
    }

    private void ExportJson()
    {
        if (_graph == null || string.IsNullOrEmpty(_entryPath))
        {
            Toasts.Warning("NodePlay", "Open a saved .nodeplay asset first.");
            return;
        }

        MirrorFromWidget();
        string abs = JsonSidePath();
        File.WriteAllText(abs, Serializer.Serialize(_graph).WriteToString());
        Toasts.Success("NodePlay", "Exported " + Path.GetFileName(abs));
    }

    private void ImportJson()
    {
        if (_graph == null || string.IsNullOrEmpty(_entryPath))
        {
            Toasts.Warning("NodePlay", "Open a saved .nodeplay asset first.");
            return;
        }

        string abs = JsonSidePath();
        if (!File.Exists(abs))
        {
            Toasts.Warning("NodePlay", "No " + Path.GetFileName(abs) + " beside the asset.");
            return;
        }

        EchoObject echo = EchoObject.ReadFromString(File.ReadAllText(abs));
        Serializer.DeserializeInto(echo, _graph);
        _graph.BuildTopology();
        RebuildMirror();
        MarkDirty();
        Toasts.Success("NodePlay", "Imported JSON.");
    }

    private string JsonSidePath()
    {
        string assets = Projects.Project.Current!.AssetsPath;
        return Path.Combine(assets, _entryPath + ".json");
    }

    // ── live debug visualization ───────────────────────────────────────────────────────────

    private void ApplyLiveStatus()
    {
        NPRunner? runner = FindLiveRunner();
        NPGraphAsset? liveGraph = runner?.RunningGraph;
        for (int i = 0; i < _nodes.Count; i++)
        {
            var widget = _nodes[i];
            if (widget.UserData is not NPNode domain)
                continue;

            NPNode? liveNode = liveGraph?.GetNode(domain.Id);
            Color accent = StatusAccent(liveNode?.Status);
            string suffix = liveNode == null ? "" : liveNode.Status switch
            {
                NPStatus.Running => "  [running]",
                NPStatus.Success => "  [ok]",
                NPStatus.Failure => "  [fail]",
                NPStatus.Optional => "  [opt]",
                _ => "",
            };
            string baseTitle = domain.DisplayName + (domain.Graph != null && ReferenceEquals(domain, domain.Graph.PrimeNode) ? "  >>" : "");
            widget.Title = baseTitle + suffix;
            widget.Accent = accent;
        }
    }

    private static Color StatusAccent(NPStatus? status) => status switch
    {
        NPStatus.Running => Color.FromArgb(0x4F, 0xC3, 0xF7),
        NPStatus.Success => Color.FromArgb(0x66, 0xBB, 0x6A),
        NPStatus.Failure => Color.FromArgb(0xEF, 0x53, 0x50),
        NPStatus.Optional => Color.FromArgb(0xFF, 0xA0, 0x00),
        _ => Color.FromArgb(0xE4, 0xB9, 0x54),
    };

    private static NPRunner? FindLiveRunner()
    {
        try
        {
            IReadOnlyList<object> selected = Selection.Selected;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is GameObject go && go.IsValid()
                    && go.GetComponent(typeof(NPRunner)) is NPRunner fromSelection)
                    return fromSelection;
            }
        }
        catch
        {
            // selection can reference dying objects mid-frame — fall back below
        }

        IReadOnlyList<NPRunner> owners = NPRunner.RunningOwners;
        return owners.Count > 0 ? owners[0] : null;
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────

    private Float2 ViewCenter()
    {
        // Viewport centre in graph space derived from pan/zoom (good enough for drop-in creation).
        float zoom = MathF.Max(_controller.Zoom, 0.05f);
        return new Float2(-_controller.Pan.X / zoom + 240f, -_controller.Pan.Y / zoom + 160f);
    }

    private NPConnection? FindWire(string id)
    {
        for (int i = 0; i < _graph!.Connections.Count; i++)
        {
            if (_graph.Connections[i].Id == id)
                return _graph.Connections[i];
        }

        return null;
    }

    private NPNode? Find(string id)
    {
        for (int i = 0; i < _graph!.Nodes.Count; i++)
        {
            if (_graph.Nodes[i].Id == id)
                return _graph.Nodes[i];
        }

        return null;
    }

    private void MarkDirty() => _dirty = true;

    private void Save()
    {
        if (_graph == null || !_dirty)
            return;

        MirrorFromWidget();

        // Position-only moves never bump StructureVersion but must reach disk.
        _mirroredVersion = -1;
        EditorAssetBackend.Instance?.SaveAsset(_graph);

        _dirty = false;
        Toasts.Success("NodePlay", $"Saved {_graph.GraphName}");
    }
}
