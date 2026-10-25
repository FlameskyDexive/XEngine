using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using XEngine.Editor.Utils;

using XEngine.Editor.Prefabs;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Color = System.Drawing.Color;

using PropertyGridUtils = XEngine.Editor.GUI.PropertyGridUtils;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor.GUI;
using XEngine.Vector;
namespace XEngine.Editor.Inspector;

/// <summary>
/// Draws the inspector for a selected GameObject: header, transform, components, add component button.
/// </summary>
public static class GameObjectInspector
{
    public static void Draw(Paper paper, XEngine.Scribe.FontFile font, GameObject go, bool debug = false)
    {
        s_detectPrefabOverridesThisDraw = BeginPrefabOverrideDetect();

        // Prefab header bar (if this GO is a prefab instance)
        if (go.IsPrefabInstance)
            DrawPrefabHeader(paper, font, go);

        DrawHeader(paper, font, go);
        EditorGUI.Divider(paper, "gi_sep_header", 6f);
        if (go.RectTransform != null)
        {
            DrawRectTransform(paper, font, go);
        }
        else
        {
            DrawTransform(paper, font, go);
        }

        EditorGUI.Divider(paper, "gi_sep_transform", 6f);
        DrawComponents(paper, font, go, debug);

        // Only show Add Component if not a prefab instance (structure is fixed)
        if (!go.IsPrefabInstance || Application.IsPlaying)
            DrawAddComponentButton(paper, font, go);

        DrawEmbeddedMaterials(paper, font, go);

        // Detect GO-level overrides (Name, Tag, Transform, etc.)
        if (go.IsPrefabInstance && s_detectPrefabOverridesThisDraw)
            PrefabUtility.DetectGOOverrides(go);

        paper.Box("gi_bottom_pad").Height(20);
    }

    /// <summary>
    /// Unity URP inspector-open behavior: cameras and lights pick up additional data the first
    /// time they are inspected in edit mode.
    /// </summary>
    private static void EnsureUniversalAdditionalData(GameObject go)
    {
        Camera? camera = go.GetComponent<Camera>();
        if (camera != null && UniversalAdditionalCameraData.Find(camera) == null)
        {
            UniversalAdditionalCameraData.GetOrAdd(camera);
            EditorSceneManager.MarkDirty("URP additional camera data", "editor");
        }

        Light? light = go.GetComponent<Light>();
        if (light != null && UniversalAdditionalLightData.Find(light) == null)
        {
            UniversalAdditionalLightData.GetOrAdd(light);
            EditorSceneManager.MarkDirty("URP additional light data", "editor");
        }
    }

    // Sections (Transform + each component) remember their collapsed state here; absent = expanded.
    private static readonly HashSet<string> _collapsedSections = new();
    private static readonly List<MonoBehaviour> s_drawComponents = new();
    private static readonly HashSet<string> s_overriddenFields = new();
    private static long s_lastPrefabOverrideStamp = long.MinValue;
    private static bool s_detectPrefabOverridesThisDraw;

    /// <summary>
    /// Echo-serialize prefab fields only when selection or scene version changed. Idle inspector
    /// frames keep highlighting from the stored override list.
    /// </summary>
    internal static bool BeginPrefabOverrideDetect()
    {
        long stamp = EditorSceneManager.ChangeVersion ^ (Selection.ChangeVersion << 20);
        if (stamp == s_lastPrefabOverrideStamp)
            return false;
        s_lastPrefabOverrideStamp = stamp;
        return true;
    }

    private static bool IsExpanded(string id) => !_collapsedSections.Contains(id);
    private static void ToggleSection(string id) { if (!_collapsedSections.Add(id)) _collapsedSections.Remove(id); }
    private sealed record ComponentTitle(string Text);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, ComponentTitle> s_componentTitles = new();
    private static string GetComponentTitle(Type type) => s_componentTitles.GetValue(type,
        static t => new ComponentTitle(PropertyGridUtils.NicifyName(t.Name))).Text;

    private static bool SectionHeader(Paper paper, XEngine.Scribe.FontFile font, string id,
        string glyph, string title, Color titleColor, Action? trailing = null, Action? onDragStart = null,
        Action? enabledControl = null)
    {
        bool expanded = IsExpanded(id);
        var semi = EditorTheme.FontSemiBold ?? font;
        using (paper.Row(UiId.Underscore(id, "head")).Height(22).Padding(4, 2, 0, 0).RowBetween(2).Enter())
        {
            var clickRow = paper.Row(UiId.Underscore(id, "hclick")).Width(UnitValue.Stretch()).Height(22).RowBetween(2)
                .Hovered.BackgroundColor(Color.FromArgb(13, EditorTheme.Purple400)).End()
                .OnClick(id, (i, _) => ToggleSection(i));
            if (onDragStart != null)
                clickRow.OnDragStart(0, (_, _) => onDragStart());

            using (clickRow.Enter())
            {
                paper.Box(UiId.Underscore(id, "chev")).Width(11).Height(22).IsNotInteractable()
                    .Text(expanded ? EditorIcons.ChevronDown : EditorIcons.ChevronRight, font)
                    .TextColor(EditorTheme.Ink300).FontSize(10f).Alignment(TextAlignment.MiddleCenter);

                paper.Box(UiId.Underscore(id, "ico")).Width(16).Height(22).IsNotInteractable()
                    .Text(glyph, font).TextColor(EditorTheme.AccentText).FontSize(14f).Alignment(TextAlignment.MiddleCenter);

                if (enabledControl != null)
                    using (paper.Box(UiId.Underscore(id, "en_wrap")).Width(UnitValue.Auto).Height(UnitValue.Auto)
                        .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).StopEventPropagation().Enter())
                        enabledControl();

                paper.Box(UiId.Underscore(id, "title")).Width(UnitValue.Stretch()).Height(22).Clip().IsNotInteractable()
                    .Text(title, semi).TextColor(titleColor)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
            }
            trailing?.Invoke();
        }
        return expanded;
    }

    private static void ComponentHeaderButton(Paper paper, string id, string icon, Action onClick)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        paper.Box(id).Size(20, 22)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Text(icon, font).TextColor(EditorTheme.Ink300).FontSize(11f)
            .Alignment(TextAlignment.MiddleCenter).OnClick(_ => onClick());
    }


    private static void SelDropdown(Paper paper, XEngine.Scribe.FontFile font, string id,
        string? label, string value, string[] options, int current, Action<int> onSelect, bool chevron, UnitValue width)
    {
        using (paper.Row(id).Width(width).Height(20).Rounded(2).Padding(3, 3, 0, 0).RowBetween(2)
            .BackgroundColor(EditorTheme.Glass)
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Hovered.BorderColor(EditorTheme.BorderStrong).End()
            .OnClick(0, (_, _) => Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
            {
                for (int i = 0; i < options.Length; i++)
                {
                    int idx = i;
                    b.Item(options[idx], () => onSelect(idx), on: idx == current);
                }
            }))
            .Enter())
        {
            if (!string.IsNullOrEmpty(label))
                paper.Box(UiId.Underscore(id, "lbl")).Width(UnitValue.Auto).Height(20).IsNotInteractable()
                    .Text(label, font).TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            paper.Box(UiId.Underscore(id, "val")).Width(UnitValue.Stretch()).Height(20).Clip().IsNotInteractable()
                .Text(value, font).TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(string.IsNullOrEmpty(label) ? TextAlignment.MiddleLeft : TextAlignment.MiddleRight);

            if (chevron)
                paper.Box(UiId.Underscore(id, "chev")).Width(10).Height(20).IsNotInteractable()
                    .Text(EditorIcons.ChevronDown, font).TextColor(EditorTheme.Ink300).FontSize(9f).Alignment(TextAlignment.MiddleCenter);
        }
    }

    // ================================================================
    //  Multi-object inspector
    // ================================================================

    /// <summary>
    /// Draws the inspector for several GameObjects at once. Shows the transform and the components whose
    /// type is present on every selected object; fields that differ across the selection are flagged as
    /// mixed, and edits apply to all.
    /// </summary>
    public static void DrawMulti(Paper paper, XEngine.Scribe.FontFile font, IReadOnlyList<GameObject> gos, bool debug = false)
    {
        if (gos.Count == 0) return;
        if (gos.Count == 1) { Draw(paper, font, gos[0], debug); return; }

        DrawMultiHeader(paper, font, gos);
        Origami.Separator(paper, "gim_sep_header").Show();
        DrawMultiTransform(paper, font, gos);
        Origami.Separator(paper, "gim_sep_transform").Show();
        DrawMultiComponents(paper, font, gos, debug);

        paper.Box("gim_bottom_pad").Height(20);
    }

    private static void DrawMultiHeader(Paper paper, XEngine.Scribe.FontFile font, IReadOnlyList<GameObject> gos)
    {
        // InspectorPanel reuses its selection lists. Deferred text/menu callbacks must
        // own the displayed set, and a draft must have the identity of that whole set.
        var targets = new GameObject[gos.Count];
        var identity = new System.Text.StringBuilder("gim_name_");
        bool allEnabled = true, anyEnabled = false, allStatic = true, anyStatic = false;
        bool nameMixed = false, tagMixed = false, layerMixed = false;
        for (int i = 0; i < gos.Count; i++)
        {
            var go = targets[i] = gos[i];
            identity.Append(go.InstanceID).Append('_');
            allEnabled &= go.Enabled; anyEnabled |= go.Enabled;
            allStatic &= go.IsStatic; anyStatic |= go.IsStatic;
            nameMixed |= go.Name != gos[0].Name;
            tagMixed |= go.Tag != gos[0].Tag;
            layerMixed |= go.LayerIndex != gos[0].LayerIndex;
        }
        using (paper.Row("gim_header").Height(42).Margin(0, 0, 4, 2)
            .Padding(6, 6, 0, 0).RowBetween(4).Enter())
        {
            paper.Box("gim_icon").Size(24, 24).IsNotInteractable()
                .Text(EditorIcons.Cubes, font).TextColor(EditorTheme.Ink300)
                .FontSize(16).Alignment(TextAlignment.MiddleCenter);
            using (paper.Column("gim_header_fields").Width(UnitValue.Stretch()).Height(42).Enter())
            {
                using (paper.Row("gim_name_row").Height(20).RowBetween(4).Enter())
                {
                    Origami.Checkbox(paper, "gim_enabled", allEnabled,
                        v => ApplyMultiHeaderChange(targets, "Toggle Enabled", static g => g.Enabled, static (g, x) => g.Enabled = x, v))
                        .Indeterminate(anyEnabled != allEnabled).NoLabel().Show();
                    Origami.TextField(paper, identity.ToString(), nameMixed ? "" : targets[0].Name, v =>
                    {
                        if (!string.IsNullOrWhiteSpace(v))
                            ApplyMultiHeaderChange(targets, "Change Name", static g => g.Name, static (g, x) => g.Name = x, v);
                    }).Width(UnitValue.Stretch()).Height(20).Placeholder(nameMixed ? "—" : "")
                        .Delayed().SelectAllOnFocus().Show();
                    paper.Box("gim_count").Width(UnitValue.Auto).Height(20).IsNotInteractable()
                        .Text($"({targets.Length})", font).TextColor(EditorTheme.Ink300)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                    Origami.Checkbox(paper, "gim_static", allStatic,
                        v => ApplyMultiHeaderChange(targets, "Toggle Static", static g => g.IsStatic, static (g, x) => g.IsStatic = x, v))
                        .Indeterminate(anyStatic != allStatic).LabelRight(Loc.Get("inspector.static")).Show();
                }
                using (paper.Row("gim_tag_layer").Height(20).Margin(0, 0, 2, 0).RowBetween(4).Enter())
                {
                    var tagNames = TagLayerManager.tags.ToArray();
                    int tagIndex = tagMixed ? -1 : TagLayerManager.tags.IndexOf(targets[0].Tag);
                    SelDropdown(paper, font, "gim_tag", Loc.Get("inspector.tag"), tagMixed ? "—" : targets[0].Tag,
                        tagNames, tagIndex, v =>
                        {
                            if ((uint)v < (uint)tagNames.Length)
                                ApplyMultiHeaderChange(targets, "Change Tag", static g => g.Tag, static (g, x) => g.Tag = x, tagNames[v]);
                        }, chevron: false, width: UnitValue.Stretch());

                    var allLayers = TagLayerManager.layers;
                    var layerNames = new List<string>();
                    var layerIndices = new List<int>();
                    for (int i = 0; i < allLayers.Length; i++)
                        if (!string.IsNullOrEmpty(allLayers[i])) { layerNames.Add(allLayers[i]); layerIndices.Add(i); }
                    int layerIndex = layerMixed ? -1 : layerIndices.IndexOf(targets[0].LayerIndex);
                    string layerValue = layerMixed ? "—" : layerIndex >= 0 ? layerNames[layerIndex] : "";
                    SelDropdown(paper, font, "gim_layer", Loc.Get("inspector.layer"), layerValue,
                        layerNames.ToArray(), layerIndex, v =>
                        {
                            if ((uint)v < (uint)layerIndices.Count)
                                ApplyMultiHeaderChange(targets, "Change Layer", static g => g.LayerIndex, static (g, x) => g.LayerIndex = x, layerIndices[v]);
                        }, chevron: false, width: UnitValue.Stretch());
                }
            }
        }
    }

    private static void ApplyMultiHeaderChange<T>(GameObject[] targets, string description,
        Func<GameObject, T> get, Action<GameObject, T> set, T value)
    {
        // Reject an obsolete scene/menu callback as a whole. Equal values must not
        // dirty the scene or replace a valid Redo chain with an empty edit.
        for (int i = 0; i < targets.Length; i++)
            if (targets[i].IsDisposed || !ReferenceEquals(targets[i].Scene, Scene.Current)
                || !ReferenceEquals(Undo.FindGO(targets[i].Identifier), targets[i])) return;
        var changed = new List<GameObject>(targets.Length);
        for (int i = 0; i < targets.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(get(targets[i]), value)) changed.Add(targets[i]);
        if (changed.Count > 0) Undo.ApplyGameObjectChanges(changed, description, get, set, value);
    }

    private static void DrawMultiTransform(Paper paper, XEngine.Scribe.FontFile font, IReadOnlyList<GameObject> gos)
    {
        using var numeric = Undo.BeginNumericFields(gos, paper);
        var targets = numeric.Targets!;
        paper.Box("gim_transform_header").Height(22).ChildLeft(8)
            .Text($"{EditorIcons.ArrowsUpDownLeftRight}  {Loc.Get("inspector.transform")}", font)
            .TextColor(EditorTheme.Ink500)
            .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

        DrawMultiTransformRow(paper, "gim_pos", Loc.Get("inspector.position"), targets, numeric.Identity, 0);
        DrawMultiTransformRow(paper, "gim_rot", Loc.Get("inspector.rotation"), targets, numeric.Identity, 1);
        DrawMultiTransformRow(paper, "gim_scale", Loc.Get("inspector.scale"), targets, numeric.Identity, 2);
    }

    private static Float3 MultiTransformValue(GameObject go, int property) => property switch
    {
        0 => go.Transform.LocalPosition,
        1 => go.Transform.LocalEulerAngles,
        _ => go.Transform.LocalScale,
    };

    private static float MultiTransformAxis(GameObject go, int property, int axis)
    {
        var value = MultiTransformValue(go, property);
        return axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    }

    private static void SetMultiTransformAxis(GameObject go, int property, int axis, float value)
    {
        var vector = MultiTransformValue(go, property);
        if (axis == 0) vector.X = value; else if (axis == 1) vector.Y = value; else vector.Z = value;
        if (property == 0) go.Transform.LocalPosition = vector;
        else if (property == 1) go.Transform.LocalEulerAngles = vector;
        else go.Transform.LocalScale = vector;
    }

    private static void DrawMultiTransformRow(Paper paper, string id, string label,
        IReadOnlyList<GameObject> targets, string identity, int property)
    {
        EditorGUI.Row(paper, id, label, () =>
        {
            using (paper.Row(UiId.Underscore(id, identity)).Height(UnitValue.Auto).RowBetween(6).Enter())
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    int capturedAxis = axis;
                    float value = MultiTransformAxis(targets[0], property, axis);
                    bool mixed = false;
                    for (int i = 1; i < targets.Count; i++)
                        mixed |= !value.Equals(MultiTransformAxis(targets[i], property, axis));
                    string axisLabel = axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
                    var color = axis == 0 ? Color.FromArgb(255, 251, 113, 133)
                        : axis == 1 ? Color.FromArgb(255, 74, 222, 128) : Color.FromArgb(255, 96, 165, 250);
                    Origami.NumericField(paper, $"{id}_{identity}_{axis}", value,
                        v => ApplyMultiTransformAxis(targets, property, capturedAxis, v))
                        .MixedValue(mixed).DraggableLabel(axisLabel, color, compact: true)
                        .Width(UnitValue.Stretch()).Show();
                }
            }
        });
    }

    private static void ApplyMultiTransformAxis(IReadOnlyList<GameObject> targets, int property, int axis, float value)
    {
        for (int i = 0; i < targets.Count; i++)
            if (targets[i].IsDisposed || !ReferenceEquals(targets[i].Scene, Scene.Current)
                || !ReferenceEquals(Undo.FindGO(targets[i].Identifier), targets[i])) return;
        for (int i = 0; i < targets.Count; i++)
        {
            var go = targets[i];
            float before = MultiTransformAxis(go, property, axis);
            if (before.Equals(value)) continue;
            Undo.RecordGameObjectChange(go, $"Change Transform {property}:{axis}", before, value,
                (g, v) => SetMultiTransformAxis(g, property, axis, v),
                get: g => MultiTransformAxis(g, property, axis));
            SetMultiTransformAxis(go, property, axis, value);
        }
    }

    private static void DrawMultiComponents(Paper paper, XEngine.Scribe.FontFile font, IReadOnlyList<GameObject> gos, bool debug = false)
    {
        // Component types in the first object's order, kept only if present on every selected object.
        var orderedTypes = new List<Type>();
        foreach (var c in gos[0].GetComponents<MonoBehaviour>())
        {
            if (c.HideFlags.HasFlag(HideFlags.Hide)) continue;
            if (c is RectTransform) continue; // handled by the transform row at the top
            var ct = c.GetType();
            if (!orderedTypes.Contains(ct)) orderedTypes.Add(ct);
        }

        foreach (var type in orderedTypes)
        {
            var instances = new List<object>(gos.Count);
            bool onAll = true;
            foreach (var go in gos)
            {
                MonoBehaviour? match = null;
                foreach (var c in go.GetComponents<MonoBehaviour>())
                {
                    if (c.HideFlags.HasFlag(HideFlags.Hide)) continue;
                    if (c.GetType() == type) { match = c; break; }
                }
                if (match == null) { onAll = false; break; }
                instances.Add(match);
            }
            if (!onAll) continue;

            string compId = UiId.Cat("gim_comp_", type.Name);
            string icon = GetComponentIcon((MonoBehaviour)instances[0]);

            bool allEn = true, anyEn = false;
            for (int i = 0; i < instances.Count; i++)
            {
                bool enabled = ((MonoBehaviour)instances[i]).Enabled;
                allEn &= enabled; anyEn |= enabled;
            }
            bool expanded = SectionHeader(paper, font, compId, icon, GetComponentTitle(type),
                anyEn ? EditorTheme.Ink500 : EditorTheme.Ink300,
                enabledControl: () => Origami.Checkbox(paper, UiId.Underscore(compId, "en"), allEn,
                    v =>
                    {
                        var actions = new List<(Action, Action)>(instances.Count);
                        foreach (var o in instances)
                        {
                            var c = (MonoBehaviour)o;
                            var cid = c.Identifier;
                            bool old = c.Enabled;
                            actions.Add((() => { var x = Undo.FindComponent(cid); if (x != null) { x.Enabled = old; x.OnValidate(); } },
                                        () => { var x = Undo.FindComponent(cid); if (x != null) { x.Enabled = v; x.OnValidate(); } }));
                            c.Enabled = v; c.OnValidate();
                        }
                        Undo.RegisterActionGroup("Toggle Component", actions);
                    })
                    .Small().Indeterminate(anyEn != allEn).NoLabel().Show());

            // Reflection-based multi grid (custom single-target editors are bypassed in multi mode).
            if (expanded)
            {
                using var numeric = Undo.BeginComponentNumericFields(compId, instances, paper);
                string gridId = UiId.Underscore(compId, numeric.Identity);
                if (debug)
                    Origami.PropertyGrid(paper, gridId, numeric.ComponentTargets!, EditorApplication.DebugGridConfig).Show();
                else if (type == typeof(SkinnedMeshRenderer))
                    SkinnedMeshRendererEditor.DrawMulti(paper, gridId, numeric.ComponentTargets!);
                else
                    PropertyGridUtils.DrawMulti(paper, gridId, numeric.ComponentTargets!);
            }

            Origami.Separator(paper, UiId.Underscore(compId, "sep")).Show();
        }
    }

    // ================================================================
    //  Header: Name, Enabled, Tag, Layer
    // ================================================================

    private static void DrawHeader(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        var goId = go.Identifier;

        using (paper.Row("gi_header")
            .Height(42)
            .Margin(0, 0, 4, 2)
            .Padding(6, 6, 0, 0)
            .RowBetween(4)
            .Enter())
        {
            // Icon selector (Unity's top-left "Select icon").
            using (paper.Box("gi_icon_btn")
                .Width(24).Height(24).Rounded(2)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Cursor(PaperCursor.Pointer)
                .OnClick(go, (g, _) => DrawIconMenu(paper, g))
                .Enter())
            {
                if (GameObjectIcons.HasIcon(go))
                    GameObjectIcons.Draw(paper, "gi_icon", go, size: 16f, slot: 24);
                else
                    paper.Box("gi_icon_none").Width(24).Height(24).IsNotInteractable()
                        .Text(EditorIcons.Circle, font).TextColor(EditorTheme.Ink300)
                        .FontSize(9f).Alignment(TextAlignment.MiddleCenter);
            }

            using (paper.Column("gi_header_fields").Width(UnitValue.Stretch()).Height(42).Enter())
            {
                using (paper.Row("gi_name_row").Height(20).RowBetween(4).Enter())
                {
                    Origami.Checkbox(paper, "gi_enabled", go.Enabled,
                        v => { Undo.RecordGameObjectChange(go, "Toggle Enabled", go.Enabled, v, (g, x) => g.Enabled = x); go.Enabled = v; EditorSceneManager.MarkDirty(); })
                        .NoLabel().Show();

                    Origami.TextField(paper, UiId.Cat("gi_name_", goId), go.Name, v =>
                        {
                            if (!go.IsDisposed && ReferenceEquals(Undo.FindGO(goId), go)
                                && !string.IsNullOrWhiteSpace(v) && v != go.Name)
                            {
                                Undo.RecordGameObjectChange(go, "Change Name", go.Name, v, (g, x) => g.Name = x);
                                go.Name = v;
                                EditorSceneManager.MarkDirty("Change Name");
                            }
                        })
                        .Width(UnitValue.Stretch()).Height(20).Delayed().SelectAllOnFocus()
                        .Show();

                    Origami.Checkbox(paper, "gi_static", go.IsStatic,
                        v =>
                        {
                            Undo.RecordGameObjectChange(go, "Toggle Static", go.IsStatic, v, (g, x) => g.IsStatic = x);
                            go.IsStatic = v;
                            EditorSceneManager.MarkDirty();

                            // Offer to cascade the change to the whole subtree.
                            if (go.ChildCount > 0)
                                Origami.Confirm("Apply to Children?",
                                    $"Also set Static = {v} on all child objects of \"{go.Name}\"?",
                                    () => ApplyStaticToChildren(go, v));
                        }).LabelRight(Loc.Get("inspector.static")).Show();
                }

                // Tag + Layer row (label-inside sel dropdowns)
                using (paper.Row("gi_tag_layer")
                    .Height(20)
                    .Margin(0, 0, 2, 0)
                    .RowBetween(4)
                    .Enter())
                {
                    var tagNames = TagLayerManager.tags.ToArray();
                    int tagIdx = TagLayerManager.tags.IndexOf(go.Tag);
                    if (tagIdx < 0) tagIdx = 0;
                    SelDropdown(paper, font, "gi_tag", Loc.Get("inspector.tag"), go.Tag, tagNames, tagIdx,
                        v =>
                        {
                            if (v >= 0 && v < tagNames.Length && tagNames[v] != go.Tag)
                            {
                                var newTag = tagNames[v];
                                Undo.RecordGameObjectChange(go, "Change Tag", go.Tag, newTag, (g, x) => g.Tag = x);
                                go.Tag = newTag;
                                EditorSceneManager.MarkDirty();
                            }
                        }, chevron: false, width: UnitValue.Stretch());

                    var allLayers = TagLayerManager.layers;
                    var layerNames = new List<string>();
                    var layerIndices = new List<int>();
                    for (int i = 0; i < allLayers.Length; i++)
                        if (!string.IsNullOrEmpty(allLayers[i])) { layerNames.Add(allLayers[i]); layerIndices.Add(i); }

                    int selLayer = layerIndices.IndexOf(go.LayerIndex);
                    if (selLayer < 0) selLayer = 0;
                    string layerVal = selLayer >= 0 && selLayer < layerNames.Count ? layerNames[selLayer] : "";
                    SelDropdown(paper, font, "gi_layer", Loc.Get("inspector.layer"), layerVal, layerNames.ToArray(), selLayer,
                        v =>
                        {
                            if (v >= 0 && v < layerIndices.Count && layerIndices[v] != go.LayerIndex)
                            {
                                var newIdx = layerIndices[v];
                                Undo.RecordGameObjectChange(go, "Change Layer", go.LayerIndex, newIdx, (g, x) => g.LayerIndex = x);
                                go.LayerIndex = newIdx;
                                EditorSceneManager.MarkDirty();
                            }
                        }, chevron: false, width: UnitValue.Stretch());
                }
            }
        }
    }

    /// <summary>Icon picker for a GameObject: none / label colors / custom texture asset.</summary>
    private static void DrawIconMenu(Paper paper, GameObject go)
    {
        Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
        {
            b.Header(Loc.Get("inspector.select_icon"));

            b.Item(Loc.Get("common.none"), () => SetIcon(go, 0, ""), icon: EditorIcons.Ban);

            string[] names = { Loc.Get("icon.gray"), Loc.Get("icon.blue"), Loc.Get("icon.cyan"), Loc.Get("icon.green"),
                Loc.Get("icon.yellow"), Loc.Get("icon.orange"), Loc.Get("icon.red"), Loc.Get("icon.purple") };
            for (int i = 0; i < GameObjectIcons.LabelColors.Length; i++)
            {
                int idx = i + 1;
                string label = i < names.Length ? names[i] : idx.ToString();
                b.Item(label, () => SetIcon(go, idx, ""), on: go.LabelIcon == idx && go.IconAssetGuid.Length == 0);
            }

            b.Separator();
            b.Item(Loc.Get("icon.other_texture"), () =>
                GUI.Popups.SelectorModal.Open(Loc.Get("inspector.select_icon"), typeof(Runtime.Resources.Texture2D),
                    GUI.Popups.SelectorTabs.Assets, asset =>
                    {
                        var tex = asset as Runtime.Resources.Texture2D;
                        SetIcon(go, 0, tex != null ? tex.AssetID.ToString() : "");
                    }), icon: EditorIcons.Image);
        });
    }

    private static void SetIcon(GameObject go, int labelIcon, string iconGuid)
    {
        int oldLabel = go.LabelIcon;
        string oldGuid = go.IconAssetGuid;
        Undo.RegisterAction("Set GameObject Icon",
            undo: () => { var g = Undo.FindGO(go.Identifier); if (g != null) { g.LabelIcon = oldLabel; g.IconAssetGuid = oldGuid; } },
            redo: () => { var g = Undo.FindGO(go.Identifier); if (g != null) { g.LabelIcon = labelIcon; g.IconAssetGuid = iconGuid; } });
        go.LabelIcon = labelIcon;
        go.IconAssetGuid = iconGuid;
        EditorSceneManager.MarkDirty();
    }

    /// <summary>Set <see cref="GameObject.IsStatic"/> on every descendant of <paramref name="go"/> (recorded for undo).</summary>
    private static void ApplyStaticToChildren(GameObject go, bool value)
    {
        foreach (var child in go.GetChildrenDeep())
        {
            if (child.IsStatic == value) continue;
            Undo.RecordGameObjectChange(child, "Toggle Static", child.IsStatic, value, (g, x) => g.IsStatic = x);
            child.IsStatic = value;
        }
    }

    /// <summary>
    /// Renders an auto-width label followed by the caller's control filling the remainder.
    /// Used for the Tag/Layer header where we want compact label gutters, not the inspector's
    /// fixed-width LabelWidth gutter.
    /// </summary>
    private static void DrawInlineLabeled(Paper paper, string id, string label,
        XEngine.Scribe.FontFile font, Action drawControl)
    {
        using (paper.Row(id).Width(UnitValue.Stretch()).Height(EditorTheme.RowHeight).RowBetween(4).Enter())
        {
            paper.Box(UiId.Underscore(id, "lbl"))
                .Width(UnitValue.Auto).Height(EditorTheme.RowHeight)
                .Margin(4, 4, 0, 0)
                .IsNotInteractable()
                .Text(label, font).TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSize);

            using (paper.Box(UiId.Underscore(id, "ctl")).Width(UnitValue.Stretch()).Height(EditorTheme.RowHeight).Enter())
            {
                drawControl();
            }
        }
    }

    // ================================================================
    //  Transform
    // ================================================================

    private static void DrawTransform(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        using var numeric = Undo.BeginNumericFields(go, paper);
        var t = go.Transform;
        var goId = go.Identifier;

        bool expanded = SectionHeader(paper, font, "gi_transform", EditorIcons.ArrowsUpDownLeftRight,
            Loc.Get("inspector.transform"), EditorTheme.Ink500, () =>
            {
                ComponentHeaderButton(paper, "gi_tf_reset", EditorIcons.ArrowRotateRight, () => ResetTransform(go));
                ComponentHeaderButton(paper, "gi_tf_dots", EditorIcons.EllipsisVertical, () =>
                    Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
                        b.Item(Loc.Get("inspector.reset"), () => ResetTransform(go), icon: EditorIcons.ArrowRotateRight)));
            });
        if (!expanded) return;

        // Position
        var pos = t.LocalPosition;
        EditorGUI.Row(paper, "gi_pos", Loc.Get("inspector.position"), () =>
            Origami.Float3Field(paper, UiId.Underscore("gi_pos_vf", numeric.Identity), pos, v => { Undo.RecordGameObjectChange(go, "Change Position", t.LocalPosition, v, (g, x) => g.Transform.LocalPosition = x, coalesce: true, get: static g => g.Transform.LocalPosition); t.LocalPosition = v; }).Show());

        // Rotation (as euler)
        var euler = t.LocalEulerAngles;
        EditorGUI.Row(paper, "gi_rot", Loc.Get("inspector.rotation"), () =>
            Origami.Float3Field(paper, UiId.Underscore("gi_rot_vf", numeric.Identity), euler, v => { Undo.RecordGameObjectChange(go, "Change Rotation", t.LocalEulerAngles, v, (g, x) => g.Transform.LocalEulerAngles = x, coalesce: true, get: static g => g.Transform.LocalEulerAngles); t.LocalEulerAngles = v; }).Show());

        // Scale
        var scale = t.LocalScale;
        EditorGUI.Row(paper, "gi_scale", Loc.Get("inspector.scale"), () =>
            Origami.Float3Field(paper, UiId.Underscore("gi_scale_vf", numeric.Identity), scale, v => { Undo.RecordGameObjectChange(go, "Change Scale", t.LocalScale, v, (g, x) => g.Transform.LocalScale = x, coalesce: true, get: static g => g.Transform.LocalScale); t.LocalScale = v; }).Show());
    }

    private static void ResetTransform(GameObject go)
    {
        var t = go.Transform;
        var goId = go.Identifier;
        var (oldP, oldR, oldS) = (t.LocalPosition, t.LocalEulerAngles, t.LocalScale);
        Undo.RegisterAction("Reset Transform",
            undo: () => { var g = Undo.FindGO(goId); if (g != null) { g.Transform.LocalPosition = oldP; g.Transform.LocalEulerAngles = oldR; g.Transform.LocalScale = oldS; } },
            redo: () => { var g = Undo.FindGO(goId); if (g != null) { g.Transform.LocalPosition = Float3.Zero; g.Transform.LocalEulerAngles = Float3.Zero; g.Transform.LocalScale = Float3.One; } });
        t.LocalPosition = Float3.Zero;
        t.LocalEulerAngles = Float3.Zero;
        t.LocalScale = Float3.One;
    }

    private static readonly (Float2 min, Float2 max)[,] AnchorPresets = new (Float2, Float2)[4, 4]
    {
        // Row 0: top fixed (TopLeft, TopCenter, TopRight) + horizontal-stretch top
        { (new(0f, 1f), new(0f, 1f)),     (new(0.5f, 1f), new(0.5f, 1f)),     (new(1f, 1f), new(1f, 1f)),     (new(0f, 1f), new(1f, 1f)) },
        // Row 1: middle fixed (MidLeft, MidCenter, MidRight) + horizontal-stretch middle
        { (new(0f, 0.5f), new(0f, 0.5f)), (new(0.5f, 0.5f), new(0.5f, 0.5f)), (new(1f, 0.5f), new(1f, 0.5f)), (new(0f, 0.5f), new(1f, 0.5f)) },
        // Row 2: bottom fixed (BottomLeft, BottomCenter, BottomRight) + horizontal-stretch bottom
        { (new(0f, 0f), new(0f, 0f)),     (new(0.5f, 0f), new(0.5f, 0f)),     (new(1f, 0f), new(1f, 0f)),     (new(0f, 0f), new(1f, 0f)) },
        // Row 3: vertical-stretch (left, center, right) + stretch-all
        { (new(0f, 0f), new(0f, 1f)),     (new(0.5f, 0f), new(0.5f, 1f)),     (new(1f, 0f), new(1f, 1f)),     (new(0f, 0f), new(1f, 1f)) },
    };

    private static bool ApproxEq(Float2 a, Float2 b)
        => System.Math.Abs(a.X - b.X) < 1e-4f && System.Math.Abs(a.Y - b.Y) < 1e-4f;

    private static void DrawRectTransform(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        var rt = go.RectTransform!;
        var t = go.Transform;

        paper.Box("gi_rt_header").Height(22).ChildLeft(8)
            .Text($"{EditorIcons.VectorSquare}  Rect Transform", font)
            .TextColor(EditorTheme.Ink500)
            .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleLeft);

        // Top block: 4x4 anchor preset grid on the left, position + size fields filling the rest.
        using (paper.Row("gi_rt_top").Height(UnitValue.Auto).RowBetween(8).Margin(4, 4, 2, 2).Enter())
        {
            DrawAnchorPresetGrid(paper, font, go, rt);

            using (paper.Column("gi_rt_top_right").Width(UnitValue.Stretch()).Height(UnitValue.Auto).Enter())
            {
                DrawPositionRow(paper, font, go, rt, t);
                DrawSizeRow(paper, font, go, rt);
                DrawPivotRow(paper, font, go, rt);
            }
        }


        // Anchors (Min/Max) exposed for fine-grained tweaking outside the preset grid.
        EditorGUI.Row(paper, "gi_rt_amin", "Anchor Min", () =>
        {
            Origami.Float2Field(paper, "gi_rt_amin_vf", rt.AnchorMin, v =>
            {
                Undo.Snapshot(rt);
                SetAnchorsPreservingRect(rt, v, rt.AnchorMax);
            }).Show();
        });

        EditorGUI.Row(paper, "gi_rt_amax", "Anchor Max", () =>
        {
            Origami.Float2Field(paper, "gi_rt_amax_vf", rt.AnchorMax, v =>
            {
                Undo.Snapshot(rt);
                SetAnchorsPreservingRect(rt, rt.AnchorMin, v);
            }).Show();
        });

        // Rotation (as euler) and scale come from the underlying Transform.
        var euler = t.LocalEulerAngles;
        EditorGUI.Row(paper, "gi_rt_rot", "Rotation", () =>
        {
            Origami.Float3Field(paper, "gi_rt_rot_vf", euler, v =>
            {
                Undo.RecordGameObjectChange(go, "Change Rotation", t.LocalEulerAngles, v,
                    (g, x) =>
                    {
                        g.Transform.LocalEulerAngles = x;
                        rt.MarkLayoutDirty();
                    }, coalesce: true);
                t.LocalEulerAngles = v;
                rt.MarkLayoutDirty();
            }).Show();
        });

        var scale = t.LocalScale;
        EditorGUI.Row(paper, "gi_rt_scale", "Scale", () =>
        {
            Origami.Float3Field(paper, "gi_rt_scale_vf", scale, v =>
            {
                Undo.RecordGameObjectChange(go, "Change Scale", t.LocalScale, v, (g, x) =>
                    {
                        g.Transform.LocalScale = x;
                        rt.MarkLayoutDirty();
                    },
                    coalesce: true);
                t.LocalScale = v;
                rt.MarkLayoutDirty();
            }).Show();
        });
    }

    /// <summary>
    /// Renders the 4x4 anchor preset grid. The active preset is highlighted; clicking a cell
    /// applies its (AnchorMin, AnchorMax) to the RectTransform.
    /// </summary>
    // Cached row/cell element ids for the anchor preset grid (constant strings; avoids
    // rebuilding interpolated ids every frame while a RectTransform is selected).
    private static readonly string[] AnchorPresetRowIds =
        { "gi_rt_apreset_r0", "gi_rt_apreset_r1", "gi_rt_apreset_r2", "gi_rt_apreset_r3" };
    private static readonly string[,] AnchorPresetCellIds = new string[4, 4];

    private static void DrawAnchorPresetGrid(Paper paper, XEngine.Scribe.FontFile font, GameObject go, RectTransform rt)
    {
        const float CellSize = 22f;
        const float CellGap = 2f;
        const float GridPad = 4f;
        const float GridSize = CellSize * 4 + CellGap * 3 + GridPad * 2;

        if (AnchorPresetCellIds[0, 0] == null)
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    AnchorPresetCellIds[r, c] = $"gi_rt_acell_{r}_{c}";

        using (paper.Column("gi_rt_apreset")
            .Width(GridSize).Height(GridSize)
            .BackgroundColor(EditorTheme.Neutral200)
            .BorderColor(EditorTheme.Ink100).BorderWidth(1)
            .Rounded(3)
            .ChildLeft(GridPad).ChildRight(GridPad).ChildTop(GridPad).ChildBottom(GridPad)
            .ColBetween(CellGap)
            .Enter())
        {
            for (int row = 0; row < 4; row++)
            {
                using (paper.Row(AnchorPresetRowIds[row])
                    .Height(CellSize).RowBetween(CellGap).Enter())
                {
                    for (int col = 0; col < 4; col++)
                    {
                        var preset = AnchorPresets[row, col];
                        DrawAnchorPresetCell(paper, AnchorPresetCellIds[row, col], go, rt,
                            preset.min, preset.max, CellSize);
                    }
                }
            }
        }
    }

    private static void DrawAnchorPresetCell(Paper paper, string id, GameObject go, RectTransform rt,
        Float2 minPreset, Float2 maxPreset, float cellSize)
    {
        bool isFixed = ApproxEq(minPreset, maxPreset);
        bool isActive = ApproxEq(rt.AnchorMin, minPreset) && ApproxEq(rt.AnchorMax, maxPreset);

        var bg = isActive ? EditorTheme.Purple400 : EditorTheme.Neutral300;
        var hoverBg = isActive ? EditorTheme.Purple400 : EditorTheme.Neutral400;
        var indicator = isActive ? EditorTheme.Neutral200 : EditorTheme.Purple400;

        using (paper.Box(id)
            .Width(cellSize).Height(cellSize)
            .BackgroundColor(bg)
            .Hovered.BackgroundColor(hoverBg).End()
            .BorderColor(EditorTheme.Ink100).BorderWidth(1)
            .Rounded(2)
            .OnClick((go, minPreset, maxPreset), (cap, _) =>
            {
                var (capGo, capMin, capMax) = cap;
                var r = capGo.RectTransform;
                if (r == null) return;

                // Changing the anchor preset re-parameterizes the rect but must keep it visually put,
                // so it re-solves SizeDelta/AnchoredPosition. Snapshot the full solved state both ways
                // so undo/redo restore the exact values rather than re-deriving them.
                var oldMin = r.AnchorMin; var oldMax = r.AnchorMax;
                var oldSize = r.SizeDelta; var oldPos = r.AnchoredPosition;

                SetAnchorsPreservingRect(r, capMin, capMax);

                var newMin = r.AnchorMin; var newMax = r.AnchorMax;
                var newSize = r.SizeDelta; var newPos = r.AnchoredPosition;

                Undo.RegisterAction("Change Anchor Preset",
                    undo: () => { var rr = capGo.RectTransform; if (rr != null) { rr.AnchorMin = oldMin; rr.AnchorMax = oldMax; rr.SizeDelta = oldSize; rr.AnchoredPosition = oldPos; } },
                    redo: () => { var rr = capGo.RectTransform; if (rr != null) { rr.AnchorMin = newMin; rr.AnchorMax = newMax; rr.SizeDelta = newSize; rr.AnchoredPosition = newPos; } });
            })
            .Enter())
        {
            // Inner drawable area (cell minus 2px border on each side).
            float inner = cellSize - 4f;

            if (isFixed)
            {
                // Fixed anchor: small dot positioned at the preset corner of the cell.
                // The cell is drawn in the editor's Y-down PaperUI, so flip the anchor's
                // +Y-up Y to place a Y=1 (top) anchor at the visual top of the cell.
                const float DotSize = 4f;
                float range = inner - DotSize;
                float dx = 2f + (float)minPreset.X * range;
                float dy = 2f + (1f - (float)minPreset.Y) * range;
                paper.Box(UiId.Underscore(id, "dot"))
                    .PositionType(PositionType.SelfDirected)
                    .Position(dx, dy)
                    .Width(DotSize).Height(DotSize)
                    .BackgroundColor(indicator)
                    .IsNotInteractable()
                    .Rounded(2);
            }
            else
            {
                // Stretch preset: a bar visualizing the spanned axis (or a filled square for stretch-all).
                bool stretchX = !ApproxEq(new Float2(minPreset.X, 0), new Float2(maxPreset.X, 0));
                bool stretchY = !ApproxEq(new Float2(0, minPreset.Y), new Float2(0, maxPreset.Y));

                const float BarThickness = 4f;
                float bx, by, bw, bh;

                if (stretchX && !stretchY)
                {
                    // Horizontal bar at a fixed Y flip the +Y-up anchor Y for Y-down PaperUI.
                    bx = 2f;
                    by = 2f + (1f - (float)minPreset.Y) * (inner - BarThickness);
                    bw = inner;
                    bh = BarThickness;
                }
                else if (stretchY && !stretchX)
                {
                    bx = 2f + (float)minPreset.X * (inner - BarThickness);
                    by = 2f;
                    bw = BarThickness;
                    bh = inner;
                }
                else
                {
                    // Stretch in both axes fill the cell.
                    bx = 2f;
                    by = 2f;
                    bw = inner;
                    bh = inner;
                }

                paper.Box(UiId.Underscore(id, "bar"))
                    .PositionType(PositionType.SelfDirected)
                    .Position(bx, by)
                    .Width(bw).Height(bh)
                    .BackgroundColor(indicator)
                    .IsNotInteractable()
                    .Rounded(1);
            }
        }
    }

    // Nebula axis colors, matching Origami's vector fields (X red, Y green, Z blue).
    private static readonly System.Drawing.Color AxisXColor = System.Drawing.Color.FromArgb(255, 251, 113, 133);
    private static readonly System.Drawing.Color AxisYColor = System.Drawing.Color.FromArgb(255, 74, 222, 128);
    private static readonly System.Drawing.Color AxisZColor = System.Drawing.Color.FromArgb(255, 96, 165, 250);

    private static bool AxisStretched(float anchorMin, float anchorMax) => MathF.Abs(anchorMin - anchorMax) > 1e-5f;

    // Rect edges relative to their anchors, as functions of AnchoredPosition/SizeDelta/Pivot only
    // (the anchor pixel positions cancel, so no parent rect is needed for these).
    private static float OffsetMin(float anchored, float size, float pivot) => anchored - pivot * size;
    private static float OffsetMax(float anchored, float size, float pivot) => anchored + (1f - pivot) * size;

    // Solve (AnchoredPosition, SizeDelta) for one axis so its min edge (offsetMin) becomes newMin while
    // the max edge is held - i.e. dragging the Left/Bottom field keeps the opposite edge pinned.
    private static void SolveHoldMax(float anchored, float size, float pivot, float newMin, out float outAnchored, out float outSize)
    {
        float omax = OffsetMax(anchored, size, pivot);
        outSize = omax - newMin;
        outAnchored = newMin + pivot * outSize;
    }

    // Solve so the max edge (offsetMax) becomes newMax while the min edge is held (Right/Top field).
    private static void SolveHoldMin(float anchored, float size, float pivot, float newMax, out float outAnchored, out float outSize)
    {
        float omin = OffsetMin(anchored, size, pivot);
        outSize = newMax - omin;
        outAnchored = omin + pivot * outSize;
    }

    private static void NumCell(Paper paper, string id, string label, System.Drawing.Color color, float value, Action<float> onChange)
        => Origami.NumericField<float>(paper, id, value, onChange).DraggableLabel(label, color, compact: true).Show();

    private static void ApplyPosSize(GameObject g, (Float2 pos, Float2 size) v)
    {
        var r = g.RectTransform;
        if (r == null) return;
        r.AnchoredPosition = v.pos;
        r.SizeDelta = v.size;
    }

    // Writes a new value into one axis component of AnchoredPosition/SizeDelta, coalescing the undo so
    // a scrub-drag collapses to a single step. Both are recorded together since a stretched-edge edit
    // moves position and size at once.
    private static void ApplyAxis(GameObject go, RectTransform rt, int axis, float newAnchored, float newSize, string desc)
    {
        Float2 pos = rt.AnchoredPosition, size = rt.SizeDelta;
        Float2 nPos = axis == 0 ? new Float2(newAnchored, pos.Y) : new Float2(pos.X, newAnchored);
        Float2 nSize = axis == 0 ? new Float2(newSize, size.Y) : new Float2(size.X, newSize);
        Undo.RecordGameObjectChange(go, desc, (pos, size), (nPos, nSize), ApplyPosSize, coalesce: true);
        rt.AnchoredPosition = nPos;
        rt.SizeDelta = nSize;
    }

    /// <summary>
    /// Position row. Each axis is contextual (Unity-style): a fixed axis shows Pos X / Pos Y, a
    /// stretched axis shows the edge inset (Left / Top) instead. Z always comes from Transform.LocalPosition.
    /// </summary>
    private static void DrawPositionRow(Paper paper, XEngine.Scribe.FontFile font, GameObject go, RectTransform rt, Transform t)
    {
        bool sx = AxisStretched(rt.AnchorMin.X, rt.AnchorMax.X);
        bool sy = AxisStretched(rt.AnchorMin.Y, rt.AnchorMax.Y);
        Float2 p = rt.AnchoredPosition, s = rt.SizeDelta, pv = rt.Pivot;

        EditorGUI.Row(paper, "gi_rt_pos", "Position", () =>
        {
            using (paper.Row("gi_rt_pos_r").Height(UnitValue.Auto).RowBetween(6).Enter())
            {
                if (sx)
                    NumCell(paper, "gi_rt_pos_x", "Left", AxisXColor, OffsetMin(p.X, s.X, pv.X), nv =>
                    { SolveHoldMax(p.X, s.X, pv.X, nv, out float a, out float sz); ApplyAxis(go, rt, 0, a, sz, "Rect Left"); });
                else
                    NumCell(paper, "gi_rt_pos_x", "X", AxisXColor, p.X, nv =>
                        ApplyAxis(go, rt, 0, nv, rt.SizeDelta.X, "Rect PosX"));

                if (sy)
                    NumCell(paper, "gi_rt_pos_y", "Top", AxisYColor, -OffsetMax(p.Y, s.Y, pv.Y), nv =>
                    { SolveHoldMin(p.Y, s.Y, pv.Y, -nv, out float a, out float sz); ApplyAxis(go, rt, 1, a, sz, "Rect Top"); });
                else
                    NumCell(paper, "gi_rt_pos_y", "Y", AxisYColor, p.Y, nv =>
                        ApplyAxis(go, rt, 1, nv, rt.SizeDelta.Y, "Rect PosY"));

                NumCell(paper, "gi_rt_pos_z", "Z", AxisZColor, t.LocalPosition.Z, nv =>
                {
                    var nl = new Float3(t.LocalPosition.X, t.LocalPosition.Y, nv);
                    Undo.RecordGameObjectChange(go, "Rect PosZ", t.LocalPosition, nl, (g, x) => g.Transform.LocalPosition = x, coalesce: true);
                    t.LocalPosition = nl;
                });
            }
        }, labelWidth: EditorTheme.LabelWidth / 2f);
    }

    /// <summary>
    /// Size row. Contextual per axis: a fixed axis shows Width / Height, a stretched axis shows the
    /// opposite edge inset (Right / Bottom). An empty third column keeps it aligned with the Z field above.
    /// </summary>
    private static void DrawSizeRow(Paper paper, XEngine.Scribe.FontFile font, GameObject go, RectTransform rt)
    {
        bool sx = AxisStretched(rt.AnchorMin.X, rt.AnchorMax.X);
        bool sy = AxisStretched(rt.AnchorMin.Y, rt.AnchorMax.Y);
        Float2 p = rt.AnchoredPosition, s = rt.SizeDelta, pv = rt.Pivot;

        EditorGUI.Row(paper, "gi_rt_size", "Size", () =>
        {
            using (paper.Row("gi_rt_size_r").Height(UnitValue.Auto).RowBetween(6).Enter())
            {
                if (sx)
                    NumCell(paper, "gi_rt_size_x", "Right", AxisXColor, -OffsetMax(p.X, s.X, pv.X), nv =>
                    { SolveHoldMin(p.X, s.X, pv.X, -nv, out float a, out float sz); ApplyAxis(go, rt, 0, a, sz, "Rect Right"); });
                else
                    NumCell(paper, "gi_rt_size_x", "W", AxisXColor, s.X, nv =>
                        ApplyAxis(go, rt, 0, rt.AnchoredPosition.X, nv, "Rect Width"));

                if (sy)
                    NumCell(paper, "gi_rt_size_y", "Bottom", AxisYColor, OffsetMin(p.Y, s.Y, pv.Y), nv =>
                    { SolveHoldMax(p.Y, s.Y, pv.Y, nv, out float a, out float sz); ApplyAxis(go, rt, 1, a, sz, "Rect Bottom"); });
                else
                    NumCell(paper, "gi_rt_size_y", "H", AxisYColor, s.Y, nv =>
                        ApplyAxis(go, rt, 1, rt.AnchoredPosition.Y, nv, "Rect Height"));

                paper.Box("gi_rt_size_pad").Width(UnitValue.Stretch()).Height(1).IsNotInteractable();
            }
        }, labelWidth: EditorTheme.LabelWidth / 2f);
    }

    private static void DrawPivotRow(Paper paper, XEngine.Scribe.FontFile font, GameObject go, RectTransform rt)
    {

        EditorGUI.Row(paper, "gi_rt_pivot", "Pivot", () =>
        {
            Origami.Float2Field(paper, "gi_rt_pivot_vf", rt.Pivot,v =>
            {
                Undo.RecordGameObjectChange(go, "Change Pivot", rt.Pivot, v,
                    (g, x) => { var r = g.RectTransform; if (r != null) r.Pivot = x; }, coalesce: true);
                rt.Pivot = v;
            }).Show();
        }, labelWidth: EditorTheme.LabelWidth/2f);

    }

    // ================================================================
    //  Components
    // ================================================================

    private static void DrawComponents(Paper paper, XEngine.Scribe.FontFile font, GameObject go, bool debug = false)
    {
        if (!Application.IsPlaying)
            EnsureUniversalAdditionalData(go);

        go.GetComponents(s_drawComponents);

        // Component drag-to-reorder: while a component of this same GameObject is being dragged,
        // insertion zones appear between the sections (Unity-style blue line drop targets).
        bool dragActive = DragDrop.IsDragging || DragDrop.IsDropFrame;
        var dragPayload = dragActive ? DragDrop.GetPayload<ComponentDragPayload>() : null;
        bool reorderZones = dragPayload is { } dp && dp.GameObject == go && !Application.IsPlaying;
        int visibleIdx = 0;

        for (int i = 0; i < s_drawComponents.Count; i++)
        {
            var comp = s_drawComponents[i];
            if (comp.HideFlags.HasFlag(HideFlags.Hide)) continue;
            if (comp is RectTransform) continue; // drawn at the top in place of Transform

            if (reorderZones)
                DrawReorderZone(paper, go, visibleIdx);

            string compId = UiId.Cat("gi_comp_", comp.Identifier);
            string compName = GetComponentTitle(comp.GetType());
            string icon = GetComponentIcon(comp);

            int capturedI = i;
            bool expanded = SectionHeader(paper, font, compId, icon, compName,
                comp.Enabled ? EditorTheme.Ink500 : EditorTheme.Ink300,
                trailing: () =>
                {
                    // Presets: apply an existing preset or capture the current values as a new one.
                    var presetComp = comp;
                    ComponentHeaderButton(paper, UiId.Underscore(compId, "preset"), EditorIcons.Sliders, () =>
                        BuildPresetMenu(paper, presetComp));

                    ComponentHeaderButton(paper, UiId.Underscore(compId, "gear"), EditorIcons.EllipsisVertical, () =>
                        Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
                            BuildComponentContextMenu(b, go, comp, capturedI)));
                },
                onDragStart: () => DragDrop.StartDrag(new ComponentDragPayload(go, comp)),
                enabledControl: () => Origami.Checkbox(paper, UiId.Underscore(compId, "en"), comp.Enabled,
                    v => { var old = comp.Enabled; var cId = comp.Identifier; Undo.RegisterAction("Toggle Component", () => { var c = Undo.FindComponent(cId); if (c != null) { c.Enabled = old; c.OnValidate(); } }, () => { var c = Undo.FindComponent(cId); if (c != null) { c.Enabled = v; c.OnValidate(); } }); comp.Enabled = v; comp.OnValidate(); })
                    .Small().NoLabel().Show());

            if (expanded)
            {
                // Set overridden field names for PropertyGrid highlighting
                if (go.IsPrefabInstance)
                {
                    string goPath = PrefabUtility.BuildGOPath(go);
                    int compIdx = i;
                    string pathPrefix = string.IsNullOrEmpty(goPath)
                        ? $"c{compIdx}."
                        : $"{goPath}.c{compIdx}.";

                    s_overriddenFields.Clear();
                    // Overrides are stored on the instance root with root-relative paths.
                    var overrideHost = PrefabUtility.GetPrefabInstanceRoot(go) ?? go;
                    foreach (var ov in overrideHost.PrefabOverrides)
                    {
                        if (ov.Path.StartsWith(pathPrefix))
                            s_overriddenFields.Add(ov.Path[pathPrefix.Length..].Split('.')[0]);
                    }
                    PropertyGridUtils.OverriddenFields = s_overriddenFields.Count > 0 ? s_overriddenFields : null;
                }

                // Component body use custom editor or default PropertyGrid. A broken custom editor, a
                // throwing property getter, or a bad [Button] must not take down the whole Inspector.
                // Contain it per component (finally keeps OverriddenFields from leaking to the next).
                try
                {
                    using var numeric = Undo.BeginNumericFields(comp, paper);
                    if (debug)
                    {
                        // Debug mode: raw field grid (private fields included, read-only unless
                        // [SerializeField]); custom component editors are bypassed.
                        Origami.PropertyGrid(paper, compId, comp, EditorApplication.DebugGridConfig)
                            .Overrides(PropertyGridUtils.OverriddenFields)
                            .Show();
                    }
                    else
                    {
                        var customEditor = EditorRegistries.GetCustomEditor(comp.GetType());
                        if (customEditor != null)
                            customEditor.OnGUI(paper, compId, comp);
                        else
                            PropertyGridUtils.Draw(paper, compId, comp);
                    }

                    // Draw [Button] attributed methods
                    DrawButtonMethods(paper, UiId.Underscore(compId, "btns"), comp);
                }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"[Inspector] Drawing {comp.GetType().Name} threw and was skipped: {ex.Message}");
                }
                finally
                {
                    PropertyGridUtils.OverriddenFields = null;
                }
            }

            // Echo-serialize only when the selection or scene actually changed — idle inspector
            // frames reuse the stored PrefabOverrides list for highlighting.
            if (go.IsPrefabInstance && s_detectPrefabOverridesThisDraw)
                PrefabUtility.DetectComponentOverrides(go, comp);

            EditorGUI.Divider(paper, UiId.Underscore(compId, "sep"), 6f);
            visibleIdx++;
        }

        if (reorderZones)
            DrawReorderZone(paper, go, visibleIdx);
    }

    /// <summary>An insertion drop target between component sections while reordering.</summary>
    private static void DrawReorderZone(Paper paper, GameObject go, int insertIndex)
    {
        using (paper.Box(UiId.Cat("gi_reorder_", insertIndex))
            .Width(UnitValue.Stretch()).Height(12)
            .Enter())
        {
            bool hovered = paper.IsParentHovered;
            paper.Box(UiId.Underscore(UiId.Cat("gi_reorder_", insertIndex), "ln"))
                .Width(UnitValue.Stretch()).Height(2)
                .Margin(0, 0, 5, 5)
                .BackgroundColor(hovered ? EditorTheme.Purple400 : Color.FromArgb(40, EditorTheme.Purple400))
                .Rounded(1).IsNotInteractable();

            var payload = DragDrop.AcceptDrop<ComponentDragPayload>(hovered);
            if (payload != null && payload.GameObject == go)
                MoveComponentTo(go, payload.Component, insertIndex);
        }
    }

    /// <summary>
    /// Move <paramref name="comp"/> so it sits at <paramref name="insertBeforeVisible"/> among the
    /// visible (non-hidden, non-RectTransform) components; Transform stays pinned at index 0.
    /// Undoable.
    /// </summary>
    private static void MoveComponentTo(GameObject go, MonoBehaviour comp, int insertBeforeVisible)
    {
        var visible = new List<MonoBehaviour>();
        go.GetComponents(visible);
        for (int i = visible.Count - 1; i >= 0; i--)
        {
            if (visible[i].HideFlags.HasFlag(HideFlags.Hide) || visible[i] is RectTransform)
                visible.RemoveAt(i);
        }

        int desired = insertBeforeVisible >= visible.Count
            ? go._components.Count
            : go._components.IndexOf(visible[insertBeforeVisible]);
        int oldIdx = go._components.IndexOf(comp);
        if (desired < 0 || oldIdx < 0 || ReferenceEquals(comp, go.Transform)) return;

        // Insertion positions above the dragged component land one slot earlier after removal.
        int newIdx = oldIdx < desired ? desired - 1 : desired;
        if (newIdx < 1) newIdx = 1;
        if (newIdx == oldIdx) return;

        var compId = comp.Identifier;
        Undo.RegisterAction("Move Component",
            () => { var c = Undo.FindComponent(compId); c?.SetSiblingIndex(oldIdx); },
            () => { var c = Undo.FindComponent(compId); c?.SetSiblingIndex(newIdx); });
        comp.SetSiblingIndex(newIdx);
    }

    private static void BuildComponentContextMenu(ContextBuilder builder, GameObject go, MonoBehaviour comp, int index)
    {
        builder.Item(Loc.Get("inspector.reset"), () =>
        {
            ComponentClipboard.Reset(comp);
            EditorSceneManager.MarkDirty();
        }, icon: EditorIcons.ArrowsRotate);

        builder.Separator();

        builder.Item(Loc.Get("inspector.copy_component"), () => ComponentClipboard.Copy(comp),
            icon: EditorIcons.Copy);
        builder.Item(Loc.Get("inspector.paste_component_as_new"),
            () => { ComponentClipboard.PasteAsNew(go); EditorSceneManager.MarkDirty(); },
            icon: EditorIcons.Paste, enabled: ComponentClipboard.CanPasteAsNew);
        builder.Item(Loc.Get("inspector.paste_component_values"),
            () => { ComponentClipboard.PasteValues(comp); EditorSceneManager.MarkDirty(); },
            icon: EditorIcons.Paste, enabled: ComponentClipboard.CanPasteValues(comp));

        builder.Separator();

        // Menu eligibility is a read-only query. CanDestroy also reports an error,
        // so calling it every repaint floods the Console for required components.
        bool canRemove = go.IsDisposed || !go.IsComponentRequired(comp, out _);
        // Block removing prefab components in editor
        if (go.IsPrefabInstance && go.PrefabComponentCount >= 0)
        {
            int compIdx = go._components.IndexOf(comp);
            if (compIdx >= 0 && compIdx < go.PrefabComponentCount)
                canRemove = false;
        }
        builder.Item(Loc.Get("inspector.remove_component"), () =>
        {
            var serialized = Echo.Serializer.Serialize(comp.GetType(), comp);
            var compType = comp.GetType();
            var compId = comp.Identifier;
            var compIndex = index;
            var goId = go.Identifier;
            Undo.RegisterAction("Remove Component",
                undo: () =>
                {
                    var g = Undo.FindGO(goId);
                    if (g == null) return;
                    var restored = Echo.Serializer.Deserialize(serialized, compType) as MonoBehaviour;
                    if (restored != null)
                    {
                        restored.Identifier = compId;
                        g.AddComponent(restored);
                        restored.SetSiblingIndex(compIndex);
                    }
                },
                redo: () =>
                {
                    var g = Undo.FindGO(goId);
                    if (g == null) return;
                    var c = g.GetComponentByIdentifier(compId);
                    if (c != null) g.RemoveComponent(c);
                });
            go.RemoveComponent(comp);
        }, icon: EditorIcons.Trash, enabled: canRemove);

        builder.Separator();

        var moveCompId = comp.Identifier;
        builder.Item(Loc.Get("inspector.move_up"), () =>
        {
            if (index > 0)
            {
                var oldIdx = index; var newIdx = index - 1;
                Undo.RegisterAction("Move Component Up",
                    () => { var c = Undo.FindComponent(moveCompId); c?.SetSiblingIndex(oldIdx); },
                    () => { var c = Undo.FindComponent(moveCompId); c?.SetSiblingIndex(newIdx); });
                comp.SetSiblingIndex(newIdx);
            }
        }, icon: EditorIcons.ArrowUp, enabled: index > 0);

        builder.Item(Loc.Get("inspector.move_down"), () =>
        {
            var oldIdx = index; var newIdx = index + 1;
            Undo.RegisterAction("Move Component Down",
                () => { var c = Undo.FindComponent(moveCompId); c?.SetSiblingIndex(oldIdx); },
                () => { var c = Undo.FindComponent(moveCompId); c?.SetSiblingIndex(newIdx); });
            comp.SetSiblingIndex(newIdx);
        }, icon: EditorIcons.ArrowDown);

        builder.Separator();

        builder.Item(Loc.Get("inspector.pop_out"), () =>
        {
            ComponentPopoutPanel.PopOut(go, comp);
        }, icon: EditorIcons.ArrowUpRightFromSquare);
    }

    /// <summary>
    /// Preset menu for a component: apply a matching preset, or capture current values as a new
    /// .preset asset. Per-type defaults are managed in the preset asset's own inspector.
    /// </summary>
    private static void BuildPresetMenu(Paper paper, MonoBehaviour comp)
    {
        Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
        {
            b.Header(Loc.Get("presets.preset"));

            var presets = Presets.PresetUtility.FindPresets(comp.GetType());
            if (presets.Count > 0)
            {
                foreach (var (entry, preset) in presets)
                {
                    var capturedPreset = preset;
                    string file = entry.Path;
                    string name = System.IO.Path.GetFileNameWithoutExtension(file);
                    b.Item(name,
                        () =>
                        {
                            Presets.PresetUtility.ApplyToComponent(capturedPreset, comp);
                            EditorSceneManager.MarkDirty();
                        }, icon: EditorIcons.Sliders);
                }
            }
            else
            {
                b.Item(Loc.Get("presets.none_found"), () => { }, enabled: false);
            }

            b.Separator();
            b.Item(Loc.Get("presets.create_new"), () =>
                Presets.PresetUtility.CreatePreset(comp, AssetCreateMenu.GetCurrentFolder()), icon: EditorIcons.Plus);
        });
    }

    // ================================================================
    //  [Button] Methods
    // ================================================================

    public static void DrawButtonMethods(Paper paper, string id, MonoBehaviour comp)
    {
        var methods = comp.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        int btnIdx = 0;
        foreach (var method in methods)
        {
            var btnAttr = method.GetCustomAttribute<ButtonAttribute>();
            if (btnAttr == null) continue;
            if (method.GetParameters().Length > 0) continue; // Only parameterless methods

            string label = btnAttr.Label ?? PropertyGridUtils.NicifyName(method.Name);
            Origami.Button(paper, UiId.Cat(UiId.Underscore(id, ""), btnIdx), label, () => { method.Invoke(comp, null); }).Show();
            btnIdx++;
        }
    }

    // ================================================================
    //  Embedded materials (Unity-style, below Add Component)
    // ================================================================

    private static readonly List<Material> s_embeddedMaterials = new();

    /// <summary>Unique materials referenced by MeshRenderer / SkinnedMeshRenderer on <paramref name="go"/>.</summary>
    public static void CollectRendererMaterials(GameObject go, List<Material> dest)
    {
        dest.Clear();
        if (go == null) return;
        AppendUniqueMaterials(go.GetComponent<MeshRenderer>()?.Materials, dest);
        AppendUniqueMaterials(go.GetComponent<SkinnedMeshRenderer>()?.Materials, dest);
    }

    private static void AppendUniqueMaterials(List<AssetRef<Material>>? refs, List<Material> dest)
    {
        if (refs == null) return;
        for (int i = 0; i < refs.Count; i++)
        {
            // AssetRef is a struct; EnsureLoaded caches on the copy, then write back.
            var aref = refs[i];
            aref.EnsureLoaded();
            refs[i] = aref;
            Material? mat = aref.Res;
            if (mat == null) continue;
            bool seen = false;
            for (int j = 0; j < dest.Count; j++)
            {
                if (ReferenceEquals(dest[j], mat)) { seen = true; break; }
                if (mat.AssetID != Guid.Empty && dest[j].AssetID == mat.AssetID) { seen = true; break; }
            }
            if (!seen) dest.Add(mat);
        }
    }

    private static void DrawEmbeddedMaterials(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        CollectRendererMaterials(go, s_embeddedMaterials);
        if (s_embeddedMaterials.Count == 0) return;

        EditorGUI.Divider(paper, "gi_sep_mats", 6f);
        for (int i = 0; i < s_embeddedMaterials.Count; i++)
        {
            Material mat = s_embeddedMaterials[i];
            string shaderName = mat.Shader != null ? mat.Shader.Name : "None";
            string title = string.IsNullOrEmpty(mat.Name) ? shaderName : $"{mat.Name}  ({shaderName})";
            string sid = $"gi_mat_{go.Identifier}_{i}_{mat.AssetID:N}";
            if (!SectionHeader(paper, font, sid, EditorIcons.Palette, title, EditorTheme.Ink500))
                continue;

            using (paper.Column(UiId.Underscore(sid, "body")).Padding(8, 8, 4, 4).Enter())
                MaterialEditorGUI.Draw(paper, sid, mat);
        }
    }

    // ================================================================
    //  Add Component
    // ================================================================

    private static void DrawAddComponentButton(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        using (paper.Row("gi_add_comp_row").Height(28).ChildLeft(20).ChildRight(20).Enter())
        {
            var trigger = paper.Box("gi_add_comp")
                .Height(28).Rounded(4)
                .BackgroundColor(EditorTheme.Ink100)
                .Hovered.BackgroundColor(EditorTheme.Ink200).End()
                .OnClick(go, (g, _) => ToggleAddComponentPopup(g));

            using (trigger.Enter())
            {
                var trigHandle = paper.CurrentParent;

                paper.Box("gi_add_comp_lbl")
                    .Width(UnitValue.Stretch()).Height(28).IsNotInteractable()
                    .Text($"{EditorIcons.Plus}  {Loc.Get("inspector.add_component")}", font)
                    .TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);

                if (_addComponentOpen && _addComponentTarget == go)
                {
                    if (paper.IsKeyPressed(PaperKey.Escape))
                        CloseAddComponentPopup();
                    else
                    {
                        RenderAddComponentBackdrop(paper);
                        RenderAddComponentPopover(paper, trigHandle);
                    }
                }
            }
        }
    }

    // ================================================================
    //  Prefab Header
    // ================================================================

    private static void DrawPrefabHeader(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        var root = PrefabUtility.GetPrefabInstanceRoot(go);
        bool isRoot = PrefabUtility.IsInstanceRoot(go);
        bool isNested = PrefabUtility.IsNestedPrefabRoot(go);
        bool hasOverrides = PrefabUtility.HasAnyOverrides(go);

        var entry = EditorAssetBackend.Instance?.GetEntry(go.PrefabAssetId);
        bool isMissing = entry == null;
        string prefabName = isMissing ? Loc.Get("inspector.missing") : System.IO.Path.GetFileNameWithoutExtension(entry!.Path);

        string label = isMissing ? Loc.Get("inspector.missing_prefab")
            : isNested ? Loc.Get("inspector.nested_prefab", new { name = prefabName })
            : Loc.Get("inspector.prefab", new { name = prefabName });

        var barColor = isMissing ? Color.FromArgb(40, EditorTheme.Red300) : Color.FromArgb(40, EditorTheme.Purple400);
        var borderColor = isMissing ? EditorTheme.Red300 : EditorTheme.Purple300;
        var textColor = isMissing ? EditorTheme.Red300 : EditorTheme.Purple400;

        // Entire prefab section in a purple-tinted container
        using (paper.Column("gi_prefab_section")
            .Height(UnitValue.Auto)
            .BackgroundColor(barColor)
            .BorderColor(borderColor).BorderWidth(1)
            .Rounded(4).Margin(0, 4, 0, 4)
            .Padding(4, 4, 4, 4)
            .Enter())
        {
            // Top row: label + buttons
            using (paper.Row("gi_prefab_bar")
                .Height(24).RowBetween(4)
                .Enter())
            {
                paper.Box("gi_prefab_icon")
                    .Width(UnitValue.Stretch()).Height(24)
                    .Text($"{EditorIcons.Cubes}  {label}", font)
                    .TextColor(textColor)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                if (!isMissing)
                {
                    Origami.Button(paper, "gi_prefab_select", Loc.Get("inspector.select"), () => { Selection.Ping(go.PrefabAssetId); }).Width(55).Show();

                    if (isRoot && hasOverrides)
                    {
                        Origami.Button(paper, "gi_prefab_revert", Loc.Get("inspector.revert"), () => { if (root != null) PrefabUtility.RevertOverrides(root); }).Width(55).Show();

                        Origami.Button(paper, "gi_prefab_apply", Loc.Get("inspector.apply"), () => { if (root != null) PrefabUtility.ApplyOverrides(root); }).Width(50).Show();
                    }
                }
            }

            // Overrides list inline
            if (hasOverrides)
            {
                paper.Box("gi_prefab_ov_sep").Height(1)
                    .BackgroundColor(borderColor).Margin(0, 2, 0, 2);

                DrawOverridesContent(paper, font, go);
            }
        }
    }

    private static void DrawOverridesContent(Paper paper, XEngine.Scribe.FontFile font, GameObject go)
    {
        float fs = EditorTheme.FontSize;
        // Overrides for the whole prefab instance are stored on its root.
        go = PrefabUtility.GetPrefabInstanceRoot(go) ?? go;
        var overrides = go.PrefabOverrides;

        using (paper.Column("gi_prefab_ov_list")
            .Height(UnitValue.Auto)
            .Margin(8, 0, 4, 4)
            .Enter())
        {
            for (int i = 0; i < overrides.Count; i++)
            {
                int idx = i;
                var ov = overrides[i];

                using (paper.Row(UiId.Cat("gi_ov_", i))
                    .Height(EditorTheme.RowHeight)
                    .BackgroundColor(EditorTheme.Neutral300)
                    .Rounded(3).Margin(0, 0, 0, 1)
                    .ChildLeft(6).RowBetween(4)
                    .Enter())
                {
                    // Override path
                    paper.Box(UiId.Cat("gi_ov_path_", i))
                        .Width(UnitValue.Stretch()).Height(EditorTheme.RowHeight)
                        .Text(ov.Path, font).TextColor(EditorTheme.Purple400)
                        .FontSize(fs - 2).Alignment(TextAlignment.MiddleLeft);

                    // Revert single
                    paper.Box(UiId.Cat("gi_ov_revert_", i))
                        .Width(50).Height(EditorTheme.RowHeight).Rounded(3)
                        .Hovered.BackgroundColor(EditorTheme.Ink200).End()
                        .Text(Loc.Get("inspector.revert"), font).TextColor(EditorTheme.Ink400)
                        .FontSize(fs - 2).Alignment(TextAlignment.MiddleCenter)
                        .OnClick((go, idx), (cap, _) =>
                        {
                            if (cap.idx < cap.go.PrefabOverrides.Count)
                            {
                                var overridePath = cap.go.PrefabOverrides[cap.idx].Path;
                                PrefabUtility.RevertSingleOverride(cap.go, overridePath);
                            }
                        });

                    // Apply single
                    paper.Box(UiId.Cat("gi_ov_apply_", i))
                        .Width(45).Height(EditorTheme.RowHeight).Rounded(3)
                        .Hovered.BackgroundColor(EditorTheme.Ink200).End()
                        .Text(Loc.Get("inspector.apply"), font).TextColor(EditorTheme.Ink400)
                        .FontSize(fs - 2).Alignment(TextAlignment.MiddleCenter)
                        .OnClick((go, idx), (cap, _) =>
                        {
                            if (cap.idx < cap.go.PrefabOverrides.Count)
                            {
                                var singleOv = cap.go.PrefabOverrides[cap.idx];
                                PrefabUtility.ApplySingleOverride(cap.go, singleOv);
                            }
                        });
                }
            }
        }
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static string GetComponentIcon(MonoBehaviour comp) => EditorRegistries.GetComponentIcon(comp);

    // ================================================================
    //  Anchor editing that preserves screen position
    // ================================================================

    /// <summary>
    /// Change a RectTransform's anchors while keeping its on-screen rect fixed: the anchor reference
    /// shifts and <see cref="RectTransform.AnchoredPosition"/> / <see cref="RectTransform.SizeDelta"/>
    /// are back-solved against the parent rect so the element does not move (Unity-style).
    /// </summary>
    private static void SetAnchorsPreservingRect(RectTransform rt, Float2 newMin, Float2 newMax)
    {
        Rect world = rt.ComputedRect;
        Rect parent = GetParentRect(rt);

        rt.AnchorMin = newMin;
        rt.AnchorMax = newMax;

        // Without a laid-out rect (parent or self) there is nothing to preserve; just apply anchors.
        if (parent.Size.X <= 0 || parent.Size.Y <= 0 || world.Size.X <= 0 || world.Size.Y <= 0)
            return;

        SolveAxis(parent.Min.X + newMin.X * parent.Size.X, parent.Min.X + newMax.X * parent.Size.X,
            world.Min.X, world.Max.X, rt.Pivot.X, out float sizeX, out float posX);
        SolveAxis(parent.Min.Y + newMin.Y * parent.Size.Y, parent.Min.Y + newMax.Y * parent.Size.Y,
            world.Min.Y, world.Max.Y, rt.Pivot.Y, out float sizeY, out float posY);

        rt.SizeDelta = new Float2(sizeX, sizeY);
        rt.AnchoredPosition = new Float2(posX, posY);
    }

    // Inverse of RectTransform.ComputeRect for one axis (single formula covering both fixed and
    // stretch anchors, matching ComputeRect: size = anchorSpan + SizeDelta, min = anchorMin +
    // AnchoredPosition - Pivot*SizeDelta). Given the target span [rMin,rMax] and the anchor pixel
    // positions, solve the SizeDelta/AnchoredPosition that reproduce it.
    private static void SolveAxis(float aMinPx, float aMaxPx,
        float rMin, float rMax, float pivot, out float size, out float pos)
    {
        size = (rMax - rMin) - (aMaxPx - aMinPx);
        pos = rMin - aMinPx + pivot * size;
    }

    // The rect this element is laid out inside: the parent RectTransform's computed rect, or the
    // canvas root rect when the element is a direct child of the canvas.
    private static Rect GetParentRect(RectTransform rt)
    {
        var canvas = rt.GameObject.GetComponentInParent<GameCanvas>(includeSelf: true);
        var parentGo = rt.GameObject.Parent;
        var parentRt = parentGo?.RectTransform;
        if (parentRt != null && parentGo != canvas?.GameObject &&
            parentRt.ComputedRect.Size.X > 0 && parentRt.ComputedRect.Size.Y > 0)
            return parentRt.ComputedRect;
        return canvas?.RootRect ?? default;
    }

    // ================================================================
    //  Add Component Popup
    // ================================================================

    private struct ComponentEntry
    {
        public string Path;     // Full path e.g. "Physics/Colliders/Box Collider"
        public string Category; // e.g. "Physics/Colliders"
        public string Name;     // e.g. "Box Collider"
        public string Icon;
        public Type Type;
    }

    private static bool _addComponentOpen;
    private static GameObject? _addComponentTarget;
    private static string _addComponentSearch = "";
    private static List<string> _addComponentNavStack = [];
    private static List<ComponentEntry>? _cachedComponents;

    // Matches Origami's DropdownBuilder default popover cap (Widgets/Dropdown.cs), so the Add
    // Component popover scrolls the same way any other dropdown in the editor does.
    private const float PopoverMaxListHeight = 320f;

    /// <summary>
    /// Drop the cached component list (which holds every MonoBehaviour <see cref="Type"/>,
    /// including user ones) whenever script assemblies change. In particular, a built-in package
    /// can be compiled after the first editor registry scan, so retaining the prior list would
    /// leave newly supplied controls out of Add Component until the next reload.
    /// </summary>
    [Runtime.OnAssemblyUnload]
    public static void ClearAddComponentCache()
    {
        _cachedComponents = null;
        _addComponentTarget = null;
        _addComponentNavStack = [];
        // Hot-reload: the per-frame draw scratch list is only cleared at the start of the next
        // Draw call, but the reload GC loop runs before that - drop the last frame's live user
        // MonoBehaviours so they cannot pin the collectible ALC.
        s_drawComponents.Clear();
        PropertyGridRenderer.ClearTransientCaches();
    }

    [Runtime.OnAssemblyLoad]
    private static void ClearAddComponentCacheAfterAssemblyLoad() => _cachedComponents = null;

    private static void ToggleAddComponentPopup(GameObject target)
    {
        if (_addComponentOpen && _addComponentTarget == target)
        {
            CloseAddComponentPopup();
            return;
        }

        _addComponentTarget = target;
        _addComponentSearch = "";
        _addComponentNavStack = [];
        _cachedComponents ??= GatherComponents();
        _addComponentOpen = true;
    }

    private static void CloseAddComponentPopup()
    {
        _addComponentOpen = false;
        _addComponentTarget = null;
    }

    // Fullscreen, invisible click-catcher so clicking anywhere outside the popover closes it -
    // the same click-outside behaviour Origami's dropdowns use (see DropdownInternal.RenderBackdrop).
    private static void RenderAddComponentBackdrop(Paper paper)
    {
        paper.Box("gi_acp_backdrop")
            .PositionType(PositionType.SelfDirected)
            .Position(-9999, -9999)
            .Size(99999, 99999)
            .Layer(Layer.Overlay)
            .StopEventPropagation()
            .OnClick(0, (_, _) => CloseAddComponentPopup());
    }

    // Popover anchored directly below the Add Component button, styled like Origami's dropdown
    // popovers - same background, border, shadow, rounding and row hover, sourced from EditorTheme.
    private static void RenderAddComponentPopover(Paper paper, ElementHandle trigHandle)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        float triggerWidth = trigHandle.Data.LayoutRect.Size.X > 0 ? (float)trigHandle.Data.LayoutRect.Size.X : 280f;
        float triggerHeight = trigHandle.Data.LayoutRect.Size.Y > 0 ? (float)trigHandle.Data.LayoutRect.Size.Y : 28f;

        const float padX = 5f, padY = 5f, searchH = 28f, searchGap = 4f;

        using (paper.Column("gi_acp_pop")
            .PositionType(PositionType.SelfDirected)
            .Position(0, triggerHeight + 4f)
            .Width(triggerWidth)
            .Height(UnitValue.Auto)
            .BackgroundColor(EditorTheme.Popover)
            .BorderColor(EditorTheme.BorderStrong).BorderWidth(1)
            .DropShadow(0, 14, 40, -6, EditorTheme.Shadow)
            .Rounded(EditorTheme.Roundness + 2f)
            .Padding(padX, padX, padY, padY)
            .ColBetween(searchGap)
            .HookToParent()
            .Layer(Layer.Topmost)
            .ClampToScreen()
            .StopEventPropagation()
            .Enter())
        {
            using (paper.Row("gi_acp_search_row").Height(searchH).Enter())
            {
                Origami.SearchField(paper, "gi_acp_search", _addComponentSearch, v => _addComponentSearch = v, Loc.Get("popup.search_components")).Show();
            }

            var components = _cachedComponents ?? [];

            Origami.ScrollView(paper, "gi_acp_scroll", triggerWidth - padX * 2, PopoverMaxListHeight)
                .Padding(0)
                .Body(() =>
            {
                if (!string.IsNullOrEmpty(_addComponentSearch))
                    DrawAddComponentSearchResults(paper, font, components);
                else
                    DrawAddComponentBrowseLevel(paper, font, components);
            });
        }
    }

    // Flat, globally-filtered list shown while the search box has text (ignores current folder).
    private static void DrawAddComponentSearchResults(Paper paper, XEngine.Scribe.FontFile font, List<ComponentEntry> components)
    {
        var filtered = components.Where(c =>
            c.Name.Contains(_addComponentSearch, StringComparison.OrdinalIgnoreCase) ||
            c.Path.Contains(_addComponentSearch, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            paper.Box("acp_empty").Height(40)
                .Text(Loc.Get("popup.no_components"), font)
                .TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
            return;
        }

        for (int i = 0; i < filtered.Count; i++)
            DrawComponentItem(paper, font, UiId.Cat("acp_item_", i), filtered[i]);
    }

    // Unity-style click-to-navigate browser: the current folder's subfolders and components,
    // with a "Back" row when nested. Clicking a folder drills in; clicking Back steps back out.
    private static void DrawAddComponentBrowseLevel(Paper paper, XEngine.Scribe.FontFile font, List<ComponentEntry> components)
    {
        string prefix = string.Join("/", _addComponentNavStack);
        var (leaves, subfolders) = SplitComponentLevel(components, prefix);

        if (_addComponentNavStack.Count > 0)
        {
            string currentName = _addComponentNavStack[^1];
            using (paper.Row("acp_back")
                .Height(EditorTheme.RowHeight)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Rounded(6).ChildLeft(9).ChildRight(9).RowBetween(9)
                .OnClick(0, (_, _) => _addComponentNavStack.RemoveAt(_addComponentNavStack.Count - 1))
                .Enter())
            {
                paper.Box("acp_back_ico").Width(16).Height(EditorTheme.RowHeight)
                    .Text(EditorIcons.ChevronLeft, font).TextColor(EditorTheme.Ink400)
                    .FontSize(11f).Alignment(TextAlignment.MiddleCenter);
                paper.Box("acp_back_name").Height(EditorTheme.RowHeight)
                    .Text(currentName, font).TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
            }

            paper.Box("acp_back_sep").Height(1).Margin(8, 3, 8, 3).BackgroundColor(EditorTheme.BorderSoft);
        }

        foreach (var folder in subfolders)
        {
            var captured = folder;
            using (paper.Row(UiId.Cat("acp_folder_", folder))
                .Height(EditorTheme.RowHeight)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Rounded(6).ChildLeft(9).ChildRight(9).RowBetween(9)
                .OnClick(0, (_, _) => _addComponentNavStack.Add(captured))
                .Enter())
            {
                paper.Box(UiId.Underscore(UiId.Cat("acp_folder_", folder), "ico")).Width(16).Height(EditorTheme.RowHeight)
                    .Text(EditorIcons.Folder, font).TextColor(EditorTheme.Ink400)
                    .FontSize(11f).Alignment(TextAlignment.MiddleCenter);

                paper.Box(UiId.Underscore(UiId.Cat("acp_folder_", folder), "name"))
                    .Width(UnitValue.Stretch()).Height(EditorTheme.RowHeight)
                    .Text(folder, font).TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                paper.Box(UiId.Underscore(UiId.Cat("acp_folder_", folder), "arw")).Width(16).Height(EditorTheme.RowHeight)
                    .Text(EditorIcons.ChevronRight, font).TextColor(EditorTheme.Ink300)
                    .FontSize(11f).Alignment(TextAlignment.MiddleCenter);
            }
        }

        if (subfolders.Count > 0 && leaves.Count > 0)
            paper.Box("acp_level_sep").Height(1).Margin(8, 3, 8, 3).BackgroundColor(EditorTheme.BorderSoft);

        foreach (var comp in leaves)
            DrawComponentItem(paper, font, UiId.Cat("acp_item_", comp.Type.Name), comp);

        if (subfolders.Count == 0 && leaves.Count == 0)
        {
            paper.Box("acp_empty").Height(40)
                .Text(Loc.Get("popup.no_components"), font)
                .TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
        }
    }

    // Splits components into this level's direct items (Category == prefix) and its immediate
    // subfolder names (the next path segment past prefix), so browsing can drill in one segment
    // at a time regardless of how deep the full category path goes.
    private static (List<ComponentEntry> Leaves, List<string> Subfolders) SplitComponentLevel(List<ComponentEntry> components, string prefix)
    {
        var leaves = new List<ComponentEntry>();
        var subfolders = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in components)
        {
            if (c.Category == prefix)
            {
                leaves.Add(c);
                continue;
            }

            if (prefix.Length > 0 && !c.Category.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;

            string rel = prefix.Length > 0 ? c.Category[(prefix.Length + 1)..] : c.Category;
            int slash = rel.IndexOf('/');
            subfolders.Add(slash < 0 ? rel : rel[..slash]);
        }

        var sortedSubfolders = subfolders.ToList();
        sortedSubfolders.Sort(StringComparer.OrdinalIgnoreCase);
        leaves.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return (leaves, sortedSubfolders);
    }

    private static void DrawComponentItem(Paper paper, XEngine.Scribe.FontFile font, string id, ComponentEntry comp)
    {
        using (paper.Row(id)
            .Height(EditorTheme.RowHeight)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Rounded(6).ChildLeft(9).ChildRight(9).RowBetween(9)
            .OnClick(comp.Type, (type, _) =>
            {
                if (_addComponentTarget != null)
                {
                    AddComponentWithUndo(_addComponentTarget, type);
                    CloseAddComponentPopup();
                }
            })
            .Enter())
        {
            paper.Box(UiId.Underscore(id, "ico"))
                .Width(16).Height(EditorTheme.RowHeight)
                .Text(comp.Icon, font).TextColor(EditorTheme.Ink400)
                .FontSize(11f).Alignment(TextAlignment.MiddleCenter);

            paper.Box(UiId.Underscore(id, "name"))
                .Height(EditorTheme.RowHeight)
                .Text(comp.Name, font).TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
        }
    }

    /// <summary>
    /// Adds a component of <paramref name="type"/> to <paramref name="go"/> and registers a matching
    /// undo/redo step. Shared by the popup and the drag-a-script-onto-the-inspector path.
    /// </summary>
    public static MonoBehaviour? AddComponentWithUndo(GameObject go, Type type)
    {
        var addedComp = go.AddComponent(type);
        if (addedComp != null)
        {
            if (addedComp is Camera addedCamera)
                UniversalAdditionalCameraData.GetOrAdd(addedCamera);
            else if (addedComp is Light addedLight)
                UniversalAdditionalLightData.GetOrAdd(addedLight);

            // Preset Manager: newly added components pick up their type's default preset, if set.
            Presets.PresetUtility.ApplyDefaultTo(addedComp);

            var compId = addedComp.Identifier;
            var goId = go.Identifier;
            var serialized = Echo.Serializer.Serialize(addedComp.GetType(), addedComp);
            var compType = addedComp.GetType();
            Undo.RegisterAction("Add Component",
                undo: () => { var g = Undo.FindGO(goId); if (g == null) return; var c = g.GetComponentByIdentifier(compId); if (c != null) g.RemoveComponent(c); },
                redo: () => { var g = Undo.FindGO(goId); if (g == null) return; var c = Echo.Serializer.Deserialize(serialized, compType) as MonoBehaviour; if (c != null) { c.Identifier = compId; g.AddComponent(c); } });
        }
        return addedComp;
    }

    /// <summary>
    /// Adds an already-constructed component instance (e.g. a deserialized paste) to
    /// <paramref name="go"/> and registers a matching undo/redo step.
    /// </summary>
    public static MonoBehaviour? AddComponentInstanceWithUndo(GameObject go, MonoBehaviour instance)
    {
        go.AddComponent(instance);

        if (instance is Camera addedCamera)
            UniversalAdditionalCameraData.GetOrAdd(addedCamera);
        else if (instance is Light addedLight)
            UniversalAdditionalLightData.GetOrAdd(addedLight);

        var compId = instance.Identifier;
        var goId = go.Identifier;
        var serialized = Echo.Serializer.Serialize(instance.GetType(), instance);
        var compType = instance.GetType();
        Undo.RegisterAction("Add Component",
            undo: () => { var g = Undo.FindGO(goId); if (g == null) return; var c = g.GetComponentByIdentifier(compId); if (c != null) g.RemoveComponent(c); },
            redo: () => { var g = Undo.FindGO(goId); if (g == null) return; var c = Echo.Serializer.Deserialize(serialized, compType) as MonoBehaviour; if (c != null) { c.Identifier = compId; g.AddComponent(c); } });
        return instance;
    }

    private static List<ComponentEntry> GatherComponents()
    {
        var result = new List<ComponentEntry>();

        foreach (var type in EditorUtils.GetAllTypes())
        {
            if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract) continue;
            if (type == typeof(MonoBehaviour)) continue;
            if (type.Name == "MissingMonobehaviour") continue;
            if (!IsAddComponentTypeVisible(type)) continue;

            var menuAttr = type.GetCustomAttribute<AddComponentMenuAttribute>();
            string path = menuAttr?.Path ?? type.Name;
            string icon = menuAttr?.Icon ?? "";

            int lastSlash = path.LastIndexOf('/');
            string category = lastSlash >= 0 ? path[..lastSlash] : "";
            string name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;

            if (string.IsNullOrEmpty(icon))
            {
                icon = category switch
                {
                    var c when c.StartsWith("Rendering") => EditorIcons.Cube,
                    var c when c.StartsWith("Audio") => EditorIcons.VolumeHigh,
                    var c when c.StartsWith("Light") => EditorIcons.Sun,
                    var c when c.Contains("Collider") => EditorIcons.VectorSquare,
                    var c when c.Contains("Constraint") || c.Contains("Joint") => EditorIcons.Link,
                    var c when c.StartsWith("Physics") => EditorIcons.Atom,
                    var c when c.StartsWith("UI") => EditorIcons.Desktop,
                    var c when c.StartsWith("Effects") => EditorIcons.Burst,
                    var c when c.StartsWith("Terrain") => EditorIcons.Mountain,
                    _ => EditorIcons.PuzzlePiece
                };
            }

            result.Add(new ComponentEntry
            {
                Path = path,
                Category = category,
                Name = name,
                Icon = icon,
                Type = type
            });
        }

        result.Sort(static (left, right) =>
        {
            int pathComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
            return pathComparison != 0
                ? pathComparison
                : StringComparer.Ordinal.Compare(left.Type.FullName, right.Type.FullName);
        });
        return result;
    }

    /// <summary>
    /// Determines whether a component is offered for new authoring through the Add Component popup.
    /// Legacy UI classes remain loadable so old scenes and prefabs stay valid, but their controls
    /// never appear in authoring UI. XGUI is an Editor built-in package and owns new UI controls.
    /// </summary>
    internal static bool IsAddComponentTypeVisible(Type type)
    {
        if (type.GetCustomAttribute<System.ComponentModel.EditorBrowsableAttribute>()?.State ==
            System.ComponentModel.EditorBrowsableState.Never)
            return false;
        return !IsLegacyUiCompatibilityComponent(type);
    }

    private static bool IsLegacyUiCompatibilityComponent(Type type)
    {
        if (!ReferenceEquals(type.Assembly, typeof(GameCanvas).Assembly)) return false;

        // Runtime-owned Canvas and EventSystem contracts remain authorable because the package's
        // creation helpers compose them. These are the legacy visual, layout, and control classes
        // superseded by XGUI.
        return type.Name is "UIImage"
            or "TextComponent"
            or "UIButton"
            or "UISlider"
            or "UIScrollbar"
            or "UIScrollRect"
            or "UIInputField"
            or "UIDropdown"
            or "Selectable"
            or "RectMask"
            or "UILayoutElement"
            or "UILayoutGroup"
            or "UIHorizontalOrVerticalLayoutGroup"
            or "UIHorizontalLayoutGroup"
            or "UIVerticalLayoutGroup"
            or "UIGridLayoutGroup"
            or "UIContentSizeFitter";
    }
}
