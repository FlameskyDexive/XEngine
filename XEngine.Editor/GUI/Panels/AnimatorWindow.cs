// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.Editor.Utils;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// The Animator Window — a full state-machine editor for an <see cref="AnimatorController"/>. Uses the Origami
/// NodeGraph widget for drag/zoom/pan/connect/context-menu, a left-sidebar Layers|Parameters panel, and a bottom
/// inspector for the selected state or transition.
/// </summary>
public partial class AnimatorWindow : DockPanel, IScriptReloadCleanup
{
    public override string Title => "Animator";
    public override string Icon => EditorIcons.Film;

    private const float ToolbarHeight = AnimationEditorChrome.ToolbarHeight;
    private const float SplitterSize = 4f;
    private const float MinSidebar = 120f;
    private const float MaxSidebar = 420f;
    private const float MinInspector = 80f;
    private const float MaxInspector = 360f;

    private AnimatorController? _controller;
    private bool _subscribed;
    private bool _graphDirty = true;
    private bool _locked;
    private int _sidebarTab;
    private int _selectedLayerIndex;
    private int _selectedParamIndex = -1;
    private string? _selectedStateName;
    private readonly List<string> _selectedStateNames = new();
    private string? _selectedTransitionName;
    private readonly List<string> _selectedTransitionNames = new();
    private bool _pinTransitionInspector;
    private TransitionTimelineLayout.Hit _tlHit;
    private float _tlLastX;
    private string? _activeStateName;
    private string? _editingBlendTreeState;
    private float _blendPreviewParam;
    private float _blendPreviewParamY;
    private readonly float[] _blendWeightScratch = new float[16];
    private float _sidebarWidth = AnimationEditorChrome.SidebarWidth;
    private float _inspectorHeight = AnimationEditorChrome.InspectorHeight;
    private bool _layoutLoaded;
    private readonly List<string> _smPath = new();
    private bool _behaviourPicker;
    private static List<Type>? _behaviourTypes;

    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphConnection> _wires = new();
    private readonly NodeGraphController _ctrl = new();

    private const string EntryNodeId = "__entry__";
    private const string AnyStateNodeId = "__anystate__";
    private const string ExitNodeId = AnimatorController.ExitStateName;
    private const string UpNodeId = "__up__";
    private const string StateMachinePrefix = "__sm__";
    private const string InPort = "in";
    private const string OutPort = "out";

    private static readonly Color StateAccent = AnimationEditorChrome.StateAccent;
    private static readonly Color DefaultAccent = AnimationEditorChrome.DefaultAccent;
    private static readonly Color EntryAccent = AnimationEditorChrome.EntryAccent;
    private static readonly Color AnyStateAccent = AnimationEditorChrome.AnyStateAccent;
    private static readonly Color ExitAccent = AnimationEditorChrome.ExitAccent;
    private static readonly Color StateMachineAccent = AnimationEditorChrome.StateMachineAccent;
    private static readonly Color FlowColor = AnimationEditorChrome.FlowColor;
    private static readonly Color ActiveAccent = AnimationEditorChrome.ActiveAccent;

    public int SelectedLayerIndex => _selectedLayerIndex;
    public int SidebarTab => _sidebarTab;
    public void SelectSidebarTab(int tab) => _sidebarTab = tab;

    public void AddLayerNow()
    {
        if (_controller == null) return;
        AnimatorControllerEditGraph.AddLayer(_controller);
        _graphDirty = true;
    }
    public bool Locked => _locked;
    public NodeGraphController GraphController => _ctrl;
    public string? EditingBlendTreeState => _editingBlendTreeState;
    public IReadOnlyList<string> SelectedStateNames => _selectedStateNames;
    public IReadOnlyList<string> StateMachinePath => _smPath;
    public IReadOnlyList<string> SelectedTransitionNames => _selectedTransitionNames;
    public IReadOnlyList<GraphConnection> Wires => _wires;

    public int CountWires(string from, string to)
    {
        int n = 0;
        for (int i = 0; i < _wires.Count; i++)
        {
            if (_wires[i].FromNode == from && _wires[i].ToNode == to)
                n++;
        }
        return n;
    }

    public IReadOnlyList<string> WireTransitionNames(string from, string to)
    {
        var names = new List<string>();
        for (int i = 0; i < _wires.Count; i++)
        {
            GraphConnection w = _wires[i];
            if (w.FromNode != from || w.ToNode != to) continue;
            CollectTransitionNames(w.UserData, names);
        }
        return names;
    }

    public void RebuildGraphNow()
    {
        _graphDirty = true;
        RebuildGraphIfNeeded();
    }

    /// <summary>Selects a named transition so the inspector timeline is shown (tests / live capture).</summary>
    public bool SelectTransitionByName(string name)
    {
        if (FindTransition(name) == null) return false;
        _selectedTransitionNames.Clear();
        _selectedTransitionNames.Add(name);
        _selectedTransitionName = name;
        _selectedStateNames.Clear();
        _selectedStateName = null;
        _pinTransitionInspector = true;
        return true;
    }

    public bool MoveTransition(string stateName, string transitionName, int delta)
    {
        if (_controller == null) return false;
        bool ok = AnimatorControllerEditGraph.MoveTransition(
            _controller, _selectedLayerIndex, _smPath, stateName, transitionName, delta);
        if (ok) _graphDirty = true;
        return ok;
    }
    public float SidebarWidth
    {
        get => _sidebarWidth;
        set => _sidebarWidth = ClampSidebar(value);
    }
    public float InspectorHeight
    {
        get => _inspectorHeight;
        set => _inspectorHeight = ClampInspector(value);
    }

    public override bool SerializeState(JsonObject state)
    {
        state["sidebarWidth"] = _sidebarWidth;
        state["inspectorHeight"] = _inspectorHeight;
        return true;
    }

    public override void RestoreState(JsonObject state)
    {
        if (state.TryGetPropertyValue("sidebarWidth", out JsonNode? sw) && sw is JsonValue sv && sv.TryGetValue(out float sidebar))
            _sidebarWidth = ClampSidebar(sidebar);
        if (state.TryGetPropertyValue("inspectorHeight", out JsonNode? ih) && ih is JsonValue iv && iv.TryGetValue(out float inspector))
            _inspectorHeight = ClampInspector(inspector);
        _layoutLoaded = true;
    }

    public void OnScriptReloadCleanup()
    {
        _behaviourTypes = null;
        _behaviourPicker = false;
        DisposePaintRegions();
        PublishContinuousPresent(false, ref _countedContinuous);
        SetController(null);
    }

    public override void OnClosed()
    {
        DisposePaintRegions();
        PublishContinuousPresent(false, ref _countedContinuous);
        if (_subscribed) { Selection.OnSelectionChanged -= TryLoadFromSelection; _subscribed = false; }
        if (ReferenceEquals(ActiveInstance, this)) ActiveInstance = null;
    }

    /// <summary>Live instance for cross-panel hooks (shared Inspector renders graph selection).</summary>
    public static AnimatorWindow? ActiveInstance { get; private set; }

    /// <summary>
    /// Whether the shared Inspector should render THIS window's graph selection (a state,
    /// transition, or multi-selection) instead of the global selection: true when nothing else
    /// is selected, or when the current selection is still this window's own .controller asset —
    /// i.e. the user browsed here through the project grid and is now inspecting nodes.
    /// </summary>
    public bool OwnsInspectorContext(object? active)
    {
        if (_controller == null) return active == null;
        if (active == null) return true;
        if (active is ContentItem ci)
        {
            if (_controller.AssetID != Guid.Empty && ci.Guid == _controller.AssetID) return true;
            if (!string.IsNullOrEmpty(ci.RelativePath) && ci.RelativePath == _controller.AssetPath) return true;
        }
        return false;
    }

    /// <summary>
    /// Draws the current graph-selection inspector Unity-style (state properties / transition /
    /// multi-selection). Returns false when there is no live selection to draw.
    /// </summary>
    public bool TryDrawSelectionInspector(Paper paper, float w)
    {
        if (_controller == null) return false;
        Undo.Snapshot(_controller);

        if (_selectedStateNames.Count > 1 && _selectedTransitionName == null)
        {
            Origami.Label(paper, "anim_ins_multi", $"{_selectedStateNames.Count} states selected").Show();
            return true;
        }
        if (_selectedTransitionName != null)
        {
            if (_selectedTransitionNames.Count > 1)
                DrawTransitionGroup(paper, w);
            var t = FindTransition(_selectedTransitionName);
            if (t != null) { DrawTransitionInspector(paper, w, t); return true; }
        }
        if (_selectedStateName != null)
        {
            var s = FindStateByName(_selectedStateName);
            if (s != null) { DrawStateInspector(paper, w, s); return true; }
        }
        return false;
    }

    public void SetController(AnimatorController? controller)
    {
        _controller = controller;
        _selectedLayerIndex = 0;
        _selectedParamIndex = -1;
        _selectedStateName = null;
        _selectedStateNames.Clear();
        _selectedTransitionName = null;
        _selectedTransitionNames.Clear();
        _editingBlendTreeState = null;
        _smPath.Clear();
        _graphDirty = true;
        BumpContent();
        BumpVisual();
    }

    public AnimatorController? Controller => _controller;

    public void SelectLayer(int layer)
    {
        if (_controller == null) return;
        int next = AnimatorControllerEditGraph.ClampLayer(_controller, layer);
        if (next == _selectedLayerIndex) return;
        _selectedLayerIndex = next;
        _selectedStateName = null;
        _selectedStateNames.Clear();
        _selectedTransitionName = null;
        _selectedTransitionNames.Clear();
        _editingBlendTreeState = null;
        _smPath.Clear();
        _graphDirty = true;
    }

    public bool DrillInto(string stateMachineName)
    {
        if (_controller == null || string.IsNullOrEmpty(stateMachineName)) return false;
        var parent = AnimatorControllerEditGraph.Machine(_controller, _selectedLayerIndex, _smPath);
        if (AnimatorControllerEditGraph.FindChild(parent, stateMachineName) == null)
            return false;
        _smPath.Add(stateMachineName);
        _graphDirty = true;
        return true;
    }

    public bool NavigateUp()
    {
        if (_smPath.Count == 0) return false;
        _smPath.RemoveAt(_smPath.Count - 1);
        _graphDirty = true;
        return true;
    }

    public void NavigateToPath(int crumbIndex)
    {
        if (crumbIndex <= 0)
            _smPath.Clear();
        else if (crumbIndex < _smPath.Count)
            _smPath.RemoveRange(crumbIndex, _smPath.Count - crumbIndex);
        _graphDirty = true;
    }

    public AnimatorController.StateMachine? CreateStateMachineAt(Float2 graphPos)
        => CreateStateMachineAt(graphPos.X, graphPos.Y, "New StateMachine");

    public AnimatorController.StateMachine? CreateStateMachineAt(float x, float y, string? name = null)
    {
        if (_controller == null) return null;
        Undo.Snapshot(_controller);
        var sm = AnimatorControllerEditGraph.CreateStateMachine(
            _controller, _selectedLayerIndex, _smPath, name ?? "New StateMachine", x, y);
        _graphDirty = true;
        return sm;
    }

    public bool MoveStateInto(string stateName, string targetMachine)
    {
        if (_controller == null) return false;
        Undo.Snapshot(_controller);
        bool ok = AnimatorControllerEditGraph.MoveStateInto(
            _controller, _selectedLayerIndex, _smPath, stateName, targetMachine);
        if (ok) _graphDirty = true;
        return ok;
    }

    public void OpenBlendTreeEditor(string stateName)
    {
        if (_controller == null) return;
        var state = FindStateByName(stateName);
        if (state == null || !BlendTreeEditorModel.HasTree(state)) return;
        _editingBlendTreeState = stateName;
        _selectedStateName = stateName;
    }

    public void CloseBlendTreeEditor() => _editingBlendTreeState = null;

    public void CreateBlendTreeState(Float2 graphPos) => CreateStateAt(graphPos, blendTree: true);

    [MenuItem("Window/Animation/Animator", priority: 20)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(AnimatorWindow));

    private void TryLoadFromSelection()
    {
        if (_locked) return;
        if (Selection.ActiveObject is not ContentItem ci || ci.IsFolder || ci.IsSubAsset) return;
        if (!ci.RelativePath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase)) return;

        Guid guid = ci.Guid;
        if (guid == Guid.Empty)
        {
            var entry = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath);
            guid = entry?.Guid ?? Guid.Empty;
        }
        if (guid == Guid.Empty) return;

        var controller = AssetDatabase.Get(guid) as AnimatorController;
        if (controller != null && controller != _controller)
            SetController(controller);
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        using var profilerScope = EditorProfiler.BeginCpuScope("Animator");
        ActiveInstance = this;
        if (!_subscribed)
        {
            Selection.OnSelectionChanged += TryLoadFromSelection;
            _subscribed = true;
            TryLoadFromSelection();
        }

        RebuildGraphIfNeeded();
        UpdateActiveStateHighlight();
        EnsureLayoutLoaded();
        PublishContinuousPresent(IsPreviewPlaying, ref _countedContinuous);

        using (paper.Column("anim_root").Size(width, height).Enter())
        {
            DrawToolbar(paper, width);

            float inspectorH = Math.Min(_inspectorHeight, Math.Max(MinInspector, height - ToolbarHeight - 80f));
            float bodyHeight = height - ToolbarHeight - inspectorH - SplitterSize;
            if (bodyHeight < 80f) bodyHeight = 80f;
            float sidebar = Math.Min(_sidebarWidth, Math.Max(MinSidebar, width - 180f));

            using (paper.Row("anim_body").Width(width).Height(bodyHeight).Enter())
            {
                DrawSidebar(paper, sidebar, bodyHeight);
                DrawVSplitter(paper, bodyHeight);
                DrawCanvas(paper, width - sidebar - SplitterSize, bodyHeight);
            }

            DrawHSplitter(paper, width);
            DrawInspector(paper, width, inspectorH);
        }
    }

    private void EnsureLayoutLoaded()
    {
        if (_layoutLoaded) return;
        _layoutLoaded = true;
        var settings = EditorSettings.Instance;
        _sidebarWidth = ClampSidebar(settings.AnimatorSidebarWidth);
        _inspectorHeight = ClampInspector(settings.AnimatorInspectorHeight);
    }

    private static float ClampSidebar(float v) => Math.Clamp(v, MinSidebar, MaxSidebar);
    private static float ClampInspector(float v) => Math.Clamp(v, MinInspector, MaxInspector);

    private void DrawVSplitter(Paper paper, float h)
    {
        paper.Box("anim_vsplit").Width(SplitterSize).Height(h)
            .BackgroundColor(AnimationEditorChrome.Splitter)
            .Cursor(PaperCursor.ResizeHorizontal)
            .OnDragging(e => _sidebarWidth = ClampSidebar(_sidebarWidth + (float)e.Delta.X))
            .OnDragEnd(_ => PersistLayout());
    }

    private void DrawHSplitter(Paper paper, float w)
    {
        paper.Box("anim_hsplit").Width(w).Height(SplitterSize)
            .BackgroundColor(AnimationEditorChrome.Splitter)
            .Cursor(PaperCursor.ResizeVertical)
            .OnDragging(e => _inspectorHeight = ClampInspector(_inspectorHeight - (float)e.Delta.Y))
            .OnDragEnd(_ => PersistLayout());
    }

    private void PersistLayout()
    {
        var settings = EditorSettings.Instance;
        settings.AnimatorSidebarWidth = _sidebarWidth;
        settings.AnimatorInspectorHeight = _inspectorHeight;
        settings.Save();
    }

    private void DrawToolbar(Paper paper, float width)
    {
        using (paper.Row("anim_tb").Height(ToolbarHeight)
            .BackgroundColor(AnimationEditorChrome.Panel)
            .Padding(6, 2, 2, 0).ColBetween(4).Enter())
        {
            Origami.Label(paper, "anim_tb_name", _controller?.Name ?? "(no controller)").Show();
            if (_controller != null)
            {
                var crumbs = new List<BreadcrumbItem>
                {
                    new(AnimatorControllerEditGraph.LayerName(_controller, _selectedLayerIndex), "", 0)
                };
                for (int i = 0; i < _smPath.Count; i++)
                    crumbs.Add(new BreadcrumbItem(_smPath[i], "", i + 1));
                Origami.Breadcrumb(paper, "anim_tb_crumb", crumbs, it =>
                {
                    if (it.UserData is int idx)
                        NavigateToPath(idx);
                }).Chevrons().Show();
                if (_editingBlendTreeState != null)
                {
                    Origami.Button(paper, "anim_tb_btback", $"◀ {_editingBlendTreeState}",
                        CloseBlendTreeEditor).Show();
                }
            }
            paper.Box("anim_tb_spring").Width(width).Height(1);
            Origami.Toggle(paper, "anim_tb_live", _autoLiveLink, v => _autoLiveLink = v)
                .LabelRight("Auto Live Link").Show();
            Origami.Button(paper, "anim_tb_lock", _locked ? "Unlock" : "Lock",
                () => _locked = !_locked).Show();
            Origami.Button(paper, "anim_tb_frame", "Frame All", () => _ctrl.FrameAll()).Show();
            Origami.Button(paper, "anim_tb_framesel", "Frame", () => FrameSelected()).Show();
            if (_controller != null)
                Origami.Button(paper, "anim_tb_save", "Save", SaveController).Show();
        }
    }

    private void SaveController()
    {
        if (_controller == null) return;
        try
        {
            var echo = XEngine.Echo.Serializer.Serialize(_controller, XEngine.Echo.TypeMode.Aggressive);
            string? path = _controller.AssetPath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                echo.WriteToString(new System.IO.FileInfo(path));
                Runtime.Debug.Log($"[Animator] Saved controller to {path}");
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[Animator] Save failed: {ex.Message}");
        }
    }

    private void DrawSidebar(Paper paper, float w, float h)
    {
        using (paper.Column("anim_sidebar").Width(w).Height(h)
            .BackgroundColor(AnimationEditorChrome.Raised)
            .Padding(4, 2, 2, 2).Enter())
        {
            Origami.Tabs(paper, "anim_side_tabs", _sidebarTab, v => _sidebarTab = v)
                .Underline()
                .Height(22)
                .Tab("Layers")
                .Tab("Parameters")
                .Show();

            using (paper.Column("anim_side_paint").Width(w).Height(h - 26).Enter())
            {
                AttachSidebarPaintRegion(paper);
                if (_controller == null)
                {
                    Origami.Label(paper, "anim_side_empty", "Open a .controller").Show();
                    return;
                }

                if (_sidebarTab == 0)
                    DrawLayersTab(paper, w, h - 26);
                else
                    DrawParametersTab(paper, w, h - 26);
            }
        }
    }

    private void DrawLayersTab(Paper paper, float w, float h)
    {
        if (_controller == null) return;
        float listH = h - 32f;
        Origami.ScrollView(paper, "anim_layers_scroll", w, listH).Padding(2).Body(() =>
        {
            int n = AnimatorControllerEditGraph.EffectiveLayerCount(_controller);
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                bool selected = idx == _selectedLayerIndex;
                using (paper.Column($"anim_ly{idx}").Width(w)
                    .BackgroundColor(selected ? AnimationEditorChrome.Selection : AnimationEditorChrome.Raised)
                    .Padding(4, 2, 2, 2).Enter())
                {
                    using (paper.Row($"anim_lyh{idx}").Width(w).Height(20)
                        .OnClick(0, (_, _) => SelectLayer(idx)).Enter())
                    {
                        Origami.TextField(paper, $"anim_lyn{idx}",
                            AnimatorControllerEditGraph.LayerName(_controller, idx), v =>
                            {
                                AnimatorControllerEditGraph.MutableLayer(_controller, idx).Name = v;
                            }).Show();
                    }

                    float weight = 1f;
                    AnimatorLayerBlendingMode blending = AnimatorLayerBlendingMode.Override;
                    AssetRef<AvatarMask> mask = default;
                    int synced = -1;
                    bool syncTiming = false;
                    bool ikPass = false;
                    if (_controller.Layers.Count > 0)
                    {
                        var authored = _controller.Layers[AnimatorControllerEditGraph.ClampLayer(_controller, idx)];
                        weight = authored.Weight;
                        blending = authored.Blending;
                        mask = authored.Mask;
                        synced = authored.SyncedLayerIndex;
                        syncTiming = authored.SyncedLayerAffectsTiming;
                        ikPass = authored.IKPass;
                    }

                    Origami.Foldout(paper, $"anim_lyf{idx}", selected ? "▾" : "▸")
                        .DefaultExpanded(selected)
                        .Body(() =>
                        {
                            if (idx == 0)
                            {
                                using (paper.Row($"anim_lyik{idx}").Width(w).Height(20).Enter())
                                {
                                    Origami.Toggle(paper, $"anim_lyikv{idx}", ikPass, v =>
                                        AnimatorControllerEditGraph.MutableLayer(_controller, idx).IKPass = v)
                                        .LabelRight("IK Pass").Show();
                                }
                                return;
                            }

                            using (paper.Row($"anim_lyw{idx}").Width(w).Height(20).ColBetween(4).Enter())
                            {
                                Origami.Label(paper, $"anim_lywl{idx}", "W").Show();
                                Origami.NumericField(paper, $"anim_lywv{idx}", weight, v =>
                                    AnimatorControllerEditGraph.MutableLayer(_controller, idx).Weight = v).Show();
                            }
                            using (paper.Row($"anim_lyb{idx}").Width(w).Height(22).Enter())
                            {
                                Origami.EnumDropdown(paper, $"anim_lybm{idx}", blending, v =>
                                    AnimatorControllerEditGraph.MutableLayer(_controller, idx).Blending = v).Show();
                            }
                            PropertyGridUtils.DrawField(paper, $"anim_lym{idx}", "Mask",
                                typeof(AssetRef<AvatarMask>), mask,
                                v => AnimatorControllerEditGraph.MutableLayer(_controller, idx).Mask =
                                    v is AssetRef<AvatarMask> m ? m : default);
                            using (paper.Row($"anim_lys{idx}").Width(w).Height(20).ColBetween(4).Enter())
                            {
                                Origami.Label(paper, $"anim_lysl{idx}", "Sync").Show();
                                Origami.NumericField(paper, $"anim_lysv{idx}", synced, v =>
                                    AnimatorControllerEditGraph.MutableLayer(_controller, idx).SyncedLayerIndex = (int)v).Show();
                            }
                            using (paper.Row($"anim_lyt{idx}").Width(w).Height(20).Enter())
                            {
                                Origami.Toggle(paper, $"anim_lytv{idx}", syncTiming, v =>
                                    AnimatorControllerEditGraph.MutableLayer(_controller, idx).SyncedLayerAffectsTiming = v)
                                    .LabelRight("Timing").Show();
                            }
                            using (paper.Row($"anim_lywp{idx}").Width(w).Height(20).Enter())
                            {
                                Origami.Label(paper, $"anim_lywpl{idx}", "Weight Param").Show();
                                Origami.TextField(paper, $"anim_lywpv{idx}",
                                    AnimatorControllerEditGraph.MutableLayer(_controller, idx).WeightParameterName ?? "",
                                    v => AnimatorControllerEditGraph.MutableLayer(_controller, idx).WeightParameterName = v ?? "")
                                    .Show();
                            }
                            using (paper.Row($"anim_lyik{idx}").Width(w).Height(20).Enter())
                            {
                                Origami.Toggle(paper, $"anim_lyikv{idx}", ikPass, v =>
                                    AnimatorControllerEditGraph.MutableLayer(_controller, idx).IKPass = v)
                                    .LabelRight("IK Pass").Show();
                            }
                        });
                }
            }
        });

        using (paper.Row("anim_layer_btns").Width(w).Height(28).Padding(4, 2, 2, 4).ColBetween(4).Enter())
        {
            Origami.Button(paper, "anim_ladd", "+ Add", () =>
            {
                Undo.Snapshot(_controller);
                AnimatorControllerEditGraph.AddLayer(_controller);
                SelectLayer(AnimatorControllerEditGraph.EffectiveLayerCount(_controller) - 1);
            }).Show();

            using (Origami.BeginReadOnlyScope(_selectedLayerIndex <= 0))
            {
                Origami.Button(paper, "anim_ldel", "− Remove", () =>
                {
                    Undo.Snapshot(_controller);
                    if (AnimatorControllerEditGraph.RemoveLayer(_controller, _selectedLayerIndex))
                        SelectLayer(_selectedLayerIndex - 1);
                }).Show();
            }
        }
    }

    private void DrawParametersTab(Paper paper, float w, float h)
    {
        if (_controller == null) return;
        Animator? live = FindLiveAnimator();
        bool playing = Application.IsPlaying && live != null;

        float listH = h - 32f;
        Origami.ScrollView(paper, "anim_params_scroll", w, listH).Padding(2).Body(() =>
        {
            var parms = _controller.Parameters;
            for (int i = 0; i < parms.Count; i++)
            {
                int idx = i;
                var parm = parms[idx];

                using (paper.Row($"anim_p{idx}").Width(w).Height(26).Padding(4, 2, 2, 2)
                    .OnClick(0, (_, _) => _selectedParamIndex = idx).Enter())
                {
                    Origami.TextField(paper, $"anim_pn{idx}", parm.Name, v => parm.Name = v).Show();
                }

                using (paper.Row($"anim_pt{idx}").Width(w).Height(24).Padding(4, 0, 2, 2).Enter())
                {
                    Origami.EnumDropdown(paper, $"anim_ptype{idx}", parm.Type, v => parm.Type = v).Show();
                    DrawParameterValue(paper, idx, parm, live, playing);
                }
                Origami.Separator(paper, $"anim_psep{idx}").Show();
            }
        });

        using (paper.Row("anim_param_btns").Width(w).Height(28).Padding(4, 2, 2, 4).ColBetween(4).Enter())
        {
            Origami.Button(paper, "anim_padd", "+ Add", () =>
            {
                Undo.Snapshot(_controller);
                _controller.Parameters.Add(new AnimatorController.Parameter
                {
                    Name = $"Param{_controller.Parameters.Count + 1}",
                    Type = AnimatorParameterType.Float
                });
                _selectedParamIndex = _controller.Parameters.Count - 1;
            }).Show();

            using (Origami.BeginReadOnlyScope(_selectedParamIndex < 0))
            {
                Origami.Button(paper, "anim_pdel", "− Remove", () => ConfirmDeleteParameter()).Show();
            }
        }
    }

    private static void DrawParameterValue(Paper paper, int idx, AnimatorController.Parameter parm,
        Animator? live, bool playing)
    {
        string name = parm.Name;
        switch (parm.Type)
        {
            case AnimatorParameterType.Float:
                if (playing && live != null)
                    Origami.NumericField(paper, $"anim_pd{idx}", live.GetFloat(name), v => live.SetFloat(name, v)).Show();
                else
                    Origami.NumericField(paper, $"anim_pd{idx}", parm.DefaultFloat, v => parm.DefaultFloat = v).Show();
                break;
            case AnimatorParameterType.Int:
                if (playing && live != null)
                    Origami.NumericField(paper, $"anim_pd{idx}", live.GetInteger(name), v => live.SetInteger(name, v)).Show();
                else
                    Origami.NumericField(paper, $"anim_pd{idx}", parm.DefaultInt, v => parm.DefaultInt = v).Show();
                break;
            case AnimatorParameterType.Trigger:
                if (playing && live != null)
                    Origami.Button(paper, $"anim_pd{idx}", "Set", () => live.SetTrigger(name)).Show();
                else
                    Origami.Toggle(paper, $"anim_pd{idx}", parm.DefaultBool, v => parm.DefaultBool = v).Show();
                break;
            default:
                if (playing && live != null)
                    Origami.Toggle(paper, $"anim_pd{idx}", live.GetBool(name), v => live.SetBool(name, v)).Show();
                else
                    Origami.Toggle(paper, $"anim_pd{idx}", parm.DefaultBool, v => parm.DefaultBool = v).Show();
                break;
        }
    }

    private void DrawCanvas(Paper paper, float w, float h)
    {
        using (paper.Column("anim_canvas_paint").Width(w).Height(h).Enter())
        {
            AttachCanvasPaintRegion(paper);
            DrawCanvasBody(paper, w, h);
        }
    }

    private void DrawCanvasBody(Paper paper, float w, float h)
    {
        if (_controller == null)
        {
            paper.Box("anim_canvas_empty").Width(w).Height(h)
                .BackgroundColor(AnimationEditorChrome.Canvas)
                .Text(AnimationEditorChrome.EmptyAnimator, EditorTheme.DefaultFont!)
                .TextColor(AnimationEditorChrome.TextMuted)
                .Alignment(XEngine.PaperUI.TextAlignment.MiddleCenter);
            return;
        }

        if (_editingBlendTreeState != null)
        {
            DrawBlendTreeCanvas(paper, w, h);
            return;
        }

        Origami.NodeGraph(paper, "anim_graph", w, h)
            .Nodes(_nodes)
            .Connections(_wires)
            .Controller(_ctrl)
            .OnExternalDrop(HandleExternalDrop)
            .Grid(true)
            .InitialView(new Float2(20, 20), 0.8f)
            .OnSelectionChanged(sel =>
            {
                _selectedStateNames.Clear();
                for (int i = 0; i < sel.Nodes.Count; i++)
                {
                    string id = sel.Nodes[i].Id;
                    if (!IsSpecialNode(id))
                        _selectedStateNames.Add(id);
                }
                _selectedStateName = _selectedStateNames.Count > 0 ? _selectedStateNames[0] : null;
                TryCompleteMakeTransition();
                if (sel.Edges.Count == 1)
                {
                    _pinTransitionInspector = false;
                    _selectedTransitionNames.Clear();
                    CollectTransitionNames(sel.Edges[0].UserData, _selectedTransitionNames);
                    _selectedTransitionName = _selectedTransitionNames.Count > 0 ? _selectedTransitionNames[0] : null;
                }
                else if (sel.Nodes.Count > 0 || !_pinTransitionInspector)
                {
                    _pinTransitionInspector = false;
                    _selectedTransitionNames.Clear();
                    _selectedTransitionName = null;
                }
                else
                {
                    _pinTransitionInspector = false;
                }
                BumpVisual();
            })
            .OnNodesMoved((nodes, delta) =>
            {
                if (_controller == null) return;
                Undo.Snapshot(_controller);
                var childMachines = AnimatorControllerEditGraph.ChildMachines(_controller, _selectedLayerIndex, _smPath);
                for (int i = 0; i < nodes.Count; i++)
                {
                    GraphNode n = nodes[i];
                    if (TrySmName(n.Id, out string smName))
                    {
                        for (int c = 0; c < childMachines.Count; c++)
                        {
                            if (childMachines[c].Name == smName)
                            {
                                childMachines[c].PosX = n.Position.X;
                                childMachines[c].PosY = n.Position.Y;
                                break;
                            }
                        }
                        continue;
                    }
                    var state = FindStateByName(n.Id);
                    if (state != null) { state.PosX = n.Position.X; state.PosY = n.Position.Y; }
                }
                for (int i = 0; i < nodes.Count; i++)
                {
                    GraphNode n = nodes[i];
                    if (IsSpecialNode(n.Id)) continue;
                    Float2 center = n.Position + new Float2(n.Width * 0.5f, 18f);
                    for (int j = 0; j < _nodes.Count; j++)
                    {
                        GraphNode smNode = _nodes[j];
                        if (!TrySmName(smNode.Id, out string target)) continue;
                        if (center.X >= smNode.Position.X && center.X <= smNode.Position.X + smNode.Width
                            && center.Y >= smNode.Position.Y && center.Y <= smNode.Position.Y + 40f)
                        {
                            MoveStateInto(n.Id, target);
                            break;
                        }
                    }
                }
            })
            .OnConnect(req => CreateTransition(req.FromNode, req.ToNode))
            .OnValidateConnection(req => CanConnect(req.FromNode, req.ToNode))
            .OnDeleteSelection(sel =>
            {
                for (int i = 0; i < sel.Edges.Count; i++)
                {
                    var names = new List<string>();
                    CollectTransitionNames(sel.Edges[i].UserData, names);
                    for (int n = 0; n < names.Count; n++)
                        DeleteTransition(names[n]);
                }
                for (int i = 0; i < sel.Nodes.Count; i++)
                {
                    string id = sel.Nodes[i].Id;
                    if (TrySmName(id, out string smName))
                    {
                        ConfirmDelete($"Delete state machine '{smName}'?", () => DeleteStateMachineNamed(smName));
                        continue;
                    }
                    if (!IsSpecialNode(id))
                        ConfirmDelete($"Delete state '{id}'?", () => DeleteState(id));
                }
            })
            .OnBackgroundContext(gp => OpenBackgroundMenu(gp))
            .OnNodeContext((node, _) => OpenNodeMenu(node))
            .OnCopy(_ => CopySelection())
            .OnPaste(() => PasteClipboard())
            .OnDuplicate(_ => DuplicateSelection())
            .OnNodeDoubleClick(node =>
            {
                if (node.Id == UpNodeId)
                {
                    NavigateUp();
                    return;
                }
                if (TrySmName(node.Id, out string smName))
                {
                    DrillInto(smName);
                    return;
                }
                if (!IsSpecialNode(node.Id))
                    OpenBlendTreeEditor(node.Id);
            })
            .Show();
    }

    private static bool CanConnect(string from, string to)
    {
        if (from == ExitNodeId || from == UpNodeId || to == UpNodeId) return false;
        if (to == EntryNodeId || to == AnyStateNodeId) return false;
        if (!IsSpecialNode(from) && from == to) return false;
        return true;
    }

    private void OpenBackgroundMenu(Float2 graphPos)
    {
        Origami.ContextMenuAtPointer(builder =>
        {
            builder.Item("Create State", () => CreateStateAt(graphPos));
            builder.Item("Create Empty", () => CreateStateAt(graphPos));
            builder.Item("Create From Selected Clip", () => CreateStateFromSelectedClip(graphPos));
            builder.Item("Create From New Blend Tree", () => CreateStateAt(graphPos, blendTree: true));
            builder.Item("Create Sub-State Machine", () => CreateStateMachineAt(graphPos));
            builder.Separator();
            builder.Item("Copy", () => CopySelection());
            if (AnimatorGraphClipboard.TryReadClipboard(out _))
                builder.Item("Paste", () => PasteClipboard());
        });
    }

    private void OpenNodeMenu(GraphNode node)
    {
        if (node.Id == UpNodeId)
        {
            Origami.ContextMenuAtPointer(builder =>
            {
                builder.Item("Go Up", () => NavigateUp());
            });
            return;
        }
        if (TrySmName(node.Id, out string smName))
        {
            Origami.ContextMenuAtPointer(builder =>
            {
                builder.Item("Open State Machine", () => DrillInto(smName));
                builder.Separator();
                builder.Item("Copy", () => CopySelection());
                builder.Item("Delete", () => ConfirmDelete($"Delete state machine '{smName}'?",
                    () => DeleteStateMachineNamed(smName)), danger: true);
            });
            return;
        }
        if (IsSpecialNode(node.Id)) return;
        string name = node.Id;
        var state = FindStateByName(name);
        Origami.ContextMenuAtPointer(builder =>
        {
            builder.Item("Make Transition", () => BeginMakeTransition(name));
            builder.Item("Set as Default", () =>
            {
                if (_controller == null) return;
                Undo.Snapshot(_controller);
                AnimatorControllerEditGraph.SetDefaultStateName(_controller, _selectedLayerIndex, name, _smPath);
                _graphDirty = true;
            });
            if (state != null && BlendTreeEditorModel.HasTree(state))
                builder.Item("Edit Blend Tree", () => OpenBlendTreeEditor(name));
            else
                builder.Item("Create new BlendTree in State", () => CreateBlendTreeInState(name));
            builder.Separator();
            builder.Item("Copy", () => CopySelection());
            builder.Item("Duplicate", () => DuplicateSelection());
            builder.Separator();
            builder.Item("Delete", () => ConfirmDelete($"Delete state '{name}'?", () => DeleteState(name)), danger: true);
        });
    }

    private void CreateStateAt(Float2 graphPos, bool blendTree = false)
    {
        if (_controller == null) return;
        Undo.Snapshot(_controller);
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        string name = AnimatorControllerEditGraph.UniqueStateName(_controller, _selectedLayerIndex,
            blendTree ? "Blend Tree" : "New State", _smPath);
        var state = new AnimatorController.State
        {
            Name = name, Speed = 1f, IsLooping = true, PosX = graphPos.X, PosY = graphPos.Y
        };
        if (blendTree)
        {
            string param = _controller.Parameters.Count > 0 ? _controller.Parameters[0].Name : string.Empty;
            BlendTreeEditorModel.AttachInline(state, BlendTreeEditorModel.CreateInline(name, param));
        }
        states.Add(state);
        if (string.IsNullOrEmpty(AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath)))
            AnimatorControllerEditGraph.SetDefaultStateName(_controller, _selectedLayerIndex, name, _smPath);
        _graphDirty = true;
    }

    private void DeleteState(string name)
    {
        if (_controller == null) return;
        Undo.Snapshot(_controller);
        var state = FindStateByName(name);
        if (state == null) return;
        var transitions = AnimatorControllerEditGraph.Transitions(_controller, _selectedLayerIndex, _smPath);
        var any = AnimatorControllerEditGraph.AnyStateTransitions(_controller, _selectedLayerIndex, _smPath);
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);

        for (int i = transitions.Count - 1; i >= 0; i--)
        {
            var t = transitions[i];
            if (t.DestinationStateName == name || state.TransitionNames.Contains(t.Name))
                transitions.RemoveAt(i);
        }
        for (int i = any.Count - 1; i >= 0; i--)
        {
            if (any[i].DestinationStateName == name)
                any.RemoveAt(i);
        }
        for (int si = 0; si < states.Count; si++)
        {
            var names = states[si].TransitionNames;
            for (int ti = names.Count - 1; ti >= 0; ti--)
            {
                bool found = false;
                for (int k = 0; k < transitions.Count; k++)
                {
                    if (transitions[k].Name == names[ti]) { found = true; break; }
                }
                if (!found) names.RemoveAt(ti);
            }
        }
        states.Remove(state);
        if (AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath) == name)
            AnimatorControllerEditGraph.SetDefaultStateName(_controller, _selectedLayerIndex,
                states.Count > 0 ? states[0].Name : string.Empty, _smPath);
        _graphDirty = true;
    }

    private void CreateTransition(string fromNodeId, string toNodeId)
    {
        if (_controller == null) return;
        if (toNodeId == EntryNodeId || toNodeId == AnyStateNodeId || toNodeId == UpNodeId) return;
        Undo.Snapshot(_controller);

        string dest = TrySmName(toNodeId, out string toSm) ? toSm : toNodeId;
        if (fromNodeId == EntryNodeId)
        {
            if (IsSpecialNode(toNodeId) && !TrySmName(toNodeId, out _)) return;
            AnimatorControllerEditGraph.SetDefaultStateName(_controller, _selectedLayerIndex, dest, _smPath);
            _graphDirty = true;
            return;
        }
        if (fromNodeId == AnyStateNodeId)
        {
            if (IsSpecialNode(toNodeId) && toNodeId != ExitNodeId && !TrySmName(toNodeId, out _)) return;
            AnimatorControllerEditGraph.AnyStateTransitions(_controller, _selectedLayerIndex, _smPath).Add(
                new AnimatorController.Transition
                {
                    Name = AnimatorControllerEditGraph.UniqueTransitionName(_controller, _selectedLayerIndex, "Any", dest, _smPath),
                    DestinationStateName = dest, Duration = 0.25f, HasFixedDuration = true, OrderedInterruption = true
                });
            _graphDirty = true;
            return;
        }

        if (TrySmName(fromNodeId, out string fromSm))
        {
            var smT = new AnimatorController.Transition
            {
                Name = AnimatorControllerEditGraph.UniqueTransitionName(_controller, _selectedLayerIndex, fromSm, dest, _smPath),
                DestinationStateName = dest,
                SourceStateMachineName = fromSm,
                Duration = 0.25f, HasFixedDuration = true, OrderedInterruption = true
            };
            AnimatorControllerEditGraph.Machine(_controller, _selectedLayerIndex, _smPath)
                .StateMachineTransitions.Add(smT);
            _graphDirty = true;
            return;
        }

        var source = FindStateByName(fromNodeId);
        if (source == null) return;
        var t = new AnimatorController.Transition
        {
            Name = AnimatorControllerEditGraph.UniqueTransitionName(_controller, _selectedLayerIndex, fromNodeId, dest, _smPath),
            DestinationStateName = dest, Duration = 0.25f, HasFixedDuration = true, OrderedInterruption = true
        };
        AnimatorControllerEditGraph.Transitions(_controller, _selectedLayerIndex, _smPath).Add(t);
        source.TransitionNames.Add(t.Name);
        _graphDirty = true;
    }

    private void DeleteTransition(string transitionName)
    {
        if (_controller == null) return;
        Undo.Snapshot(_controller);
        var transitions = AnimatorControllerEditGraph.Transitions(_controller, _selectedLayerIndex, _smPath);
        var any = AnimatorControllerEditGraph.AnyStateTransitions(_controller, _selectedLayerIndex, _smPath);
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        var machine = AnimatorControllerEditGraph.Machine(_controller, _selectedLayerIndex, _smPath);
        for (int i = transitions.Count - 1; i >= 0; i--)
        {
            if (transitions[i].Name == transitionName) transitions.RemoveAt(i);
        }
        for (int i = any.Count - 1; i >= 0; i--)
        {
            if (any[i].Name == transitionName) any.RemoveAt(i);
        }
        var entries = machine.EntryTransitions;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i].Name == transitionName) entries.RemoveAt(i);
        }
        var smOut = machine.StateMachineTransitions;
        for (int i = smOut.Count - 1; i >= 0; i--)
        {
            if (smOut[i].Name == transitionName) smOut.RemoveAt(i);
        }
        for (int i = 0; i < states.Count; i++)
            states[i].TransitionNames.Remove(transitionName);
        _graphDirty = true;
    }

    private Animator? FindLiveAnimator() => ResolveLiveAnimator();

    private void UpdateActiveStateHighlight()
    {
        string? previous = _activeStateName;
        ApplyLiveLink();
        if (!string.Equals(previous, _activeStateName, StringComparison.Ordinal))
            BumpVisual();
    }

    private void ClearActiveAccent() => ClearLiveLinkVisuals();

    private void RebuildGraphIfNeeded()
    {
        if (!_graphDirty || _controller == null) return;
        _graphDirty = false;
        _nodes.Clear();
        _wires.Clear();
        BumpContent();

        var entry = new GraphNode { Id = EntryNodeId, Title = "Entry", Position = new Float2(-220, 60), Width = 90, Accent = EntryAccent, Pill = true };
        entry.Outputs.Add(new GraphPort(OutPort, "") { Shape = PortShape.Arrow, Color = EntryAccent, Side = PortSide.Right });
        _nodes.Add(entry);

        var anyState = new GraphNode { Id = AnyStateNodeId, Title = "Any State", Position = new Float2(-220, 170), Width = 90, Accent = AnyStateAccent, Pill = true };
        anyState.Outputs.Add(new GraphPort(OutPort, "") { Shape = PortShape.Arrow, Color = AnyStateAccent, Side = PortSide.Right });
        _nodes.Add(anyState);

        var exit = new GraphNode { Id = ExitNodeId, Title = "Exit", Position = new Float2(-220, 280), Width = 90, Accent = ExitAccent, Pill = true };
        exit.Inputs.Add(new GraphPort(InPort, "") { Shape = PortShape.Arrow, Color = ExitAccent, Side = PortSide.Left });
        _nodes.Add(exit);

        if (_smPath.Count > 0)
        {
            var up = new GraphNode { Id = UpNodeId, Title = "Up", Position = new Float2(-220, -30), Width = 90, Accent = StateMachineAccent, Pill = true };
            _nodes.Add(up);
        }

        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        string defaultName = AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            bool isDefault = s.Name == defaultName;
            string title = AnimatorGraphClipboard.FormatStateTitle(s);
            var node = new GraphNode
            {
                Id = s.Name, Title = title,
                Position = new Float2(s.PosX, s.PosY),
                Width = AnimatorGraphClipboard.StateNodeWidth(s),
                Accent = isDefault ? DefaultAccent : StateAccent,
                UserData = s.Name
            };
            node.Inputs.Add(new GraphPort(InPort, "") { Shape = PortShape.Arrow, Color = FlowColor, Side = PortSide.Left });
            node.Outputs.Add(new GraphPort(OutPort, "") { Shape = PortShape.Arrow, Color = FlowColor, Side = PortSide.Right });
            _nodes.Add(node);
        }

        var children = AnimatorControllerEditGraph.ChildMachines(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < children.Count; i++)
        {
            var sm = children[i];
            var smNode = new GraphNode
            {
                Id = SmNodeId(sm.Name),
                Title = sm.Name + "  (SM)",
                Position = new Float2(sm.PosX, sm.PosY),
                Width = 160f,
                Accent = StateMachineAccent,
                UserData = sm.Name
            };
            smNode.Inputs.Add(new GraphPort(InPort, "") { Shape = PortShape.Arrow, Color = FlowColor, Side = PortSide.Left });
            smNode.Outputs.Add(new GraphPort(OutPort, "") { Shape = PortShape.Arrow, Color = FlowColor, Side = PortSide.Right });
            _nodes.Add(smNode);
        }

        var machine = AnimatorControllerEditGraph.Machine(_controller, _selectedLayerIndex, _smPath);

        if (!string.IsNullOrEmpty(defaultName))
            AddOrMergeWire(EntryNodeId, DestNodeId(defaultName), EntryAccent, name: null);

        var entries = machine.EntryTransitions;
        for (int i = 0; i < entries.Count; i++)
        {
            var t = entries[i];
            AddOrMergeWire(EntryNodeId, DestNodeId(t.DestinationStateName), EntryAccent, t.Name);
        }

        var any = AnimatorControllerEditGraph.AnyStateTransitions(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < any.Count; i++)
        {
            var t = any[i];
            AddOrMergeWire(AnyStateNodeId, DestNodeId(t.DestinationStateName), AnyStateAccent, t.Name);
        }

        var transitions = AnimatorControllerEditGraph.Transitions(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            var names = s.TransitionNames;
            for (int n = 0; n < names.Count; n++)
            {
                string tn = names[n];
                AnimatorController.Transition? t = null;
                for (int k = 0; k < transitions.Count; k++)
                {
                    if (transitions[k].Name == tn) { t = transitions[k]; break; }
                }
                if (t == null) continue;
                AddOrMergeWire(s.Name, DestNodeId(t.DestinationStateName), FlowColor, t.Name);
            }
        }

        var smOut = machine.StateMachineTransitions;
        for (int i = 0; i < smOut.Count; i++)
        {
            var t = smOut[i];
            AddOrMergeWire(SmNodeId(t.SourceStateMachineName), DestNodeId(t.DestinationStateName), FlowColor, t.Name);
        }

        FanOppositeWires();
    }

    /// <summary>
    /// Opposite-direction pairs (A→B and B→A) would overlap on the same segment; give each a
    /// perpendicular control point so both arrows stay readable (Unity offsets these too). The
    /// perpendicular flips with the wire's direction, fanning the pair to opposite sides.
    /// </summary>
    private void FanOppositeWires()
    {
        for (int i = 0; i < _wires.Count; i++)
        {
            GraphConnection wire = _wires[i];
            wire.ControlPoints.Clear();
            for (int j = 0; j < _wires.Count; j++)
            {
                if (j == i) continue;
                GraphConnection other = _wires[j];
                if (other.FromNode != wire.ToNode || other.ToNode != wire.FromNode) continue;
                if (TryGetNodeCenter(wire.FromNode, out Float2 from)
                    && TryGetNodeCenter(wire.ToNode, out Float2 to)
                    && AnimatorGraphClipboard.TryFanControlPoint(from, to, 0, 2, out Float2 point))
                    wire.ControlPoints.Add(point);
                break;
            }
        }
    }

    private bool TryGetNodeCenter(string id, out Float2 center)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].Id != id) continue;
            center = new Float2(_nodes[i].Position.X + _nodes[i].Width * 0.5f, _nodes[i].Position.Y + 20f);
            return true;
        }
        center = default;
        return false;
    }

    private void AddOrMergeWire(string from, string to, Color color, string? name)
    {
        for (int i = 0; i < _wires.Count; i++)
        {
            GraphConnection w = _wires[i];
            if (w.FromNode != from || w.ToNode != to) continue;
            var names = new List<string>();
            CollectTransitionNames(w.UserData, names);
            if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                names.Add(name);
            w.UserData = names;
            return;
        }
        object? data = string.IsNullOrEmpty(name) ? null : new List<string> { name };
        _wires.Add(new GraphConnection(from, OutPort, to, InPort) { Color = color, UserData = data });
    }

    private static void CollectTransitionNames(object? data, List<string> dest)
    {
        if (data is string s)
        {
            if (!string.IsNullOrEmpty(s)) dest.Add(s);
            return;
        }
        if (data is List<string> list)
        {
            dest.AddRange(list);
            return;
        }
        if (data is IEnumerable<string> seq)
        {
            foreach (string n in seq)
                dest.Add(n);
        }
    }

    private void DrawBlendTreeCanvas(Paper paper, float w, float h)
    {
        if (_controller == null || _editingBlendTreeState == null) return;
        var state = FindStateByName(_editingBlendTreeState);
        BlendTree? tree = state != null ? BlendTreeEditorModel.GetTree(state) : null;
        if (tree == null)
        {
            CloseBlendTreeEditor();
            return;
        }

        using (paper.Column("anim_bt").Width(w).Height(h)
            .BackgroundColor(AnimationEditorChrome.Canvas)
            .Padding(8, 8, 8, 8).Enter())
        {
            var parmNames = new List<string>();
            var parms = _controller.Parameters;
            for (int i = 0; i < parms.Count; i++)
                parmNames.Add(parms[i].Name);

            using (paper.Row("anim_bt_param").Width(w).Height(22).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_bt_pl", "Parameter").Show();
                if (parmNames.Count == 0)
                {
                    Origami.TextField(paper, "anim_bt_pn", tree.ParameterName, v => tree.ParameterName = v ?? "").Show();
                }
                else
                {
                    int sel = Math.Max(0, parmNames.IndexOf(tree.ParameterName));
                    Origami.Dropdown(paper, "anim_bt_pd", sel, si =>
                    {
                        if (si >= 0 && si < parmNames.Count)
                            tree.ParameterName = parmNames[si];
                    }, parmNames).Show();
                }
                Origami.Label(paper, "anim_bt_pvl", "Preview").Show();
                Origami.NumericField(paper, "anim_bt_pv", _blendPreviewParam, v => _blendPreviewParam = v).Show();
                if (tree.Type == BlendType.TwoDCartesian || tree.Type == BlendType.TwoDDirectional)
                    Origami.NumericField(paper, "anim_bt_pvy", _blendPreviewParamY, v => _blendPreviewParamY = v).Show();
            }

            using (paper.Row("anim_bt_type").Width(w).Height(22).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_bt_tl", "Type").Show();
                var types = new List<string> { "1D", "2D Cartesian", "2D Directional", "Direct" };
                Origami.Dropdown(paper, "anim_bt_td", (int)tree.Type, si =>
                {
                    if (si >= 0 && si < 4) tree.Type = (BlendType)si;
                }, types).Show();
                Origami.Toggle(paper, "anim_bt_auto", tree.UseAutomaticThresholds, v =>
                {
                    tree.UseAutomaticThresholds = v;
                    if (v) BlendTreeEditorModel.ApplyAutomaticThresholds(tree);
                }).Show();
                Origami.Label(paper, "anim_bt_autol", "Auto thresholds").Show();
                // Unity's one-shot "Compute Thresholds": evenly respace without turning auto mode on.
                Origami.Button(paper, "anim_bt_compute", "Compute Thresholds", () =>
                {
                    if (_controller != null) Undo.Snapshot(_controller);
                    BlendTreeEditorModel.ApplyAutomaticThresholds(tree);
                }).Height(20).Show();
                if (tree.Type == BlendType.TwoDCartesian || tree.Type == BlendType.TwoDDirectional)
                {
                    Origami.Label(paper, "anim_bt_ply", "Parameter Y").Show();
                    if (parmNames.Count == 0)
                        Origami.TextField(paper, "anim_bt_pny", tree.ParameterNameY, v => tree.ParameterNameY = v ?? "").Show();
                    else
                    {
                        int selY = Math.Max(0, parmNames.IndexOf(tree.ParameterNameY));
                        Origami.Dropdown(paper, "anim_bt_pdy", selY, si =>
                        {
                            if (si >= 0 && si < parmNames.Count)
                                tree.ParameterNameY = parmNames[si];
                        }, parmNames).Show();
                    }
                }
            }

            BlendTreeEditorModel.SortChildrenByThreshold(tree);
            var children = tree.Children;
            int n = children.Count;
            if (n > _blendWeightScratch.Length)
                n = _blendWeightScratch.Length;
            BlendTreeEditorModel.ComputeWeights(tree, _blendPreviewParam, _blendPreviewParamY, _blendWeightScratch);

            using (paper.Row("anim_bt_wrow").Width(w).Height(18).ColBetween(4).Enter())
            {
                for (int i = 0; i < n; i++)
                {
                    float wt = _blendWeightScratch[i];
                    paper.Box($"anim_bt_wb{i}").Height(14).Width(Math.Max(8f, (w - 32f) * Math.Max(wt, 0.02f)))
                        .BackgroundColor(wt > 0.01f ? AnimationEditorChrome.Selection : AnimationEditorChrome.Raised);
                }
            }

            if (tree.Type == BlendType.TwoDCartesian || tree.Type == BlendType.TwoDDirectional)
            {
                DrawBlendTree2DPlot(paper, tree, children, n, w);
            }
            else
            {
            float trackH = 36f;
            float minT = n > 0 ? children[0].Threshold : 0f;
            float maxT = n > 1 ? children[n - 1].Threshold : minT + 1f;
            float range = maxT - minT;
            if (range <= 0f) range = 1f;
            float trackW = Math.Max(1f, w - 40f);

            using (paper.Row("anim_bt_track").Width(w - 16f).Height(trackH)
                .BackgroundColor(AnimationEditorChrome.Raised)
                .OnClick(e =>
                {
                    _blendPreviewParam = minT + e.NormalizedPosition.X * range;
                })
                .Enter())
            {
                float prev = minT;
                for (int i = 0; i < n; i++)
                {
                    int idx = i;
                    float gap = ((children[i].Threshold - prev) / range) * trackW;
                    paper.Box($"anim_bt_sp{idx}").Width(Math.Max(0f, gap)).Height(1);
                    paper.Box($"anim_bt_tick{idx}").Width(12).Height(20)
                        .BackgroundColor(AnimationEditorChrome.Keyframe32)
                        .OnDragging(idx, (ci, e) =>
                        {
                            children[ci].Threshold += e.Delta.X / trackW * range;
                            BlendTreeEditorModel.SortChildrenByThreshold(tree);
                        });
                    prev = children[i].Threshold;
                }
            }
            }

            Origami.ScrollView(paper, "anim_bt_list", w - 16f, h - 120f).Padding(2).Body(() =>
            {
                for (int i = 0; i < children.Count; i++)
                {
                    int idx = i;
                    BlendTree.Child child = children[idx];
                    using (paper.Row($"anim_bt_c{idx}").Width(w - 24f).Height(24).ColBetween(6).Enter())
                    {
                        Origami.Label(paper, $"anim_bt_cw{idx}", $"{_blendWeightScratch[idx < _blendWeightScratch.Length ? idx : 0]:0.00}").Show();
                        PropertyGridUtils.DrawField(paper, $"anim_bt_cc{idx}", "Motion",
                            typeof(AssetRef<AnimationClip>), child.Clip,
                            v => child.Clip = v is AssetRef<AnimationClip> r ? r : default);
                        Origami.NumericField(paper, $"anim_bt_ct{idx}", child.Threshold, v =>
                        {
                            child.Threshold = v;
                            BlendTreeEditorModel.SortChildrenByThreshold(tree);
                        }).Show();
                        if (tree.Type != BlendType.OneD)
                        {
                            Origami.NumericField(paper, $"anim_bt_cx{idx}", child.Position.X, v =>
                            {
                                child.Position = new Float2(v, child.Position.Y);
                            }).Show();
                            Origami.NumericField(paper, $"anim_bt_cy{idx}", child.Position.Y, v =>
                            {
                                child.Position = new Float2(child.Position.X, v);
                            }).Show();
                        }
                        Origami.Button(paper, $"anim_bt_cd{idx}", "×", () => children.RemoveAt(idx)).Show();
                    }
                }
            });

            Origami.Button(paper, "anim_bt_add", "+ Add Motion", () =>
            {
                Undo.Snapshot(_controller);
                float next = children.Count > 0 ? children[children.Count - 1].Threshold + 0.25f : 0f;
                children.Add(new BlendTree.Child { Threshold = next });
            }).Show();
        }
    }

    private void DrawInspector(Paper paper, float w, float h)
    {
        using (paper.Row("anim_ins").Width(w).Height(h).Padding(8, 4, 4, 4)
            .BackgroundColor(AnimationEditorChrome.Panel).Enter())
        {
            if (_controller == null) { Origami.Label(paper, "anim_ins_empty", "Select a .controller to edit").Show(); return; }
            if (!TryDrawSelectionInspector(paper, w))
                Origami.Label(paper, "anim_ins_hint", "Click a state or transition to edit").Show();
        }
    }

    private void DrawStateInspector(Paper paper, float w, AnimatorController.State s)
    {
        using (paper.Column("anim_ins_s").Width(w).Enter())
        {
            // Unity-style identity row: film glyph + name field spanning the grid.
            using (paper.Row("anim_ins_s0").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_sicon", EditorIcons.Film).Show();
                Origami.TextField(paper, "anim_ins_sn", s.Name, v =>
                {
                    if (_controller == null || string.IsNullOrEmpty(v)) return;
                    string old = s.Name;
                    RenameStateReferences(old, v);
                    s.Name = v; _selectedStateName = v; _graphDirty = true;
                }).Width(UnitValue.Stretch()).Show();
            }
            using (paper.Row("anim_ins_s1").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_snl", "Speed").Show();
                Origami.NumericField(paper, "anim_ins_sp", s.Speed, v => s.Speed = v)
                    .Width(UnitValue.Stretch()).Show();
                Origami.Label(paper, "anim_ins_sll", "Loop").Show();
                Origami.Toggle(paper, "anim_ins_sl", s.IsLooping, v => s.IsLooping = v).Show();
            }
            using (paper.Row("anim_ins_stag").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_stg", "Tag").Show();
                Origami.TextField(paper, "anim_ins_stgv", s.Tag, v => s.Tag = v ?? string.Empty)
                    .Width(UnitValue.Stretch()).Placeholder("(none)").Show();
            }
            using (paper.Row("anim_ins_s4").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Toggle(paper, "anim_ins_swd", s.WriteDefaults, v => s.WriteDefaults = v).LabelRight("Write Defaults").Show();
                Origami.Toggle(paper, "anim_ins_smi", s.Mirror, v => s.Mirror = v).LabelRight("Mirror").Show();
                Origami.Toggle(paper, "anim_ins_sik", s.IKOnFeet, v => s.IKOnFeet = v).LabelRight("Foot IK").Show();
            }
            using (paper.Row("anim_ins_s5").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_sco", "Cycle Offset").Show();
                Origami.NumericField(paper, "anim_ins_scov", s.CycleOffset, v => s.CycleOffset = v).Show();
            }

            // Unity's per-state parameter bindings: an animator parameter can drive the value live.
            using (paper.Row("anim_ins_pb1").Width(w).Height(24).ColBetween(8).Enter())
            {
                DrawParamBinding(paper, "anim_ins_pspd", "Speed Param", s.SpeedMultiplierParameter,
                    AnimatorParameterType.Float, v => s.SpeedMultiplierParameter = v);
                DrawParamBinding(paper, "anim_ins_pmir", "Mirror Param", s.MirrorParameter,
                    AnimatorParameterType.Bool, v => s.MirrorParameter = v);
            }
            using (paper.Row("anim_ins_pb2").Width(w).Height(24).ColBetween(8).Enter())
            {
                DrawParamBinding(paper, "anim_ins_pcyc", "Cycle Param", s.CycleOffsetParameter,
                    AnimatorParameterType.Float, v => s.CycleOffsetParameter = v);
                DrawParamBinding(paper, "anim_ins_ptim", "Time Param", s.TimeParameter,
                    AnimatorParameterType.Float, v => s.TimeParameter = v);
            }
            using (paper.Row("anim_ins_s3").Width(w).Height(24).ColBetween(8).Enter())
            {
                PropertyGridUtils.DrawField(paper, "anim_ins_sm", "Motion",
                    typeof(AssetRef<AnimationClip>), s.Motion,
                    v => s.Motion = v is AssetRef<AnimationClip> clip ? clip : default);
                PropertyGridUtils.DrawField(paper, "anim_ins_sbt", "Blend Tree",
                    typeof(AssetRef<BlendTree>), s.BlendTree ?? default,
                    v =>
                    {
                        if (v is AssetRef<BlendTree> bt && !bt.IsExplicitNull)
                            s.BlendTree = bt;
                        else
                            s.BlendTree = null;
                    });
            }
            Origami.Label(paper, "anim_ins_bh", "Behaviours").Show();
            for (int i = 0; i < s.Behaviours.Count; i++)
            {
                int idx = i;
                using (paper.Row($"anim_ins_bh{i}").Width(w).Height(22).ColBetween(4).Enter())
                {
                    Origami.Label(paper, $"anim_ins_bhn{i}", ShortBehaviourName(s.Behaviours[i])).Show();
                    Origami.Button(paper, $"anim_ins_bhx{i}", "×", () =>
                    {
                        if ((uint)idx < (uint)s.Behaviours.Count)
                            s.Behaviours.RemoveAt(idx);
                    }).Show();
                }
            }
            Origami.Button(paper, "anim_ins_bha", "Add Behaviour", () =>
            {
                _behaviourPicker = !_behaviourPicker;
            }).Show();
            if (_behaviourPicker)
            {
                EnsureBehaviourTypes();
                List<Type> types = _behaviourTypes!;
                if (types.Count == 0)
                    Origami.Label(paper, "anim_ins_bhe", "No StateMachineBehaviour types loaded").Show();
                for (int i = 0; i < types.Count; i++)
                {
                    Type t = types[i];
                    Origami.Button(paper, $"anim_ins_bhp{i}", t.Name, () =>
                    {
                        s.Behaviours.Add(t.FullName ?? t.Name);
                        _behaviourPicker = false;
                    }).Show();
                }
            }

            DrawStateTransitionsList(paper, w, s);
        }
    }

    /// <summary>A "None"-headed dropdown over the animator parameters of one type, bound to a
    /// per-state parameter-binding field (Unity's speed/mirror/cycle/time parameter pickers).</summary>
    private void DrawParamBinding(Paper paper, string id, string label, string current,
        AnimatorParameterType type, Action<string> set)
    {
        Origami.Label(paper, id + "_l", label).Show();
        var names = new List<string> { "None" };
        int selected = 0;
        if (_controller != null)
        {
            var parms = _controller.Parameters;
            for (int i = 0; i < parms.Count; i++)
            {
                if (parms[i].Type != type) continue;
                names.Add(parms[i].Name);
                if (parms[i].Name == current) selected = names.Count - 1;
            }
        }
        Origami.Dropdown(paper, id + "_dd", selected, si =>
        {
            if (_controller != null) Undo.Snapshot(_controller);
            set(si <= 0 || si >= names.Count ? string.Empty : names[si]);
        }, names.ToArray()).Show();
    }

    /// <summary>Unity's state-inspector Transitions list: this state's outgoing edges with the
    /// Solo/Mute column header; clicking one selects it so the transition inspector opens.</summary>
    private void DrawStateTransitionsList(Paper paper, float w, AnimatorController.State s)
    {
        if (_controller == null) return;
        using (paper.Row("anim_ins_trh").Width(w).Height(22).ColBetween(8).Enter())
        {
            Origami.Label(paper, "anim_ins_trhl", "Transitions").Show();
            paper.Box("anim_ins_trhs").Width(UnitValue.Stretch()).Height(1);
            Origami.Label(paper, "anim_ins_trhc_solo", "Solo").TextColor(EditorTheme.InkFaint).FontSize(EditorTheme.FontSize - 1).Show();
            Origami.Label(paper, "anim_ins_trhc_mute", "Mute").TextColor(EditorTheme.InkFaint).FontSize(EditorTheme.FontSize - 1).Show();
        }
        if (s.TransitionNames.Count == 0)
        {
            using (paper.Row("anim_ins_tre_r").Width(w).Height(20)
                .BackgroundColor(AnimationEditorChrome.Canvas).Enter())
            {
                Origami.Label(paper, "anim_ins_tre", "List is Empty").Show();
                paper.Box("anim_ins_tre_s").Width(UnitValue.Stretch()).Height(1);
                Origami.Label(paper, "anim_ins_tre_add", "−").TextColor(EditorTheme.InkFaint).Show();
            }
            return;
        }
        for (int i = 0; i < s.TransitionNames.Count; i++)
        {
            string name = s.TransitionNames[i];
            var t = FindTransition(name);
            if (t == null) continue;
            int idx = i;
            string dest = string.IsNullOrEmpty(t.DestinationStateName) ? "Exit" : t.DestinationStateName;
            using (paper.Row($"anim_ins_tr{i}").Width(w).Height(20).ColBetween(8).Enter())
            {
                Origami.Button(paper, $"anim_ins_trb{idx}", $"{s.Name} → {dest}", () =>
                {
                    SelectTransitionByName(name);
                }).Width(UnitValue.Stretch()).Height(18).Ghost().Show();
                Origami.Checkbox(paper, $"anim_ins_trsolo{idx}", t.Solo, v => { Undo.Snapshot(_controller!); FindTransition(name)!.Solo = v; _graphDirty = true; }).Show();
                Origami.Checkbox(paper, $"anim_ins_trmute{idx}", t.Mute, v => { Undo.Snapshot(_controller!); FindTransition(name)!.Mute = v; _graphDirty = true; }).Show();
            }
        }
    }

    private static string ShortBehaviourName(string typeName)
    {
        int comma = typeName.IndexOf(',');
        string full = comma >= 0 ? typeName[..comma].Trim() : typeName;
        int dot = full.LastIndexOf('.');
        return dot >= 0 ? full[(dot + 1)..] : full;
    }

    private static void EnsureBehaviourTypes()
    {
        if (_behaviourTypes != null) return;
        var list = new List<Type>();
        foreach (Type type in EditorUtils.GetAllTypes())
        {
            if (!typeof(StateMachineBehaviour).IsAssignableFrom(type) || type.IsAbstract)
                continue;
            list.Add(type);
        }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        _behaviourTypes = list;
    }

    private void RenameStateReferences(string old, string v)
    {
        if (_controller == null) return;
        var transitions = AnimatorControllerEditGraph.Transitions(_controller, _selectedLayerIndex, _smPath);
        var any = AnimatorControllerEditGraph.AnyStateTransitions(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < transitions.Count; i++)
        {
            if (transitions[i].DestinationStateName == old)
                transitions[i].DestinationStateName = v;
        }
        for (int i = 0; i < any.Count; i++)
        {
            if (any[i].DestinationStateName == old)
                any[i].DestinationStateName = v;
        }
        if (AnimatorControllerEditGraph.GetDefaultStateName(_controller, _selectedLayerIndex, _smPath) == old)
            AnimatorControllerEditGraph.SetDefaultStateName(_controller, _selectedLayerIndex, v, _smPath);
    }

    private void DrawTransitionGroup(Paper paper, float w)
    {
        using (paper.Column("anim_ins_tg").Width(w).Enter())
        {
            Origami.Label(paper, "anim_ins_tgh", $"{_selectedTransitionNames.Count} transitions").Show();
            for (int i = 0; i < _selectedTransitionNames.Count; i++)
            {
                int idx = i;
                string name = _selectedTransitionNames[i];
                var t = FindTransition(name);
                using (paper.Row($"anim_ins_tg{i}").Width(w).Height(22).ColBetween(4).Enter())
                {
                    bool selected = name == _selectedTransitionName;
                    Origami.Button(paper, $"anim_ins_tgs{i}", selected ? "●" : "○", () =>
                    {
                        _selectedTransitionName = name;
                    }).Show();
                    Origami.Label(paper, $"anim_ins_tgn{i}", name).Show();
                    if (t != null)
                    {
                        Origami.Toggle(paper, $"anim_ins_tgso{i}", t.Solo, v => t.Solo = v).LabelRight("Solo").Show();
                        Origami.Toggle(paper, $"anim_ins_tgmu{i}", t.Mute, v => t.Mute = v).LabelRight("Mute").Show();
                    }
                    Origami.Button(paper, $"anim_ins_tgu{i}", "↑", () =>
                    {
                        string? owner = FindOwnerState(name);
                        if (owner != null) MoveTransition(owner, name, -1);
                        if (idx > 0)
                        {
                            _selectedTransitionNames.RemoveAt(idx);
                            _selectedTransitionNames.Insert(idx - 1, name);
                        }
                    }).Show();
                    Origami.Button(paper, $"anim_ins_tgd{i}", "↓", () =>
                    {
                        string? owner = FindOwnerState(name);
                        if (owner != null) MoveTransition(owner, name, 1);
                        if (idx + 1 < _selectedTransitionNames.Count)
                        {
                            _selectedTransitionNames.RemoveAt(idx);
                            _selectedTransitionNames.Insert(idx + 1, name);
                        }
                    }).Show();
                }
            }
        }
    }

    private string? FindOwnerState(string transitionName)
    {
        if (_controller == null) return null;
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < states.Count; i++)
        {
            var names = states[i].TransitionNames;
            for (int n = 0; n < names.Count; n++)
            {
                if (names[n] == transitionName)
                    return states[i].Name;
            }
        }
        return null;
    }

    private void DrawTransitionTimeline(Paper paper, float w, AnimatorController.Transition t)
    {
        var box = paper.Box("anim_ins_tl")
            .Width(w).Height(TransitionTimelineLayout.Height)
            .BackgroundColor(AnimationEditorChrome.Canvas)
            .OnDragStart(e =>
            {
                var layout = TransitionTimelineLayout.Compute(
                    0, 0, (float)e.ElementRect.Size.X, TransitionTimelineLayout.Height,
                    t.Duration, t.Offset, t.ExitTime, t.HasExitTime, t.HasFixedDuration);
                _tlHit = TransitionTimelineLayout.HitTest(layout, (float)e.RelativePosition.X, (float)e.RelativePosition.Y);
                _tlLastX = (float)e.RelativePosition.X;
            })
            .OnDragging(e =>
            {
                if (_tlHit == TransitionTimelineLayout.Hit.None) return;
                float x = (float)e.RelativePosition.X;
                float width = (float)e.ElementRect.Size.X;
                var layout = TransitionTimelineLayout.Compute(
                    0, 0, width, TransitionTimelineLayout.Height,
                    t.Duration, t.Offset, t.ExitTime, t.HasExitTime, t.HasFixedDuration);
                float dur = t.Duration, off = t.Offset, exit = t.ExitTime;
                TransitionTimelineLayout.ApplyDrag(_tlHit, x - _tlLastX, width, layout.ViewLength, ref dur, ref off, ref exit);
                t.Duration = dur;
                t.Offset = off;
                t.ExitTime = exit;
                _tlLastX = x;
            })
            .OnDragEnd(_ => _tlHit = TransitionTimelineLayout.Hit.None)
            .OnPostLayout((handle, _) =>
            {
                PaintTransitionTimeline(paper, ref handle, t);
            });
        using (box.Enter()) { }
    }

    private static void DrawTransitionTimelineContent(Canvas canvas, Rect r, AnimatorController.Transition t)
    {
        float x = (float)r.Min.X, y = (float)r.Min.Y, w = (float)r.Size.X, h = (float)r.Size.Y;
        var layout = TransitionTimelineLayout.Compute(
            x, y, w, h, t.Duration, t.Offset, t.ExitTime, t.HasExitTime, t.HasFixedDuration);
        canvas.RoundedRectFilled(layout.SourceX, layout.SourceY, layout.SourceW, TransitionTimelineLayout.BarH,
            3f, Color32.FromArgb(220, 70, 110, 160));
        canvas.RoundedRectFilled(layout.DestX, layout.DestY, layout.DestW, TransitionTimelineLayout.BarH,
            3f, Color32.FromArgb(220, 90, 140, 90));
        canvas.RoundedRectFilled(layout.CrossX, layout.CrossY, layout.CrossW, TransitionTimelineLayout.BarH,
            2f, Color32.FromArgb(140, 220, 180, 70));
        if (layout.HasExitTime)
        {
            canvas.SetStrokeColor(Color32.FromArgb(255, 240, 200, 80));
            canvas.SetStrokeWidth(2f);
            canvas.BeginPath();
            canvas.MoveTo(layout.ExitX, layout.SourceY - 2f);
            canvas.LineTo(layout.ExitX, layout.SourceY + TransitionTimelineLayout.BarH + 2f);
            canvas.Stroke();
        }
    }

    private void DrawTransitionInspector(Paper paper, float w, AnimatorController.Transition t)
    {
        using (paper.Column("anim_ins_t").Width(w).Enter())
        {
            using (paper.Row("anim_ins_t1").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_tnl", "→").Show();
                string destLabel = t.DestinationStateName == ExitNodeId ? "Exit" : t.DestinationStateName;
                Origami.Label(paper, "anim_ins_tn", destLabel).Show();
                Origami.Label(paper, "anim_ins_tdl", "Duration").Show();
                Origami.NumericField(paper, "anim_ins_td", t.Duration, v => t.Duration = v).Show();
                Origami.Label(paper, "anim_ins_tol", "Offset").Show();
                Origami.NumericField(paper, "anim_ins_to", t.Offset, v => t.Offset = v).Show();
            }
            DrawTransitionTimeline(paper, w, t);
            using (paper.Row("anim_ins_t2").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Toggle(paper, "anim_ins_te", t.HasExitTime, v => t.HasExitTime = v).LabelRight("Exit Time").Show();
                if (t.HasExitTime)
                    Origami.NumericField(paper, "anim_ins_tet", t.ExitTime, v => t.ExitTime = v).Show();
                Origami.Toggle(paper, "anim_ins_tso", t.Solo, v => t.Solo = v).LabelRight("Solo").Show();
                Origami.Toggle(paper, "anim_ins_tmu", t.Mute, v => t.Mute = v).LabelRight("Mute").Show();
            }
            using (paper.Row("anim_ins_tfd").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Toggle(paper, "anim_ins_thf", t.HasFixedDuration, v => t.HasFixedDuration = v).LabelRight("Fixed Duration").Show();
                Origami.Toggle(paper, "anim_ins_toi", t.OrderedInterruption, v => t.OrderedInterruption = v).LabelRight("Ordered").Show();
                Origami.Toggle(paper, "anim_ins_tcs", t.CanTransitionToSelf, v => t.CanTransitionToSelf = v).LabelRight("Can Transition To Self").Show();
            }
            using (paper.Row("anim_ins_tis").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_tisl", "Interruption").Show();
                Origami.EnumDropdown(paper, "anim_ins_tisv", t.InterruptionSource, v => t.InterruptionSource = v).Show();
            }
            using (paper.Row("anim_ins_t3").Width(w).Height(24).ColBetween(8).Enter())
            {
                Origami.Label(paper, "anim_ins_tcl", $"Conditions: {t.Conditions.Count}").Show();
                Origami.Button(paper, "anim_ins_tca", "+", () =>
                    t.Conditions.Add(new AnimatorController.Condition { Mode = ConditionMode.If, ParamName = "", Threshold = 0f })).Show();
            }
            for (int i = 0; i < t.Conditions.Count; i++)
            {
                int idx = i;
                var c = t.Conditions[i];
                using (paper.Row($"anim_ins_tc{i}").Width(w).Height(22).ColBetween(4).Enter())
                {
                    var parmNames = new List<string>();
                    if (_controller != null)
                    {
                        var parms = _controller.Parameters;
                        for (int p = 0; p < parms.Count; p++)
                            parmNames.Add(parms[p].Name);
                    }
                    int selIdx = Math.Max(0, parmNames.IndexOf(c.ParamName));
                    Origami.Dropdown(paper, $"anim_ins_tcp{i}", selIdx, si =>
                    {
                        if (si >= 0 && si < parmNames.Count) c.ParamName = parmNames[si];
                    }, parmNames).Show();

                    Origami.EnumDropdown(paper, $"anim_ins_tcm{i}", c.Mode, v => c.Mode = v).Show();

                    if (c.Mode is ConditionMode.Greater or ConditionMode.Less or ConditionMode.Equals or ConditionMode.NotEqual)
                        Origami.NumericField(paper, $"anim_ins_tct{i}", c.Threshold, v => c.Threshold = v).Show();

                    Origami.Button(paper, $"anim_ins_tcd{i}", "×", () => t.Conditions.RemoveAt(idx)).Show();
                }
            }
        }
    }

    private static bool IsSpecialNode(string id)
        => id == EntryNodeId || id == AnyStateNodeId || id == ExitNodeId
            || id == UpNodeId || id.StartsWith(StateMachinePrefix, StringComparison.Ordinal);

    private static string SmNodeId(string name) => StateMachinePrefix + name;

    private static bool TrySmName(string id, out string name)
    {
        if (!string.IsNullOrEmpty(id) && id.StartsWith(StateMachinePrefix, StringComparison.Ordinal))
        {
            name = id.Substring(StateMachinePrefix.Length);
            return name.Length > 0;
        }
        name = string.Empty;
        return false;
    }

    private string DestNodeId(string dest)
    {
        if (string.IsNullOrEmpty(dest) || dest == ExitNodeId)
            return dest;
        if (_controller == null) return dest;
        var children = AnimatorControllerEditGraph.ChildMachines(_controller, _selectedLayerIndex, _smPath);
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Name == dest)
                return SmNodeId(dest);
        }
        return dest;
    }

    private void DeleteStateMachineNamed(string name)
    {
        if (_controller == null) return;
        Undo.Snapshot(_controller);
        if (AnimatorControllerEditGraph.DeleteStateMachine(_controller, _selectedLayerIndex, _smPath, name))
            _graphDirty = true;
    }

    private AnimatorController.State? FindStateByName(string name)
        => _controller == null ? null : AnimatorControllerEditGraph.FindState(_controller, _selectedLayerIndex, name, _smPath);

    private AnimatorController.Transition? FindTransition(string name)
        => _controller == null ? null : AnimatorControllerEditGraph.FindTransition(_controller, _selectedLayerIndex, name, _smPath);

    public void SelectStates(params string[] names)
    {
        _selectedStateNames.Clear();
        for (int i = 0; i < names.Length; i++)
        {
            if (!string.IsNullOrEmpty(names[i]) && !IsSpecialNode(names[i]))
                _selectedStateNames.Add(names[i]);
        }
        _selectedStateName = _selectedStateNames.Count > 0 ? _selectedStateNames[0] : null;
        _ctrl.SelectNodes(_selectedStateNames);
    }

    public void SelectAllStates()
    {
        if (_controller == null) return;
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        var names = new string[states.Count];
        for (int i = 0; i < states.Count; i++)
            names[i] = states[i].Name;
        SelectStates(names);
    }

    public void FrameSelected() => _ctrl.FrameSelection();

    public void CopySelection()
    {
        if (_controller == null || _selectedStateNames.Count == 0) return;
        AnimatorGraphClipboard.Copy(_controller, _selectedLayerIndex, _selectedStateNames, _smPath);
    }

    public List<string> PasteClipboard()
    {
        var created = new List<string>();
        if (_controller == null) return created;
        Undo.Snapshot(_controller);
        created = AnimatorGraphClipboard.Paste(_controller, _selectedLayerIndex, path: _smPath);
        if (created.Count > 0)
        {
            _graphDirty = true;
            SelectStates(created.ToArray());
        }
        return created;
    }

    public List<string> DuplicateSelection()
    {
        var created = new List<string>();
        if (_controller == null || _selectedStateNames.Count == 0) return created;
        Undo.Snapshot(_controller);
        created = AnimatorGraphClipboard.Duplicate(_controller, _selectedLayerIndex, _selectedStateNames, _smPath);
        if (created.Count > 0)
        {
            _graphDirty = true;
            SelectStates(created.ToArray());
        }
        return created;
    }

    public bool AddBehaviourToSelected(string typeName)
    {
        if (string.IsNullOrEmpty(typeName) || _selectedStateName == null) return false;
        var s = FindStateByName(_selectedStateName);
        if (s == null) return false;
        s.Behaviours.Add(typeName);
        return true;
    }

    public void ShowBehaviourPicker(bool show) => _behaviourPicker = show;

    internal void ProbeSelectFirstState()
    {
        if (_controller == null) return;
        var states = AnimatorControllerEditGraph.States(_controller, _selectedLayerIndex, _smPath);
        if (states.Count == 0) return;
        _selectedStateName = states[0].Name;
        _selectedStateNames.Clear();
        _selectedStateNames.Add(_selectedStateName);
        _selectedTransitionName = null;
        _selectedTransitionNames.Clear();
        BumpVisual();
    }

    internal void ProbeAddState()
    {
        CreateStateAt(new Float2(80f, 80f));
        BumpContent();
    }
}
