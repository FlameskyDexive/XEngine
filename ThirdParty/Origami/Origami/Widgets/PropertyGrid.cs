// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using XEngine.Echo;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.OrigamiUI;

// ════════════════════════════════════════════════════════════════
//  Field Drawer - renders a specific type in the property grid
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Base class for type-specific field renderers. Register via
/// <see cref="FieldDrawerRegistry.Register{T}(FieldDrawer)"/>.
/// The PropertyGrid handles the label; the drawer renders the control.
/// </summary>
public abstract class FieldDrawer
{
    public abstract void Draw(Paper paper, string id, object? value, Type fieldType,
        Action<object?> onChange, int depth);
}

/// <summary>Registry mapping types to their FieldDrawer instances.</summary>
public sealed class FieldDrawerRegistry
{
    private readonly Dictionary<Type, FieldDrawer> _drawers = new();

    // Caches the resolved drawer (or null) per queried type so the base-chain + interface walk
    // (whose GetInterfaces call allocates a RuntimeType[] every time) doesn't run per field per
    // frame - GetDrawer is on the PropertyGrid per-row draw path. Weakly keyed so hot-reloaded
    // script types evict with their AssemblyLoadContext; reset when registrations change.
    private sealed class Resolved { public FieldDrawer? Drawer; }
    private System.Runtime.CompilerServices.ConditionalWeakTable<Type, Resolved> _resolveCache = new();

    public void Register<T>(FieldDrawer drawer) { _drawers[typeof(T)] = drawer; _resolveCache = new(); }
    public void Register(Type type, FieldDrawer drawer) { _drawers[type] = drawer; _resolveCache = new(); }

    public FieldDrawer? GetDrawer(Type type)
    {
        // TryGetValue first so cache hits allocate nothing (a factory lambda would capture `this`).
        if (_resolveCache.TryGetValue(type, out var cached)) return cached.Drawer;
        var resolved = new Resolved { Drawer = ResolveDrawer(type) };
        _resolveCache.Add(type, resolved);
        return resolved.Drawer;
    }

    private FieldDrawer? ResolveDrawer(Type type)
    {
        if (_drawers.TryGetValue(type, out var drawer)) return drawer;
        var current = type.BaseType;
        while (current != null && current != typeof(object))
        {
            if (_drawers.TryGetValue(current, out drawer)) return drawer;
            current = current.BaseType;
        }
        foreach (var iface in type.GetInterfaces())
            if (_drawers.TryGetValue(iface, out drawer)) return drawer;
        return null;
    }

    public void Clear() { _drawers.Clear(); _resolveCache = new(); }
}

// ════════════════════════════════════════════════════════════════
//  Attribute Handler - modifies rendering based on field attributes
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Base class for attribute-driven rendering modifications.
/// </summary>
public abstract class AttributeHandler
{
    /// <summary>Called before the field is drawn. Return false to skip the field entirely.</summary>
    public virtual bool OnBeforeDraw(Paper paper, string id, Attribute attr,
        FieldInfo field, object target, int depth) => true;

    /// <summary>Called instead of the default drawer. Return true if handled.</summary>
    public virtual bool OnDraw(Paper paper, string id, string label, Attribute attr,
        FieldInfo field, object target, Action<object?> onChange, int depth) => false;

    /// <summary>Called after the field is drawn.</summary>
    public virtual void OnAfterDraw(Paper paper, string id, Attribute attr,
        FieldInfo field, object target, int depth)
    { }
}

/// <summary>Registry mapping attribute types to their handlers.</summary>
public sealed class AttributeHandlerRegistry
{
    private readonly Dictionary<Type, AttributeHandler> _handlers = new();

    public void Register<TAttr>(AttributeHandler handler) where TAttr : Attribute
        => _handlers[typeof(TAttr)] = handler;

    public AttributeHandler? GetHandler(Type attrType)
        => _handlers.GetValueOrDefault(attrType);

    public void Clear() => _handlers.Clear();
}

// ════════════════════════════════════════════════════════════════
//  Custom Object Editor - whole-object editor override
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Base class for custom whole-object editors. When a nested object's type has
/// a registered CustomObjectEditor, it replaces the default field-by-field rendering.
/// </summary>
public abstract class CustomObjectEditor
{
    public abstract void OnGUI(Paper paper, string id, object target);
}

/// <summary>Registry mapping types to their CustomObjectEditor.</summary>
public sealed class CustomObjectEditorRegistry
{
    private readonly Dictionary<Type, CustomObjectEditor> _editors = new();

    // Caches the resolved editor (or null) per queried type so the base-chain + interface walk
    // (which allocates via GetInterfaces) doesn't run every frame. Weakly keyed so hot-reloaded
    // script types evict with their AssemblyLoadContext; reset when registrations change.
    private sealed class Resolved { public CustomObjectEditor? Editor; }
    private System.Runtime.CompilerServices.ConditionalWeakTable<Type, Resolved> _resolveCache = new();

    public void Register<T>(CustomObjectEditor editor) { _editors[typeof(T)] = editor; _resolveCache = new(); }
    public void Register(Type type, CustomObjectEditor editor) { _editors[type] = editor; _resolveCache = new(); }

    public CustomObjectEditor? GetEditor(Type type)
    {
        // TryGetValue first so cache hits allocate nothing (a factory lambda would capture `this`).
        if (_resolveCache.TryGetValue(type, out var cached)) return cached.Editor;
        var resolved = new Resolved { Editor = ResolveEditor(type) };
        _resolveCache.Add(type, resolved);
        return resolved.Editor;
    }

    private CustomObjectEditor? ResolveEditor(Type type)
    {
        if (_editors.TryGetValue(type, out var editor)) return editor;
        var current = type.BaseType;
        while (current != null && current != typeof(object))
        {
            if (_editors.TryGetValue(current, out editor)) return editor;
            current = current.BaseType;
        }
        foreach (var iface in type.GetInterfaces())
            if (_editors.TryGetValue(iface, out editor)) return editor;
        return null;
    }

    public void Clear() { _editors.Clear(); _resolveCache = new(); }
}

// ════════════════════════════════════════════════════════════════
//  PropertyGrid Config - holds all registries and callbacks
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Configuration for a PropertyGrid instance. Create one per context (e.g., one for
/// the editor, one for game UI) and pass it to the builder. Each config has its own
/// registries, callbacks, and settings so they don't interfere.
/// </summary>
public sealed class PropertyGridConfig
{
    /// <summary>Type-specific field drawers (bool, float, Color, etc.).</summary>
    public FieldDrawerRegistry Drawers { get; } = new();

    /// <summary>Attribute-driven rendering modifiers ([Range], [Header], etc.).</summary>
    public AttributeHandlerRegistry Handlers { get; } = new();

    /// <summary>Custom whole-object editors.</summary>
    public CustomObjectEditorRegistry CustomEditors { get; } = new();

    /// <summary>Max recursion depth. Default 10.</summary>
    public int MaxDepth = 10;

    /// <summary>When true, nested objects, lists and their entries start expanded. Default false
    /// (everything collapsed until the user opens it).</summary>
    public bool ExpandByDefault = false;

    /// <summary>Called at depth 0 before any field is drawn (e.g., for undo snapshots).</summary>
    public Action<object>? OnBeginRoot;

    /// <summary>Called after any field value changes (e.g., for OnValidate).</summary>
    public Action<object>? OnFieldChanged;

    /// <summary>
    /// Called before drawing each field. Hosts can set up state needed by custom drawers
    /// (e.g., passing the declared field type for EngineObject drawers).
    /// </summary>
    public Action<Type, object?>? OnBeforeDrawField;

    /// <summary>
    /// Draws a type picker for polymorphic fields (abstract/interface).
    /// Parameters: (paper, id, baseType, currentValue, onChange).
    /// </summary>
    public Action<Paper, string, Type, object?, Action<object?>>? DrawTypePicker;

    /// <summary>
    /// Fallback for drawing a field when no FieldDrawer is registered.
    /// The host can route to its own editor registry (e.g., PropertyEditorRegistry).
    /// Parameters: (paper, id, label, fieldType, value, onChange, depth).
    /// Return true if handled.
    /// </summary>
    public Func<Paper, string, string, Type, object?, Action<object?>, int, bool>? FallbackFieldDrawer;

    /// <summary>
    /// True when the host has a single-control editor for this type (e.g. AssetRef, EngineObject,
    /// Color). Such types render as a simple one-line collection row instead of the nested (foldout
    /// per element) list, even though they have serializable fields.
    /// </summary>
    public Func<Type, bool>? IsSimpleFieldType;

    /// <summary>
    /// Resolves a tooltip text for a field or property row (e.g. from a [Tooltip] attribute).
    /// Return null/empty for no tooltip. Applied to the row label and collection headers.
    /// </summary>
    public Func<MemberInfo, string?>? ResolveTooltip;

    /// <summary>
    /// Builds a right-click context menu for a field row (Copy/Paste value, prefab overrides,
    /// multi-object "Set to value of X", ...). Invoked while the menu is open, once per frame.
    /// </summary>
    public Action<Paper, ContextBuilder, FieldContextMenuRequest>? OnFieldContextMenu;

    /// <summary>
    /// Raw/debug mode: also show private instance fields (read-only unless [SerializeField] /
    /// [ShowInInspector]), matching Unity's Inspector Debug mode. Hosts set this on a dedicated
    /// config used by the debug inspector.
    /// </summary>
    public bool IncludePrivateFields;
}

/// <summary>Context for <see cref="PropertyGridConfig.OnFieldContextMenu"/>.</summary>
public sealed class FieldContextMenuRequest
{
    /// <summary>Paper element id of the field row.</summary>
    public string Id = "";

    /// <summary>Display label of the field.</summary>
    public string Label = "";

    /// <summary>Backing member (FieldInfo or PropertyInfo) when the row is a grid field; else null.</summary>
    public MemberInfo? Member;

    /// <summary>Member name, for prefab-override lookups. Empty when unknown.</summary>
    public string FieldName = "";

    /// <summary>Targets sharing this field (multi-object editing); never null.</summary>
    public IReadOnlyList<object> Targets = Array.Empty<object>();

    /// <summary>Live read of the representative value (works while the menu stays open).</summary>
    public Func<object?> GetValue = static () => null;

    /// <summary>Applies a value to every target (notifies the host per target).</summary>
    public Action<object?> SetValue = static _ => { };

    /// <summary>True when the field is flagged as a prefab override.</summary>
    public bool IsOverridden;

    /// <summary>True when values differ across targets.</summary>
    public bool IsMixed;
}

// ════════════════════════════════════════════════════════════════
//  PropertyGrid Builder - fluent API following Origami conventions
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Fluent builder for a property grid. Construct via
/// <see cref="Origami.PropertyGrid(Paper, string, object, PropertyGridConfig)"/>
/// and call <see cref="Show"/> to render.
/// </summary>
public sealed class PropertyGridBuilder
{
    private readonly Paper _paper;
    private readonly string _id;
    private readonly IReadOnlyList<object> _targets;
    private readonly PropertyGridConfig _config;

    private Action<object>? _onChange;
    private HashSet<string>? _overrides;
    private IReadOnlySet<string>? _excludedFields;
    private int _depth;

    internal PropertyGridBuilder(Paper paper, string id, object target, PropertyGridConfig config)
    {
        _paper = paper;
        _id = id;
        _targets = new[] { target };
        _config = config;
    }

    internal PropertyGridBuilder(Paper paper, string id, IReadOnlyList<object> targets, PropertyGridConfig config)
    {
        _paper = paper;
        _id = id;
        _targets = targets;
        _config = config;
    }

    /// <summary>Callback when any field value changes.</summary>
    public PropertyGridBuilder OnChanged(Action<object> onChange) { _onChange = onChange; return this; }

    /// <summary>Set of field names to highlight as overridden (prefab system).</summary>
    public PropertyGridBuilder Overrides(HashSet<string>? overrides) { _overrides = overrides; return this; }

    /// <summary>Omit named fields from this grid's root only; nested grids remain unchanged.</summary>
    public PropertyGridBuilder ExcludeFields(IReadOnlySet<string>? fields) { _excludedFields = fields; return this; }

    /// <summary>Starting nesting depth (default 0).</summary>
    public PropertyGridBuilder Depth(int depth) { _depth = depth; return this; }

    /// <summary>Start nested objects, lists and their entries expanded (default is collapsed).</summary>
    public PropertyGridBuilder ExpandByDefault(bool expand = true) { _config.ExpandByDefault = expand; return this; }

    /// <summary>Render the property grid.</summary>
    public void Show()
    {
        PropertyGridRenderer.DrawTargets(_paper, _id, _targets, _config, _onChange, _overrides, _depth, _excludedFields);
    }
}

// ════════════════════════════════════════════════════════════════
//  PropertyGrid Renderer - internal drawing logic
// ════════════════════════════════════════════════════════════════

/// <summary>Rendering logic for the PropertyGrid builder.</summary>
public static class PropertyGridRenderer
{
    [ThreadStatic] private static object? _rootTarget;
    [ThreadStatic] private static PropertyGridConfig? _activeConfig;

    /// <summary>
    /// True while the field currently being drawn has differing values across a multi-object selection.
    /// FieldDrawers may read this to render a "mixed" placeholder; the grid also marks it visually.
    /// </summary>
    [ThreadStatic] public static bool IsMixedField;

    // ── Per-field ambient state (valid only inside the DrawField call) ──
    // Hosts (attribute handlers, custom drawers) may read these to decorate their own rows.

    /// <summary>Tooltip text for the field currently being drawn (from config.ResolveTooltip), else null.</summary>
    [ThreadStatic] public static string? CurrentTooltip;

    /// <summary>
    /// True while the current field is private and not [SerializeField]/[ShowInInspector]
    /// (IncludePrivateFields debug mode) - such fields render read-only.
    /// </summary>
    [ThreadStatic] public static bool CurrentFieldPrivate;

    /// <summary>Member backing the field currently being drawn (FieldInfo or PropertyInfo), else null.</summary>
    [ThreadStatic] internal static MemberInfo? CurrentMember;

    /// <summary>Targets sharing the field currently being drawn (multi-object), else null.</summary>
    [ThreadStatic] internal static IReadOnlyList<object>? CurrentFieldTargets;

    [ThreadStatic] private static bool _includePrivate;

    /// <summary>Single-target entry point.</summary>
    public static void Draw(Paper paper, string id, object target, PropertyGridConfig config,
        Action<object>? onChange, HashSet<string>? overrides, int depth)
    {
        if (target == null) return;
        DrawTargets(paper, id, new[] { target }, config, onChange, overrides, depth);
    }

    /// <summary>
    /// Multi-target entry point. Draws the fields common to every target; a field whose value differs
    /// across targets is flagged as mixed, and every edit is applied to all targets. Fields that don't
    /// exist (by name and type) on every target are omitted.
    /// </summary>
    public static void DrawTargets(Paper paper, string id, IReadOnlyList<object> targets, PropertyGridConfig config,
        Action<object>? onChange, HashSet<string>? overrides, int depth, IReadOnlySet<string>? excludedFields = null)
    {
        if (targets == null || targets.Count == 0) return;
        if (depth > config.MaxDepth) return;

        object representative = targets[0];
        if (representative == null) return;

        var m = Origami.Current.Metrics;
        bool isRoot = depth == 0;
        bool multi = targets.Count > 1;

        // Nested grids reuse their parent's include-private state; the root adopts the config's.
        bool prevIncludePrivate = _includePrivate;
        _includePrivate = config.IncludePrivateFields;

        if (isRoot)
        {
            _rootTarget = representative;
            _activeConfig = config;
            for (int t = 0; t < targets.Count; t++)
                if (targets[t] != null) config.OnBeginRoot?.Invoke(targets[t]);
        }

        using (paper.Column(InternFieldId(id, "root")).ColBetween(m.Fields?.RowSpacing ?? m.SpacingLarge).Height(UnitValue.Auto).Enter())
        {
            var type = representative.GetType();
            var fields = multi ? CommonSerializableFields(targets, _includePrivate) : GetSerializableFields(type);

            var buttonMethods = GetButtonMethods(type);

            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (excludedFields?.Contains(field.Name) == true) continue;
                string fieldId = InternFieldId(id, field.Name);

                var meta = GetFieldMeta(field);
                var attrs = meta.Attributes;
                bool skip = false;
                bool handled = false;

                // Pre-draw attribute handlers (operate on the representative)
                foreach (var attr in attrs)
                {
                    var handler = config.Handlers.GetHandler(attr.GetType());
                    if (handler != null && !handler.OnBeforeDraw(paper, fieldId, attr, field, representative, depth))
                    {
                        skip = true;
                        break;
                    }
                }
                if (skip) continue;

                bool isMixed = multi && IsFieldMixed(targets, field);
                Action<object?> applyAll = GetFieldApply(paper, fieldId, field, targets, config, onChange);

                // Per-field ambient state for the DrawField call (tooltips, context menu, L/R submits).
                string? tooltip = config.ResolveTooltip?.Invoke(field);
                CurrentTooltip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
                CurrentMember = field;
                CurrentFieldTargets = targets;
                CurrentFieldPrivate = _includePrivate && !field.IsPublic && !IsExplicitlyExposed(field);

                // Attribute-driven draw replacement
                foreach (var attr in attrs)
                {
                    var handler = config.Handlers.GetHandler(attr.GetType());
                    if (handler != null)
                    {
                        string label = meta.Label;
                        IsMixedField = isMixed;
                        bool h = handler.OnDraw(paper, fieldId, label, attr, field, representative, applyAll, depth);
                        IsMixedField = false;
                        if (h) { handled = true; break; }
                    }
                }

                if (!handled)
                {
                    var value = field.GetValue(representative);
                    var fieldType = field.FieldType;
                    string label = meta.Label;
                    bool isOverridden = overrides?.Contains(field.Name) ?? false;

                    DrawField(paper, fieldId, label, fieldType, value, config, applyAll, depth, isOverridden, isMixed);
                }

                // Post-draw attribute handlers
                foreach (var attr in attrs)
                {
                    var handler = config.Handlers.GetHandler(attr.GetType());
                    handler?.OnAfterDraw(paper, fieldId, attr, field, representative, depth);
                }

                CurrentTooltip = null;
                CurrentMember = null;
                CurrentFieldTargets = null;
                CurrentFieldPrivate = false;
            }

            // [Button] methods (invoked on every target)
            foreach (var button in buttonMethods)
            {
                var method = button.Method;
                Origami.Button(paper, InternFieldId(InternFieldId(id, "btn"), method.Name), button.Label, () =>
                {
                    for (int t = 0; t < targets.Count; t++)
                    {
                        if (targets[t] == null) continue;
                        try { method.Invoke(targets[t], null); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Button error: {ex.Message}"); }
                    }
                }).Show();
            }

            // [ShowInInspector] properties: display-only (or editable, if the property has a setter
            // and isn't marked ReadOnly). Properties are never serialized; this pass only surfaces
            // computed/derived values in the inspector. Indexers and override-only properties are skipped.
            var shownProperties = GetShownProperties(type);
            foreach (var prop in shownProperties)
            {
                string propId = InternFieldId(InternFieldId(id, "p"), prop.Name);
                string label = FormatFieldName(prop.Name);

                object? propValue;
                try { propValue = prop.GetValue(representative); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShowInInspector getter threw on {prop.Name}: {ex.Message}"); continue; }

                bool isReadOnly = Origami.IsReadOnly || !IsPropertyEditable(prop);
                Action<object?> applyProp = v =>
                {
                    for (int t = 0; t < targets.Count; t++)
                    {
                        if (targets[t] == null) continue;
                        var p = targets[t].GetType() == prop.DeclaringType ? prop : ResolveProperty(targets[t].GetType(), prop.Name);
                        if (p == null) continue;
                        try { p.SetValue(targets[t], v, null); } catch { continue; }
                        config.OnFieldChanged?.Invoke(targets[t]);
                    }
                    onChange?.Invoke(targets[0]);
                };

                using (Origami.BeginReadOnlyScope(isReadOnly))
                {
                    string? propTooltip = config.ResolveTooltip?.Invoke(prop);
                    CurrentTooltip = string.IsNullOrEmpty(propTooltip) ? null : propTooltip;
                    CurrentMember = prop;
                    CurrentFieldTargets = targets;
                    CurrentFieldPrivate = false;
                    DrawField(paper, propId, label, prop.PropertyType, propValue, config, applyProp, depth, false, false);
                    CurrentTooltip = null;
                    CurrentMember = null;
                    CurrentFieldTargets = null;
                }
            }
        }

        _includePrivate = prevIncludePrivate;

        if (isRoot)
        {
            _rootTarget = null;
            _activeConfig = null;
        }
    }

    // ── DrawField (public for external callers like MaterialPropertyDrawer) ──

    public static void DrawField(Paper paper, string id, string label, Type fieldType,
        object? value, PropertyGridConfig config, Action<object?> onChange, int depth, bool isOverridden = false, bool isMixed = false)
    {
        // Notify host before drawing
        config.OnBeforeDrawField?.Invoke(fieldType, value);

        // Try fallback (host's PropertyEditorRegistry) before our own layout
        var drawer = config.Drawers.GetDrawer(fieldType);
        if (drawer == null && config.FallbackFieldDrawer != null)
        {
            if (config.FallbackFieldDrawer(paper, id, label, fieldType, value, onChange, depth))
                return;
        }

        var theme = Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;
        var ink = theme.Ink;

        // Complex collections (element type has its own fields) span the full section width with a
        // self-labelled header. Simple collections (leaf element types like Material refs, colors,
        // primitives) keep the normal label gutter and draw their compact card in the control column -
        // matching Nebula's `.i2field > .i2label + .coll` vs full-width `.i2field.full > .nl`.
        bool isLeafCollection = false;
        if (drawer == null && fieldType != typeof(string))
        {
            if (typeof(IList).IsAssignableFrom(fieldType))
            {
                Type et = GetListElementType(fieldType);
                if (IsLeafElementType(et, config))
                    isLeafCollection = true;
                else
                {
                    // Full-width card, but keep the standard field gutter so it doesn't touch the edges.
                    using (paper.Row(InternFieldId(id, "fw")).Width(UnitValue.Stretch()).Height(UnitValue.Auto).Padding(m.PaddingLarge, m.PaddingLarge, 0, 0).Enter())
                        DrawCollection(paper, id, fieldType, value as IList, config, onChange, depth, label);
                    return;
                }
            }
            else if (IsEditableDictionary(fieldType, out _, out _))
            {
                // Dictionaries span the full section width like complex collections.
                using (paper.Row(InternFieldId(id, "fw")).Width(UnitValue.Stretch()).Height(UnitValue.Auto).Padding(m.PaddingLarge, m.PaddingLarge, 0, 0).Enter())
                    DrawDictionary(paper, id, fieldType, value as IDictionary, config, onChange, depth, label);
                return;
            }
        }

        using (paper.Row(id).Height(UnitValue.Auto).MinHeight(m.RowHeight)
            .Padding(m.PaddingLarge, m.PaddingLarge, 0, 0).RowBetween(m.Padding).Enter())
        {
            // Field-row context menu (Copy/Paste value, prefab overrides, multi-object shortcuts).
            // Built fresh per frame while the menu is open, so the request reads live values.
            if (config.OnFieldContextMenu != null)
            {
                bool overridden = isOverridden, mixed = isMixed;
                string rowLabel = label;
                Origami.RightClickMenu(paper, id, b =>
                {
                    var req = BuildFieldMenuRequest(id, rowLabel, fieldType, overridden, mixed, onChange);
                    if (req != null) config.OnFieldContextMenu(paper, b, req);
                });
            }

            // Override (prefab) / mixed (multi-select) marker: a small accent / amber dot before the label.
            if (isOverridden || isMixed)
            {
                var dot = isOverridden ? theme.Primary.C500 : theme.Amber.C400;
                using (paper.Box(InternFieldId(id, "ov")).Width(8).Height(m.RowHeight).IsNotInteractable().Enter())
                    paper.Box(InternFieldId(id, "ovd")).Width(5).Height(5).Rounded(3)
                        .Margin(1, UnitValue.Stretch(), UnitValue.Stretch(), UnitValue.Stretch())
                        .BackgroundColor(dot).IsNotInteractable();
            }

            if (font != null && !string.IsNullOrEmpty(label))
            {
                bool isNumeric = IsNumericType(fieldType);
                // A tall card (simple collection) top-aligns its label; scalar fields centre it.
                var lbl = paper.Box(InternFieldId(id, "lbl"))
                    .Width(m.LabelWidth).Height(m.RowHeight)
                    .Margin(0, 0, isLeafCollection ? UnitValue.Pixels(4) : UnitValue.Stretch(), UnitValue.Stretch())
                    .Text(label, font)
                    .TextColor(isMixed ? theme.Amber.C400 : isOverridden ? theme.Primary.C700 : ink.C300)
                    .FontSize(m.Fields?.FontSize ?? m.FontSize).Alignment(TextAlignment.MiddleLeft).TextTruncate();

                if (CurrentTooltip is string tt)
                    lbl.Tooltip(tt);

                if (isNumeric && !Origami.IsReadOnly && !CurrentFieldPrivate)
                {
                    if (m.EditorNumericScrub)
                    {
                        bool floating = fieldType == typeof(float) || fieldType == typeof(double) || fieldType == typeof(decimal);
                        NumericScrub.Attach(paper, lbl, ConvertToDouble(value), floating, next =>
                        {
                            object? converted = ConvertScrubFromDouble(next, fieldType);
                            if (converted != null) onChange(converted);
                        });
                    }
                    else
                    {
                    // Draggable label for numeric fields - horizontal drag adjusts value
                    // Ctrl = x10, Shift = x0.01, default = x0.1
                    lbl.OnDragStart(e =>
                        {
                            // Store initial value at drag start
                            paper.SetElementStorage(paper.CurrentParent, "drag_start", ConvertToDouble(value));
                        })
                        .OnDragging(e =>
                        {
                            float multiplier = 0.1f;
                            if (paper.IsKeyDown(PaperUI.PaperKey.LeftControl) || paper.IsKeyDown(PaperUI.PaperKey.RightControl))
                                multiplier *= 10f;
                            else if (paper.IsKeyDown(PaperUI.PaperKey.LeftShift) || paper.IsKeyDown(PaperUI.PaperKey.RightShift))
                                multiplier *= 0.01f;

                            double startVal = paper.GetElementStorage(paper.CurrentParent, "drag_start", ConvertToDouble(value));
                            double newVal = startVal + (double)e.TotalDelta.X * multiplier;
                            object? converted = ConvertFromDouble(newVal, fieldType);
                            if (converted != null) onChange(converted);
                        })
                        .Cursor(PaperCursor.ResizeHorizontal);
                    }
                }
                else
                {
                    lbl.IsNotInteractable();
                }
            }

            using (paper.Box(InternFieldId(id, "ctl")).Width(UnitValue.Stretch()).Height(UnitValue.Auto).MinHeight(m.RowHeight).Enter())
            using (Origami.BeginReadOnlyScope(CurrentFieldPrivate))
            {
                IsMixedField = isMixed;

                // Multi-object numeric fields route raw submits (L(a,b) / R(a,b) distribution
                // expressions) through a per-target handler instead of the single-value path.
                if (CurrentMember is FieldInfo numField && CurrentFieldTargets is { Count: > 1 } numTargets && IsNumericType(fieldType))
                {
                    var prev = NumericSubmit.Interceptor;
                    NumericSubmit.Interceptor = (raw, _) => TryPerTargetNumericSubmit(raw, numField, numTargets, fieldType, config);
                    try { DrawFieldControl(paper, InternFieldId(id, "v"), fieldType, value, config, onChange, depth); }
                    finally { NumericSubmit.Interceptor = prev; }
                }
                else
                {
                    DrawFieldControl(paper, InternFieldId(id, "v"), fieldType, value, config, onChange, depth);
                }
                IsMixedField = false;
            }
        }
    }

    /// <summary>
    /// Build the context-menu request for a field row from the current per-field ambient state.
    /// Returns null when no member/targets are ambient (external DrawField callers).
    /// </summary>
    private static FieldContextMenuRequest? BuildFieldMenuRequest(string id, string label, Type fieldType,
        bool isOverridden, bool isMixed, Action<object?> onChange)
    {
        var member = CurrentMember;
        var targets = CurrentFieldTargets;
        if (member == null || targets == null || targets.Count == 0) return null;

        var req = new FieldContextMenuRequest
        {
            Id = id,
            Label = label,
            Member = member,
            FieldName = member.Name,
            Targets = targets,
            IsOverridden = isOverridden,
            IsMixed = isMixed,
        };

        if (member is FieldInfo fi)
        {
            req.GetValue = () =>
            {
                var t = targets[0];
                var f = t.GetType() == fi.DeclaringType ? fi : ResolveField(t.GetType(), fi.Name);
                return f?.GetValue(t);
            };
            req.SetValue = v =>
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    if (t == null) continue;
                    var f = t.GetType() == fi.DeclaringType ? fi : ResolveField(t.GetType(), fi.Name);
                    if (f == null) continue;
                    try { f.SetValue(t, v); } catch { continue; }
                }
                onChange(v);
            };
        }
        else if (member is PropertyInfo pi)
        {
            req.GetValue = () =>
            {
                var t = targets[0];
                var p = t.GetType() == pi.DeclaringType ? pi : ResolveProperty(t.GetType(), pi.Name);
                return p?.GetValue(t);
            };
            req.SetValue = v => onChange(v);
        }
        return req;
    }

    /// <summary>
    /// Evaluate a multi-object numeric submit (L(a,b) linear ramp / R(a,b) random) per target and
    /// apply each target's own result. Returns false when the raw text isn't a per-target expression.
    /// </summary>
    private static bool TryPerTargetNumericSubmit(string raw, FieldInfo field, IReadOnlyList<object> targets,
        Type fieldType, PropertyGridConfig config)
    {
        // Cheap pre-check: any L( / R( token at all (parser rejects them outside multi context anyway).
        bool hasL = raw.Contains("L(", StringComparison.OrdinalIgnoreCase);
        bool hasR = raw.Contains("R(", StringComparison.OrdinalIgnoreCase);
        if (!hasL && !hasR) return false;

        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t == null) continue;
            var f = t.GetType() == field.DeclaringType ? field : ResolveField(t.GetType(), field.Name);
            if (f == null) continue;
            double current = ConvertToDouble(f.GetValue(t));
            if (!MathParser.TryEvaluate(raw, current, i, targets.Count, out double result)) return false;
            var converted = ConvertFromDouble(result, fieldType);
            if (converted == null) return false;
            try { f.SetValue(t, converted); } catch { }
            config.OnFieldChanged?.Invoke(t);
        }
        return true;
    }

    // ── DrawFieldControl ─────────────────────────────────────

    public static void DrawFieldControl(Paper paper, string id, Type fieldType,
        object? value, PropertyGridConfig config, Action<object?> onChange, int depth)
    {
        // 1. Registered FieldDrawer
        var drawer = config.Drawers.GetDrawer(fieldType);
        if (drawer != null)
        {
            drawer.Draw(paper, id, value, fieldType, onChange, depth);
            return;
        }

        // 1b. Host fallback (e.g. the editor's AssetRef / EngineObject editors). DrawField does this
        // for top-level fields; element controls must too, or list/array/dictionary entries of those
        // types render as a dead read-only label. The label is empty because the caller (collection /
        // dictionary row) already draws the index/key label.
        if (config.FallbackFieldDrawer != null &&
            config.FallbackFieldDrawer(paper, id, "", fieldType, value, onChange, depth))
            return;

        // 2. Enums
        if (fieldType.IsEnum)
        {
            DrawEnum(paper, id, fieldType, value, onChange);
            return;
        }

        // 3. Collections (IList: List<T>, T[])
        if (typeof(IList).IsAssignableFrom(fieldType) && fieldType != typeof(string))
        {
            DrawCollection(paper, id, fieldType, value as IList, config, onChange, depth);
            return;
        }

        // 3b. Dictionaries (Dictionary<,> and other generic IDictionary implementations) - Unity
        // 6.6-style two-column key/value editor. Keys are limited to leaf types; values recurse
        // through the normal control pipeline. Other dictionary shapes stay on the read-only
        // "unsupported" note below.
        if (IsEditableDictionary(fieldType, out _, out _))
        {
            DrawDictionary(paper, id, fieldType, value as IDictionary, config, onChange, depth);
            return;
        }

        // Dictionaries and other unsupported types (Nullable&lt;T&gt;, Guid/DateTime, non-IList collections,
        // ...) are intentionally not editable in the property grid - they fall through to the read-only
        // "unsupported" note below.

        // 4. Nested object
        if (value != null)
        {
            var customEditor = config.CustomEditors.GetEditor(value.GetType());
            if (customEditor != null)
            {
                DrawNestedObjectWithCustomEditor(paper, id, fieldType, value, config, onChange, depth, customEditor);
                return;
            }

            var nestedFields = GetSerializableFields(value.GetType());
            if (nestedFields.Length > 0)
            {
                DrawNestedObject(paper, id, fieldType, value, config, onChange, depth);
                return;
            }
        }

        // 5. Null reference-type with "Create" button
        if (value == null && !fieldType.IsValueType)
        {
            DrawNullObject(paper, id, fieldType, config, onChange);
            return;
        }

        // 6. Fallback: read-only "unsupported type" note.
        {
            var font = Origami.Current.Font;
            var met = Origami.Current.Metrics;
            if (font != null)
            {
                string typeName = fieldType.Name.Split('`')[0]; // strip generic arity (Dictionary`2 -> Dictionary)
                paper.Box($"{id}_fb").Height(met.RowHeight).IsNotInteractable()
                    .Text($"({typeName} - unsupported)", font).TextColor(Origami.Current.Ink.C300)
                    .FontSize(met.FontSize)
                    .Alignment(TextAlignment.MiddleLeft);
            }
        }
    }

    // ── Enum ─────────────────────────────────────────────────

    private static void DrawEnum(Paper paper, string id, Type enumType, object? value, Action<object?> onChange)
    {
        // [Flags] enums hold a combination of bits, so a single-select dropdown can neither show nor set
        // combinations - use a multi-select of the individual flags instead.
        if (enumType.IsDefined(typeof(FlagsAttribute), false))
        {
            DrawFlagsEnum(paper, id, enumType, value, onChange);
            return;
        }

        var meta = GetEnumMeta(enumType);
        var names = meta.Names;
        var values = meta.Values;
        int currentIdx = value != null ? Array.IndexOf(values, value) : 0;
        if (currentIdx < 0) currentIdx = 0;

        Origami.Dropdown(paper, id, currentIdx, idx => onChange(values.GetValue(idx)), names).Show();
    }

    private static void DrawFlagsEnum(Paper paper, string id, Type enumType, object? value, Action<object?> onChange)
    {
        ulong valueU = value != null ? Convert.ToUInt64(value) : 0;

        var nonZero = new List<object>();
        var current = new List<object>();
        foreach (var v in GetEnumMeta(enumType).Values)
        {
            ulong u = Convert.ToUInt64(v);
            if (u == 0) continue;              // skip the None/0 entry - clearing all boxes = no bits
            nonZero.Add(v!);
            if ((valueU & u) == u) current.Add(v!);
        }

        var before = new HashSet<ulong>();
        foreach (var c in current) before.Add(Convert.ToUInt64(c));

        Origami.MultiDropdown<object>(paper, id, current,
            selected =>
            {
                var after = new HashSet<ulong>();
                foreach (var f in selected) after.Add(Convert.ToUInt64(f));

                ulong bits = valueU;
                foreach (ulong u in before) if (!after.Contains(u)) bits &= ~u;
                foreach (ulong u in after) if (!before.Contains(u)) bits |= u;
                onChange(Enum.ToObject(enumType, bits));
            }, nonZero)
            .Display(o => Enum.GetName(enumType, o) ?? o.ToString() ?? "")
            .Show();
    }

    // ── Collection ───────────────────────────────────────────

    // Leaf element = drawn by a single control (primitive/enum/string, a registered drawer, or a host
    // single-control editor like AssetRef/Color) -> simple one-line list. Otherwise a nested type with
    // its own fields -> the per-element foldout list.
    private static bool IsLeafElementType(Type t, PropertyGridConfig config)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)) return true;
        if (config.Drawers.GetDrawer(t) != null) return true;
        if (config.IsSimpleFieldType?.Invoke(t) == true) return true;
        return GetSerializableFields(t).Length == 0;
    }

    private static void DrawCollection(Paper paper, string id, Type collectionType,
        IList? list, PropertyGridConfig config, Action<object?> onChange, int depth, string label = "")
    {
        Type elementType = GetListElementType(collectionType);

        var theme = Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;
        if (font == null) return;
        var semi = theme.SemiBold ?? font;
        var mono = theme.Mono ?? font;

        UnitValue ST = UnitValue.Stretch();
        var bd = theme.BorderSoft;
        var glass = theme.Glass;
        var tHi = theme.Ink.C500; var tMid = theme.Ink.C300; var tLo = theme.Ink.C200; var tDim = theme.Ink.C100;
        var acc = theme.Primary.C700;
        var redBg = System.Drawing.Color.FromArgb(40, theme.Red.C500);

        // Null collection: a full-width Create button.
        if (list == null)
        {
            using (paper.Row($"{id}_null").Width(ST).Height(UnitValue.Auto).Padding(m.PaddingLarge, m.PaddingLarge, 0, 0).Enter())
                Origami.Button(paper, $"{id}_create", "Create", () =>
                    onChange(collectionType.IsArray ? Array.CreateInstance(elementType, 0) : Activator.CreateInstance(collectionType))).Show();
            return;
        }

        // Stable ids keep Paper element identity across reorders/removals.
        var colEl = paper.CurrentParent;
        var stableIds = paper.GetElementStorage<List<string>>(colEl, "stableIds", null!) ?? new List<string>();
        while (stableIds.Count < list.Count) stableIds.Add(Guid.NewGuid().ToString("N")[..8]);
        while (stableIds.Count > list.Count) stableIds.RemoveAt(stableIds.Count - 1);
        paper.SetElementStorage(colEl, "stableIds", stableIds);

        void Commit() => paper.SetElementStorage(colEl, "stableIds", stableIds);

        void RemoveAt(int i)
        {
            if (i < 0 || i >= list.Count) return;
            stableIds.RemoveAt(i); Commit();
            if (collectionType.IsArray)
            {
                var a = Array.CreateInstance(elementType, list.Count - 1);
                for (int j = 0, k = 0; j < list.Count; j++) if (j != i) a.SetValue(list[j], k++);
                onChange(a);
            }
            else
            {
                var l = (IList)Activator.CreateInstance(list.GetType())!;
                for (int j = 0; j < list.Count; j++) if (j != i) l.Add(list[j]);
                onChange(l);
            }
        }

        void AddNew()
        {
            // Concrete reference types with a default ctor are instantiated; abstract/interface (or
            // ctor-less) element types are added as null and get a type picker to assign an instance.
            object? ne;
            if (elementType.IsValueType) ne = Activator.CreateInstance(elementType);
            else if (elementType == typeof(string)) ne = "";
            else if (!elementType.IsAbstract && !elementType.IsInterface && elementType.GetConstructor(Type.EmptyTypes) != null)
                ne = Activator.CreateInstance(elementType);
            else ne = null;
            stableIds.Add(Guid.NewGuid().ToString("N")[..8]); Commit();
            if (collectionType.IsArray)
            {
                var a = Array.CreateInstance(elementType, list.Count + 1);
                for (int j = 0; j < list.Count; j++) a.SetValue(list[j], j);
                a.SetValue(ne, list.Count); onChange(a);
            }
            else
            {
                var l = (IList)Activator.CreateInstance(list.GetType())!;
                for (int j = 0; j < list.Count; j++) l.Add(list[j]);
                l.Add(ne); onChange(l);
            }
        }

        void MoveTo(int from, int to)
        {
            if (from == to || from < 0 || from >= list.Count || to < 0 || to >= list.Count) return;
            var v = list[from]; var s = stableIds[from];
            if (from < to) for (int j = from; j < to; j++) { list[j] = list[j + 1]; stableIds[j] = stableIds[j + 1]; }
            else for (int j = from; j > to; j--) { list[j] = list[j - 1]; stableIds[j] = stableIds[j - 1]; }
            list[to] = v; stableIds[to] = s; Commit(); onChange(list);
        }

        // Drag reorder from the grip. Each visible row records its centre-Y via OnPostLayout; while
        // dragging, the row whose centre is nearest the pointer becomes the drop target. This is
        // accurate regardless of individual row heights (nested cards expand to different sizes).
        void CaptureRow(int i, ElementBuilder el) => el.OnPostLayout((h, r) =>
        {
            var cys = paper.GetElementStorage<List<float>>(colEl, "rowCys", null!);
            if (cys == null) { cys = new List<float>(); paper.SetElementStorage(colEl, "rowCys", cys); }
            while (cys.Count <= i) cys.Add(0f);
            cys[i] = (float)(r.Min.Y + r.Size.Y * 0.5f);
        });
        bool BeingDragged(string sk) => paper.GetElementStorage<string>(colEl, "dragSk", null!) == sk;
        void GripDrag(string sk, ElementBuilder grip)
        {
            grip.OnDragStart(sk, (k, _) => paper.SetElementStorage(colEl, "dragSk", k))
                .OnDragging(sk, (k, _) =>
                {
                    int cur = stableIds.IndexOf(k);
                    var cys = paper.GetElementStorage<List<float>>(colEl, "rowCys", null!);
                    if (cur < 0 || cys == null) return;
                    float py = (float)paper.PointerPos.Y;
                    int target = cur; float best = float.MaxValue;
                    for (int t = 0; t < cys.Count && t < list.Count; t++)
                    {
                        float d = MathF.Abs(cys[t] - py);
                        if (d < best) { best = d; target = t; }
                    }
                    MoveTo(cur, target);
                })
                .OnDragEnd(_ => paper.SetElementStorage(colEl, "dragSk", (string?)null))
                .Cursor(PaperCursor.Grab).CursorDragging(PaperCursor.Grabbing);
        }

        System.Drawing.Color DepthColor(int d) => d switch
        {
            0 => System.Drawing.Color.FromArgb(128, theme.Primary.C500),
            1 => System.Drawing.Color.FromArgb(128, theme.Blue.C500),
            _ => System.Drawing.Color.FromArgb(128, 217, 107, 216),
        };

        // ── Simple (leaf element) list ─────────────────────────
        void DrawSimple()
        {
            const float rowH = 30f;
            using (paper.Column($"{id}_coll").Width(ST).Height(UnitValue.Auto)
                .Rounded(8).BorderColor(bd).BorderWidth(1).Clip().Enter())
            {
                var listEl = paper.CurrentParent;
                bool expanded = paper.GetElementStorage<bool>(listEl, "exp", config.ExpandByDefault);
                float anim = paper.AnimateBool(expanded, 0.18f, id: $"{id}_se");

                using (paper.Row($"{id}_ch").Width(ST).Height(26).RoundedTop(8).Padding(m.SpacingLarge, m.SpacingLarge, 0, 0).RowBetween(m.SpacingMedium).BackgroundColor(glass)
                    .Hovered.BackgroundColor(theme.Hover).End()
                    .OnClick(0, (_, _) => paper.SetElementStorage(listEl, "exp", !paper.GetElementStorage<bool>(listEl, "exp", config.ExpandByDefault)))
                    .Cursor(PaperCursor.Pointer)
                    .Enter())
                {
                    paper.Box($"{id}_ci").Width(14).Height(26).Margin(0, 0, ST, ST).IsNotInteractable().Icon(paper, OrigamiIconSet.Layers, acc, size: 12f);
                    var chLabel = paper.Box($"{id}_cc").Width(ST).Height(26).IsNotInteractable()
                        .Text(string.IsNullOrEmpty(label) ? $"{list.Count} elements" : $"{label}  ({list.Count})", font)
                        .TextColor(tMid).FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft);
                    if (CurrentTooltip is string ttSimple) chLabel.Tooltip(ttSimple);
                    paper.Box($"{id}_cv").Width(12).Height(26).Margin(0, 0, ST, ST).IsNotInteractable()
                        .Icon(paper, expanded ? OrigamiIconSet.ChevronDown : OrigamiIconSet.ChevronRight, tLo, size: 11f);
                }

                if (!expanded && anim <= 0.001f) return;

                using (paper.Column($"{id}_body").Width(ST).Height(UnitValue.Lerp(0, UnitValue.Auto, anim)).Clip().Enter())
                {
                    paper.Box($"{id}_chd").Width(ST).Height(1).BackgroundColor(bd).IsNotInteractable();

                    for (int i = 0; i < list.Count; i++)
                    {
                        int idx = i; string sk = stableIds[i];
                        var rowB = paper.Row($"{id}_r_{sk}").Width(ST).Height(UnitValue.Auto).MinHeight(rowH).Padding(m.PaddingSmall, m.Padding, m.PaddingSmall, m.PaddingSmall).RowBetween(m.SpacingLarge);
                        if (BeingDragged(sk)) rowB.BackgroundColor(theme.Selected);
                        CaptureRow(idx, rowB);
                        using (rowB.Enter())
                        {
                            var grip = paper.Box($"{id}_g_{sk}").Width(12).Height(rowH).Icon(paper, OrigamiIconSet.Grip, tDim, size: 13f);
                            GripDrag(sk, grip);

                            paper.Box($"{id}_i_{sk}").Width(14).Height(rowH).IsNotInteractable()
                                .Text(idx.ToString(), mono).TextColor(tDim).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter);

                            using (paper.Box($"{id}_v_{sk}").Width(ST).Height(UnitValue.Auto).MinHeight(m.RowHeight).Enter())
                                DrawFieldControl(paper, $"{id}_el_{sk}", elementType, list[idx], config, v => { list[idx] = v; onChange(list); }, depth + 1);

                            paper.Box($"{id}_x_{sk}").Width(18).Height(18).Rounded(4).Margin(0, 0, ST, ST)
                                .Hovered.BackgroundColor(redBg).End()
                                .Icon(paper, OrigamiIconSet.Close, tLo, size: 11f)
                                .OnClick(idx, (j, _) => RemoveAt(j)).Cursor(PaperCursor.Pointer);
                        }
                        if (i < list.Count - 1)
                            paper.Box($"{id}_d_{sk}").Width(ST).Height(1).BackgroundColor(bd).IsNotInteractable();
                    }

                    paper.Box($"{id}_add").Width(ST).Height(26)
                        .Hovered.BackgroundColor(theme.Hover).End()
                        .Text("+ Add Element", font).TextColor(acc).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter)
                        .OnClick(0, (_, _) => AddNew()).Cursor(PaperCursor.Pointer);
                }
            }
        }

        // ── One nested element card (foldout with its own property fields) ──
        void DrawElement(int i, string sk)
        {
            const float rowH = 28f;
            var cardB = paper.Column($"{id}_nel_{sk}").Width(ST).Height(UnitValue.Auto)
                .Rounded(8).BorderColor(bd).BorderWidth(1)
                .BackgroundColor(BeingDragged(sk) ? theme.Selected : System.Drawing.Color.FromArgb(6, 255, 255, 255)).Clip();
            CaptureRow(i, cardB);
            using (cardB.Enter())
            {
                var nelEl = paper.CurrentParent;
                bool exp = paper.GetElementStorage<bool>(nelEl, "exp", config.ExpandByDefault);

                var nh = paper.Row($"{id}_nh_{sk}").Width(ST).Height(UnitValue.Auto).MinHeight(rowH).Padding(m.SpacingLarge, m.SpacingLarge, 0, 0).RowBetween(m.SpacingMedium)
                    .Hovered.BackgroundColor(theme.Hover).End()
                    .OnClick(sk, (k, _) => paper.SetElementStorage(nelEl, "exp", !paper.GetElementStorage<bool>(nelEl, "exp", config.ExpandByDefault)))
                    .Cursor(PaperCursor.Pointer);
                if (exp) nh.RoundedTop(8); else nh.Rounded(8);   // hover fill follows the card corners
                using (nh.Enter())
                {
                    // Absorb the click (so the grip doesn't toggle the card header's expand) per-event
                    // rather than blanket .StopEventPropagation(), so the wheel still bubbles to a
                    // parent ScrollView. The reorder drag (GripDrag) bubbles harmlessly - the header
                    // has no drag handler.
                    var grip = paper.Box($"{id}_ng_{sk}").Width(12).Height(rowH).OnClick(e => e.StopPropagation()).Icon(paper, OrigamiIconSet.Grip, tDim, size: 12f);
                    GripDrag(sk, grip);

                    paper.Box($"{id}_nc_{sk}").Width(11).Height(rowH).IsNotInteractable()
                        .Icon(paper, exp ? OrigamiIconSet.ChevronDown : OrigamiIconSet.ChevronRight, tLo, size: 11f);

                    paper.Box($"{id}_ni_{sk}").Width(UnitValue.Auto).Height(rowH).IsNotInteractable()
                        .Text(i.ToString(), mono).TextColor(tDim).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter);

                    // Show the element's actual concrete type, not the list's (base) element type.
                    paper.Box($"{id}_nt_{sk}").Width(ST).Height(rowH).IsNotInteractable()
                        .Text(list[i]?.GetType().Name ?? elementType.Name, semi).TextColor(tHi).FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft).TextTruncate();

                    paper.Box($"{id}_nx_{sk}").Width(18).Height(18).Rounded(4).Margin(0, 0, ST, ST)
                        .Hovered.BackgroundColor(redBg).End()
                        .Icon(paper, OrigamiIconSet.Close, tLo, size: 11f)
                        .OnClick(i, (j, e) => { e.StopPropagation(); RemoveAt(j); }).Cursor(PaperCursor.Pointer);
                }

                if (exp)
                {
                    paper.Box($"{id}_nd_{sk}").Width(ST).Height(1).BackgroundColor(bd).IsNotInteractable();
                    using (paper.Column($"{id}_nbody_{sk}").Width(ST).Height(UnitValue.Auto).Padding(0, 0, m.SpacingSmall, m.PaddingSmall).Enter())
                    {
                        var elem = list[i];
                        int capIdx = i;
                        var customEd = elem != null ? config.CustomEditors.GetEditor(elem.GetType()) : null;
                        if (customEd != null)
                            customEd.OnGUI(paper, $"{id}_nce_{sk}", elem!);
                        else if (elem != null)
                            Draw(paper, $"{id}_nbf_{sk}", elem, config, changed => { list[capIdx] = changed; onChange(list); }, null, depth + 1);
                        else
                            // Null element: a type picker to assign / instantiate a value.
                            using (paper.Row($"{id}_npw_{sk}").Width(ST).Height(UnitValue.Auto).Padding(m.SpacingLarge, m.SpacingLarge, m.PaddingSmall, m.PaddingSmall).Enter())
                                config.DrawTypePicker?.Invoke(paper, $"{id}_np_{sk}", elementType, null, v => { list[capIdx] = v; onChange(list); });
                    }
                }
            }
        }

        // ── Nested (complex element) list ──────────────────────
        void DrawNested()
        {
            var color = DepthColor(depth);
            using (paper.Column($"{id}_nl").Width(ST).Height(UnitValue.Auto)
                .Rounded(9).BorderColor(bd).BorderWidth(1).BackgroundColor(System.Drawing.Color.FromArgb(36, 0, 0, 0)).Clip().Enter())
            {
                var listEl = paper.CurrentParent;
                bool expanded = paper.GetElementStorage<bool>(listEl, "exp", config.ExpandByDefault);
                float anim = paper.AnimateBool(expanded, 0.18f, id: $"{id}_ne");

                using (paper.Row($"{id}_nlh").Width(ST).Height(30).RoundedTop(9).Padding(m.SpacingLarge, m.SpacingLarge, 0, 0).RowBetween(m.SpacingLarge).BackgroundColor(glass)
                    .Hovered.BackgroundColor(theme.Hover).End()
                    .OnClick(0, (_, _) => paper.SetElementStorage(listEl, "exp", !paper.GetElementStorage<bool>(listEl, "exp", config.ExpandByDefault)))
                    .Cursor(PaperCursor.Pointer)
                    .Enter())
                {
                    paper.Box($"{id}_nli").Width(14).Height(30).Margin(0, 0, ST, ST).IsNotInteractable().Icon(paper, OrigamiIconSet.Layers, color, size: 13f);
                    var nlLabel = paper.Box($"{id}_nlt").Width(UnitValue.Auto).Height(30).IsNotInteractable()
                        .Text(string.IsNullOrEmpty(label) ? elementType.Name : label, semi).TextColor(tHi).FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft);
                    if (CurrentTooltip is string ttNested) nlLabel.Tooltip(ttNested);
                    paper.Box($"{id}_nlc").Width(ST).Height(30).Margin(4, 0, 0, 0).IsNotInteractable()
                        .Text($"[{list.Count}]", mono).TextColor(tLo).FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft);
                    paper.Box($"{id}_nlv").Width(12).Height(30).Margin(0, 0, ST, ST).IsNotInteractable()
                        .Icon(paper, expanded ? OrigamiIconSet.ChevronDown : OrigamiIconSet.ChevronRight, tLo, size: 11f);
                }

                if (!expanded && anim <= 0.001f) return;

                using (paper.Column($"{id}_nlwrap").Width(ST).Height(UnitValue.Lerp(0, UnitValue.Auto, anim)).Clip().Enter())
                {
                    paper.Box($"{id}_nlhd").Width(ST).Height(1).BackgroundColor(bd).IsNotInteractable();

                    using (paper.Column($"{id}_nlb").Width(ST).Height(UnitValue.Auto).Padding(m.Padding, m.Padding, m.Padding, m.Padding).ColBetween(m.SpacingMedium).Enter())
                    {
                        if (list.Count == 0)
                            paper.Box($"{id}_nle").Width(ST).Height(24).IsNotInteractable()
                                .Text("Empty list", font).TextColor(tDim).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter);
                        for (int i = 0; i < list.Count; i++)
                            DrawElement(i, stableIds[i]);

                        paper.Box($"{id}_nladd").Width(ST).Height(26).Rounded(6)
                            .Hovered.BackgroundColor(theme.Hover).End()
                            .Text($"+ Add {elementType.Name}", font).TextColor(acc).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter)
                            .OnClick(0, (_, _) => AddNew()).Cursor(PaperCursor.Pointer);
                    }
                }
            }
        }

        if (IsLeafElementType(elementType, config))
            DrawSimple();
        else
            DrawNested();
    }

    // ── Dictionary ──────────────────────────────────────────

    /// <summary>True when the type is a generic IDictionary with an editable (leaf) key type.
    /// Shape is cached per type: this runs per field per frame on the DrawField path and
    /// GetGenericArguments allocates a fresh RuntimeType[] every call.</summary>
    private static bool IsEditableDictionary(Type type, out Type keyType, out Type valueType)
    {
        var shape = s_dictShape.GetValue(type, static t =>
        {
            var s = new DictShape();
            if (typeof(IDictionary).IsAssignableFrom(t) && t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args.Length == 2)
                {
                    s.Key = args[0];
                    s.Value = args[1];
                    s.Editable = IsSupportedDictionaryKey(args[0]);
                }
            }
            return s;
        });
        keyType = shape.Key!;
        valueType = shape.Value!;
        return shape.Editable;
    }

    private sealed class DictShape { public bool Editable; public Type? Key; public Type? Value; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, DictShape> s_dictShape = new();

    /// <summary>Element type of an IList collection type (array or generic), cached per type -
    /// the uncached expression allocates a RuntimeType[] per call on the per-frame draw path.</summary>
    private static Type GetListElementType(Type collectionType)
        => s_listElemType.GetValue(collectionType,
            static t => t.IsArray ? t.GetElementType()! : t.GetGenericArguments()[0]);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, Type> s_listElemType = new();

    /// <summary>Names + boxed values of an enum type, cached - Enum.GetNames/GetValues allocate
    /// fresh arrays every call and enum fields redraw every frame. Callers must not mutate.</summary>
    private static EnumMeta GetEnumMeta(Type enumType)
        => s_enumMeta.GetValue(enumType,
            static e => new EnumMeta { Names = Enum.GetNames(e), Values = Enum.GetValues(e) });

    private sealed class EnumMeta { public string[] Names = null!; public Array Values = null!; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, EnumMeta> s_enumMeta = new();

    private static bool IsSupportedDictionaryKey(Type t)
        => t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal);

    /// <summary>
    /// Two-column key/value dictionary editor (Unity 6.6-style). Entries are edited in place;
    /// duplicate keys are rejected inline with a red highlight. Keys need IComparable for the
    /// sort toggle. The dictionary instance itself is mutated (never replaced), so host undo
    /// snapshots keep working.
    /// </summary>
    private static void DrawDictionary(Paper paper, string id, Type dictType, IDictionary? dict,
        PropertyGridConfig config, Action<object?> onChange, int depth, string label = "")
    {
        var args = dictType.GetGenericArguments();
        Type keyType = args[0], valueType = args[1];

        var theme = Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;
        if (font == null) return;
        var semi = theme.SemiBold ?? font;
        var mono = theme.Mono ?? font;

        UnitValue ST = UnitValue.Stretch();
        var bd = theme.BorderSoft;
        var glass = theme.Glass;
        var acc = theme.Primary.C700;
        var redBg = System.Drawing.Color.FromArgb(40, theme.Red.C500);

        if (dict == null)
        {
            using (paper.Row($"{id}_null").Width(ST).Height(UnitValue.Auto).Padding(m.PaddingLarge, m.PaddingLarge, 0, 0).Enter())
                Origami.Button(paper, $"{id}_create", "Create", () => onChange(Activator.CreateInstance(dictType))).Show();
            return;
        }

        bool sortable = typeof(IComparable).IsAssignableFrom(keyType);

        using (paper.Column($"{id}_dict").Width(ST).Height(UnitValue.Auto)
            .Rounded(8).BorderColor(bd).BorderWidth(1).Clip().Enter())
        {
            var dictEl = paper.CurrentParent;
            bool expanded = paper.GetElementStorage<bool>(dictEl, "exp", config.ExpandByDefault);
            float anim = paper.AnimateBool(expanded, 0.18f, id: $"{id}_de");

            using (paper.Row($"{id}_dh").Width(ST).Height(26).RoundedTop(8).Padding(m.SpacingLarge, m.SpacingLarge, 0, 0).RowBetween(m.SpacingMedium).BackgroundColor(glass)
                .Hovered.BackgroundColor(theme.Hover).End()
                .OnClick(0, (_, _) => paper.SetElementStorage(dictEl, "exp", !paper.GetElementStorage<bool>(dictEl, "exp", config.ExpandByDefault)))
                .Cursor(PaperCursor.Pointer)
                .Enter())
            {
                paper.Box($"{id}_di").Width(14).Height(26).Margin(0, 0, ST, ST).IsNotInteractable()
                    .Text("{}", mono).TextColor(acc).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter);
                var dhLabel = paper.Box($"{id}_dc").Width(ST).Height(26).IsNotInteractable()
                    .Text(string.IsNullOrEmpty(label) ? $"{dict.Count} entries" : $"{label}  ({dict.Count})", font)
                    .TextColor(theme.Ink.C300).FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft);
                if (CurrentTooltip is string ttDict) dhLabel.Tooltip(ttDict);

                if (sortable && dict.Count > 1)
                {
                    bool sorted = paper.GetElementStorage<bool>(dictEl, "sorted", false);
                    paper.Box($"{id}_dsort").Width(UnitValue.Auto).Height(26).Padding(6, 6, 0, 0)
                        .Hovered.BackgroundColor(theme.Hover).End()
                        .Text(sorted ? "↓ key" : "↑ key", mono).TextColor(theme.Ink.C200).FontSize(m.FontSize - 1)
                        .Alignment(TextAlignment.MiddleCenter)
                        .OnClick(0, (_, _) => paper.SetElementStorage(dictEl, "sorted", !paper.GetElementStorage<bool>(dictEl, "sorted", false)))
                        .Cursor(PaperCursor.Pointer);
                }

                paper.Box($"{id}_dv").Width(12).Height(26).Margin(0, 0, ST, ST).IsNotInteractable()
                    .Text(expanded ? "▾" : "▸", font).TextColor(theme.Ink.C200).FontSize(11f).Alignment(TextAlignment.MiddleCenter);
            }

            if (!expanded && anim <= 0.001f) return;

            using (paper.Column($"{id}_dbody").Width(ST).Height(UnitValue.Lerp(0, UnitValue.Auto, anim)).Clip().Enter())
            {
                paper.Box($"{id}_dhd").Width(ST).Height(1).BackgroundColor(bd).IsNotInteractable();

                // Snapshot entries so in-frame mutations (an OnClick adding/removing) don't
                // invalidate the enumeration; optionally sorted by key.
                var entries = new List<DictionaryEntry>(dict.Count);
                foreach (DictionaryEntry kvp in dict)
                    entries.Add(kvp);
                if (sortable && paper.GetElementStorage<bool>(dictEl, "sorted", false))
                    entries.Sort((a, b) => ((IComparable)a.Key).CompareTo(b.Key));

                if (entries.Count == 0)
                    paper.Box($"{id}_dempty").Width(ST).Height(24).IsNotInteractable()
                        .Text("Empty", font).TextColor(theme.Ink.C100).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter);

                for (int i = 0; i < entries.Count; i++)
                {
                    int idx = i;
                    object key = entries[i].Key;

                    var rowB = paper.Row($"{id}_dr_{idx}").Width(ST).Height(UnitValue.Auto).MinHeight(m.RowHeight)
                        .Padding(m.PaddingSmall, m.Padding, m.PaddingSmall, m.PaddingSmall).RowBetween(m.Spacing);
                    // A duplicate-key edit attempt is flagged in element storage until cleared.
                    if (paper.GetElementStorage<string>(dictEl, $"dup_{idx}", null!) == KeyText(key))
                        rowB.BorderColor(theme.Red.C500).BorderWidth(1);
                    using (rowB.Enter())
                    {
                        // Key editor - committing a key that already exists is rejected and flagged.
                        using (paper.Box($"{id}_dk_{idx}").Width(m.LabelWidth).Height(UnitValue.Auto).MinHeight(m.RowHeight).Enter())
                            DrawFieldControl(paper, $"{id}_dkc_{idx}", keyType, key, config, v =>
                            {
                                if (Equals(v, key)) return;
                                if (v == null || dict.Contains(v))
                                {
                                    paper.SetElementStorage(dictEl, $"dup_{idx}", KeyText(key));
                                    return;
                                }
                                paper.SetElementStorage(dictEl, $"dup_{idx}", (string?)null);
                                var val = dict[key];
                                dict.Remove(key);
                                dict[v] = val;
                                onChange(dict);
                            }, depth + 1);

                        using (paper.Box($"{id}_dvc_{idx}").Width(ST).Height(UnitValue.Auto).MinHeight(m.RowHeight).Enter())
                            DrawFieldControl(paper, $"{id}_dval_{idx}", valueType, dict[key], config, v =>
                            {
                                dict[key] = v;
                                onChange(dict);
                            }, depth + 1);

                        paper.Box($"{id}_dx_{idx}").Width(18).Height(18).Rounded(4).Margin(0, 0, ST, ST)
                            .Hovered.BackgroundColor(redBg).End()
                            .Text("×", font).TextColor(theme.Ink.C200).FontSize(11f).Alignment(TextAlignment.MiddleCenter)
                            .OnClick(key, (k, _) => { dict.Remove(k!); onChange(dict); }).Cursor(PaperCursor.Pointer);
                    }

                    if (i < entries.Count - 1)
                        paper.Box($"{id}_dd_{idx}").Width(ST).Height(1).BackgroundColor(bd).IsNotInteractable();
                }

                // Duplicate-key feedback is the red row border above; add-entry below.
                paper.Box($"{id}_dadd").Width(ST).Height(26)
                    .Hovered.BackgroundColor(theme.Hover).End()
                    .Text("+ Add Entry", font).TextColor(acc).FontSize(m.FontSize).Alignment(TextAlignment.MiddleCenter)
                    .OnClick(0, (_, _) =>
                    {
                        var nk = MakeDefaultKey(keyType, dict);
                        if (nk == null || dict.Contains(nk)) return;
                        dict[nk] = MakeDefaultValue(valueType);
                        onChange(dict);
                    }).Cursor(PaperCursor.Pointer);
            }
        }

        static string KeyText(object? k) => k?.ToString() ?? "";
    }

    private static object? MakeDefaultKey(Type keyType, IDictionary dict)
    {
        try
        {
            if (keyType == typeof(string))
            {
                for (int i = 0; i < 1000; i++)
                {
                    var candidate = i == 0 ? "key" : $"key {i + 1}";
                    if (!dict.Contains(candidate)) return candidate;
                }
                return null;
            }
            if (keyType.IsEnum)
            {
                foreach (var v in Enum.GetValues(keyType))
                    if (!dict.Contains(v)) return v;
                return null;
            }
            for (int i = 0; i < 1000; i++)
            {
                if (i > 120) break;
                var candidate = Convert.ChangeType(i, keyType);
                if (!dict.Contains(candidate)) return candidate;
            }
            return null;
        }
        catch { return null; }
    }

    private static object? MakeDefaultValue(Type valueType)
    {
        if (valueType.IsValueType) return Activator.CreateInstance(valueType);
        if (valueType == typeof(string)) return "";
        if (!valueType.IsAbstract && !valueType.IsInterface && valueType.GetConstructor(Type.EmptyTypes) != null)
            return Activator.CreateInstance(valueType);
        return null;
    }

    // ── Nested Object ────────────────────────────────────────

    private static void DrawNestedObject(Paper paper, string id, Type declaredType,
        object value, PropertyGridConfig config, Action<object?> onChange, int depth)
    {
        if (depth + 1 > config.MaxDepth) return;
        var actualType = value.GetType();

        Origami.Foldout(paper, $"{id}_fold", actualType.Name).DefaultExpanded(config.ExpandByDefault).Body(() =>
        {
            if (declaredType.IsAbstract || declaredType.IsInterface)
                config.DrawTypePicker?.Invoke(paper, $"{id}_pick", declaredType, value, onChange);

            Draw(paper, $"{id}_inner", value, config, changed =>
            {
                if (actualType.IsValueType) onChange(changed); else onChange(value);
            }, null, depth + 1);
        });
    }

    private static void DrawNestedObjectWithCustomEditor(Paper paper, string id, Type declaredType,
        object value, PropertyGridConfig config, Action<object?> onChange, int depth, CustomObjectEditor editor)
    {
        if (depth + 1 > config.MaxDepth) return;

        Origami.Foldout(paper, $"{id}_fold", value.GetType().Name).DefaultExpanded(config.ExpandByDefault).Body(() =>
        {
            if (declaredType.IsAbstract || declaredType.IsInterface)
                config.DrawTypePicker?.Invoke(paper, $"{id}_pick", declaredType, value, onChange);

            editor.OnGUI(paper, $"{id}_custom", value);
        });
    }

    // ── Null Object ──────────────────────────────────────────

    private static void DrawNullObject(Paper paper, string id, Type fieldType, PropertyGridConfig config, Action<object?> onChange)
    {
        var theme = Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;

        using (paper.Row($"{id}_null").Height(m.RowHeight).RowBetween(m.SpacingMedium).Enter())
        {
            if (font != null)
                paper.Box($"{id}_null_lbl").Width(UnitValue.Stretch()).Height(m.RowHeight)
                    .Text("(null)", font).TextColor(theme.Ink.C300)
                    .FontSize(m.FontSize).Alignment(TextAlignment.MiddleLeft)
                    .IsNotInteractable();

            if (!fieldType.IsAbstract && !fieldType.IsInterface)
            {
                Origami.Button(paper, $"{id}_create", "Create", () =>
                {
                    try { onChange(Activator.CreateInstance(fieldType)); }
                    catch { }
                }).Show();
            }
            else
            {
                config.DrawTypePicker?.Invoke(paper, $"{id}_pick", fieldType, null, onChange);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────

    // Apply an edited field value to every target (resolving the field per target type so it works even
    // when the selection is a mix of types that share the field), then notify each.
    private static void SetFieldOnAllAndNotify(PropertyGridConfig config, FieldInfo field,
        IReadOnlyList<object> targets, object? value, Action<object>? rootOnChange)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t == null) continue;
            var f = t.GetType() == field.DeclaringType ? field : ResolveField(t.GetType(), field.Name);
            if (f == null) continue;
            try { f.SetValue(t, value); } catch { continue; }
            config.OnFieldChanged?.Invoke(t);
        }
        rootOnChange?.Invoke(targets[0]);
    }

    /// <summary>The serializable fields shared (by name and type) across every target.</summary>
    public static FieldInfo[] CommonSerializableFields(IReadOnlyList<object> targets) => CommonSerializableFields(targets, false);

    /// <summary>The serializable fields shared (by name and type) across every target.</summary>
    public static FieldInfo[] CommonSerializableFields(IReadOnlyList<object> targets, bool includePrivate)
    {
        var rep = GetSerializableFields(targets[0].GetType(), includePrivate);
        if (targets.Count == 1) return rep;

        var result = new List<FieldInfo>(rep.Length);
        foreach (var f in rep)
        {
            bool inAll = true;
            for (int i = 1; i < targets.Count; i++)
            {
                if (targets[i] == null) { inAll = false; break; }
                var tf = ResolveField(targets[i].GetType(), f.Name);
                if (tf == null || tf.FieldType != f.FieldType) { inAll = false; break; }
            }
            if (inAll) result.Add(f);
        }
        return result.ToArray();
    }

    private static FieldInfo? ResolveField(Type type, string name)
    {
        foreach (var f in GetSerializableFields(type))
            if (f.Name == name) return f;
        return null;
    }

    private static bool IsFieldMixed(IReadOnlyList<object> targets, FieldInfo repField)
    {
        object? first = null;
        bool got = false;
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (t == null) continue;
            var f = ResolveField(t.GetType(), repField.Name);
            if (f == null) continue;
            var v = f.GetValue(t);
            if (!got) { first = v; got = true; }
            else if (!ValuesEqual(first, v)) return true;
        }
        return false;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return a.Equals(b);
    }

    /// <summary>Convert "myFieldName" to "My Field Name".</summary>
    public static string FormatFieldName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.StartsWith("m_") && name.Length > 2) name = name[2..];
        else if (name.StartsWith('_') && name.Length > 1) name = name[1..];

        var sb = new System.Text.StringBuilder(name.Length + 4);
        sb.Append(char.ToUpper(name[0]));
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    // ── Numeric drag helpers ────────────────────────────────

    private static readonly HashSet<Type> s_numericTypes = new()
    {
        typeof(float), typeof(double), typeof(decimal),
        typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(short), typeof(ushort), typeof(byte), typeof(sbyte),
    };

    private static bool IsNumericType(Type type) => s_numericTypes.Contains(type);

    private static double ConvertToDouble(object? value)
    {
        if (value == null) return 0;
        try { return Convert.ToDouble(value); }
        catch { return 0; }
    }

    private static object? ConvertFromDouble(double value, Type targetType)
    {
        try
        {
            if (targetType == typeof(float)) return (float)value;
            if (targetType == typeof(double)) return value;
            if (targetType == typeof(int)) return (int)Math.Round(value);
            if (targetType == typeof(uint)) return (uint)Math.Max(0, Math.Round(value));
            if (targetType == typeof(long)) return (long)Math.Round(value);
            if (targetType == typeof(ulong)) return (ulong)Math.Max(0, Math.Round(value));
            if (targetType == typeof(short)) return (short)Math.Round(value);
            if (targetType == typeof(ushort)) return (ushort)Math.Max(0, Math.Round(value));
            if (targetType == typeof(byte)) return (byte)Math.Clamp(Math.Round(value), 0, 255);
            if (targetType == typeof(sbyte)) return (sbyte)Math.Clamp(Math.Round(value), -128, 127);
            if (targetType == typeof(decimal)) return (decimal)value;
            return Convert.ChangeType(value, targetType);
        }
        catch { return null; }
    }

    // Scrubbing must saturate at integral limits rather than wrap or throw.
    private static object? ConvertScrubFromDouble(double value, Type targetType)
    {
        if (!double.IsFinite(value)) return null;
        if (targetType == typeof(int)) return int.CreateSaturating(value);
        if (targetType == typeof(uint)) return uint.CreateSaturating(value);
        if (targetType == typeof(long)) return long.CreateSaturating(value);
        if (targetType == typeof(ulong)) return ulong.CreateSaturating(value);
        if (targetType == typeof(short)) return short.CreateSaturating(value);
        if (targetType == typeof(ushort)) return ushort.CreateSaturating(value);
        if (targetType == typeof(byte)) return byte.CreateSaturating(value);
        if (targetType == typeof(sbyte)) return sbyte.CreateSaturating(value);
        if (targetType == typeof(float)) return (float)Math.Clamp(value, -float.MaxValue, float.MaxValue);
        if (targetType == typeof(decimal)) return decimal.CreateSaturating(value);
        return ConvertFromDouble(value, targetType);
    }

    // Reflection results are invariant per type/field, but the inspector rebuilds every frame
    // (immediate mode), so cache them. Keyed weakly so hot-reloaded script types don't pin their
    // (collectible) AssemblyLoadContext - entries evict when the type is unloaded.
    private sealed class FieldMeta
    {
        public Attribute[] Attributes = Array.Empty<Attribute>();
        public string Label = "";
    }
    private sealed class ButtonMethod
    {
        public MethodInfo Method = null!;
        public string Label = "";
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, FieldInfo[]> s_fieldCache = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, FieldInfo[]> s_fieldCacheAll = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FieldInfo, FieldMeta> s_fieldMeta = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, ButtonMethod[]> s_buttonCache = new();

    private static readonly Dictionary<(string parent, string suffix), string> s_idCache = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Paper, FieldApplySurface> s_applySurfaces = new();

    private sealed class FieldApplySurface
    {
        internal readonly Dictionary<string, FieldApplyState> Fields = new();
        internal FieldApplySurface(Paper paper)
        {
            paper.OnEndOfFramePostLayout += () =>
            {
                // .NET permits Dictionary.Remove during enumeration. Retain visible fields only.
                foreach (var entry in Fields)
                    if (!entry.Value.Used) Fields.Remove(entry.Key);
                    else entry.Value.Used = false;
            };
        }
    }

    /// <summary>
    /// Drop per-field apply delegates. Each entry holds the last <see cref="FieldInfo"/> and
    /// live target instances; leaving them across a script unload pins the collectible ALC
    /// and forces an editor restart.
    /// </summary>
    public static void ClearTransientCaches()
    {
        foreach (var entry in s_applySurfaces) entry.Value.Fields.Clear();
    }

    /// <summary>
    /// Stable Paper element id for a parent+suffix pair. Immediate-mode grids call this every
    /// field every frame; caching avoids a new string per widget.
    /// </summary>
    public static string InternFieldId(string parent, string suffix)
    {
        var key = (parent, suffix);
        if (!s_idCache.TryGetValue(key, out string? id))
        {
            // Selection-specific grid IDs must not grow a process-lifetime intern table.
            if (s_idCache.Count >= 8192) s_idCache.Clear();
            id = parent + "_" + suffix;
            s_idCache[key] = id;
        }
        return id;
    }

    private sealed class FieldApplyState
    {
        public FieldInfo Field = null!;
        public IReadOnlyList<object> Targets = Array.Empty<object>();
        public PropertyGridConfig Config = null!;
        public Action<object>? OnChange;
        public readonly Action<object?> Apply;
        public bool Used;
        public FieldApplyState() => Apply = v => SetFieldOnAllAndNotify(Config, Field, Targets, v, OnChange);
        public bool Matches(IReadOnlyList<object> targets)
        {
            if (Targets.Count != targets.Count) return false;
            for (int i = 0; i < Targets.Count; i++)
                if (!ReferenceEquals(Targets[i], targets[i])) return false;
            return true;
        }
    }

    private static Action<object?> GetFieldApply(Paper paper, string fieldId, FieldInfo field,
        IReadOnlyList<object> targets, PropertyGridConfig config, Action<object>? onChange)
    {
        var fields = s_applySurfaces.GetValue(paper, static p => new(p)).Fields;
        if (!fields.TryGetValue(fieldId, out FieldApplyState? state) || !state.Matches(targets))
        {
            state = new FieldApplyState();
            var captured = new object[targets.Count];
            for (int i = 0; i < captured.Length; i++) captured[i] = targets[i];
            state.Targets = captured;
            fields[fieldId] = state;
        }
        state.Field = field;
        state.Config = config;
        state.OnChange = onChange;
        state.Used = true;
        return state.Apply;
    }

    /// <summary>Cached attributes + display label for a field.</summary>
    private static FieldMeta GetFieldMeta(FieldInfo field)
        => s_fieldMeta.GetValue(field, static f =>
        {
            var attrs = f.GetCustomAttributes(true).OfType<Attribute>().ToArray();
            return new FieldMeta
            {
                Attributes = attrs,
                Label = ResolveFieldLabel(f.Name, attrs),
            };
        });

    /// <summary>
    /// Display label for a field: an [InspectorName("...")] override when present (matched by
    /// attribute name so the grid stays decoupled from the runtime assembly), else the nicified
    /// field name.
    /// </summary>
    public static string ResolveFieldLabel(string fieldName, IReadOnlyList<Attribute> attributes)
    {
        for (int i = 0; i < attributes.Count; i++)
        {
            if (attributes[i].GetType().Name != "InspectorNameAttribute") continue;
            var nameProp = attributes[i].GetType().GetProperty("DisplayName");
            if (nameProp?.GetValue(attributes[i]) is string s && s.Length > 0)
                return s;
            break;
        }
        return FormatFieldName(fieldName);
    }

    /// <summary>Cached parameterless [Button] methods (with resolved labels) for a type.</summary>
    private static ButtonMethod[] GetButtonMethods(Type type)
        => s_buttonCache.GetValue(type, static t =>
            t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
             .Where(m2 => m2.GetParameters().Length == 0
                       && m2.GetCustomAttributes().Any(a => a.GetType().Name == "ButtonAttribute"))
             .Select(m2 =>
             {
                 var btnAttr = m2.GetCustomAttributes().First(a => a.GetType().Name == "ButtonAttribute");
                 var labelProp = btnAttr.GetType().GetProperty("Label");
                 return new ButtonMethod
                 {
                     Method = m2,
                     Label = (labelProp?.GetValue(btnAttr) as string) ?? FormatFieldName(m2.Name),
                 };
             })
             .ToArray());

    /// <summary>Get serializable fields for a type (matches Echo's logic). Cached per type.</summary>
    public static FieldInfo[] GetSerializableFields(Type type) => GetSerializableFields(type, _includePrivate);

    /// <summary>
    /// Get serializable fields for a type. With <paramref name="includePrivate"/> (debug/raw mode),
    /// private instance fields are included as well; they render read-only unless explicitly exposed.
    /// </summary>
    public static FieldInfo[] GetSerializableFields(Type type, bool includePrivate)
        => includePrivate
            ? s_fieldCacheAll.GetValue(type, static t => ComputeSerializableFields(t, true))
            : s_fieldCache.GetValue(type, static t => ComputeSerializableFields(t, false));

    private static FieldInfo[] ComputeSerializableFields(Type type, bool includePrivate)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var fields = new List<FieldInfo>();
        var current = type;

        while (current != null && current != typeof(object))
        {
            foreach (var field in current.GetFields(flags))
            {
                if (field.IsStatic) continue;
                if (includePrivate)
                {
                    // Debug/raw mode: everything except explicitly ignored/hidden. Private fields
                    // that aren't serialized are still surfaced (read-only) for inspection.
                    bool ignored = field.GetCustomAttribute<SerializeIgnoreAttribute>() != null
                        || field.GetCustomAttribute<NonSerializedAttribute>() != null;
                    if (ignored) continue;
                    if (field.GetCustomAttributes().Any(a => a.GetType().Name == "HideInInspectorAttribute"))
                        continue;
                    fields.Add(field);
                    continue;
                }
                // Include public fields, [SerializeField] fields, and [ShowInInspector] fields.
                // ShowInInspector makes a private field visible without forcing it to be serialized;
                // pair it with [SerializeField] for both visibility and persistence. Matched by
                // attribute name so Origami stays free of a XEngine.Runtime compile-time reference,
                // matching how HideInInspector is handled below.
                bool shouldSerialize = field.IsPublic
                    || field.GetCustomAttribute<SerializeFieldAttribute>() != null
                    || field.GetCustomAttributes().Any(a => a.GetType().Name == "ShowInInspectorAttribute");
                if (!shouldSerialize) continue;
                bool shouldIgnore = field.GetCustomAttribute<SerializeIgnoreAttribute>() != null
                    || field.GetCustomAttribute<NonSerializedAttribute>() != null;
                if (shouldIgnore) continue;
                if (field.GetCustomAttributes().Any(a => a.GetType().Name == "HideInInspectorAttribute"))
                    continue;
                fields.Add(field);
            }
            current = current.BaseType;
        }
        return fields.ToArray();
    }

    /// <summary>True when a private field is deliberately exposed ([SerializeField] / [ShowInInspector]).</summary>
    private static bool IsExplicitlyExposed(FieldInfo field)
        => field.GetCustomAttribute<SerializeFieldAttribute>() != null
           || field.GetCustomAttributes().Any(a => a.GetType().Name == "ShowInInspectorAttribute");

    // ── [ShowInInspector] properties ───────────────────────────
    // Properties are never serialized; this surfaces computed/derived values in the inspector.
    // Matched by attribute name (ShowInInspectorAttribute) so Origami stays free of a
    // XEngine.Runtime compile-time reference, matching how HideInInspector/Button are handled.

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, PropertyInfo[]> s_shownPropCache = new();

    /// <summary>
    /// Instance properties on <paramref name="type"/> (and its base chain) marked with
    /// <c>[ShowInInspector]</c>, excluding indexers, params-indexed properties, and
    /// <c>override</c>-only properties that don't carry the attribute themselves. Cached per type.
    /// </summary>
    private static PropertyInfo[] GetShownProperties(Type type)
        => s_shownPropCache.GetValue(type, static t =>
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var props = new List<PropertyInfo>();
            var current = t;
            while (current != null && current != typeof(object))
            {
                foreach (var prop in current.GetProperties(flags))
                {
                    // Need a getter to display anything; skip indexers (GetIndexParameters().Length > 0)
                    // and skip properties already collected from a more derived type (same name).
                    if (prop.GetMethod == null) continue;
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (!prop.GetCustomAttributes(true).Any(a => a.GetType().Name == "ShowInInspectorAttribute")) continue;
                    if (props.Exists(p => p.Name == prop.Name)) continue;
                    props.Add(prop);
                }
                current = current.BaseType;
            }
            return props.ToArray();
        });

    /// <summary>Resolve a property by name on a target's type (mirrors ResolveField).</summary>
    private static PropertyInfo? ResolveProperty(Type type, string name)
    {
        foreach (var p in GetShownProperties(type))
            if (p.Name == name) return p;
        return null;
    }

    /// <summary>
    /// True if the property can be edited in the inspector: it has a public or non-public setter,
    /// is not marked <c>[ShowInInspector(ReadOnly = true)]</c>, and is not already inside a
    /// read-only scope.
    /// </summary>
    private static bool IsPropertyEditable(PropertyInfo prop)
    {
        if (prop.SetMethod == null) return false;
        var attr = prop.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "ShowInInspectorAttribute");
        if (attr != null)
        {
            var ro = attr.GetType().GetProperty("ReadOnly");
            if (ro?.GetValue(attr) is bool readOnly && readOnly) return false;
        }
        return true;
    }
}
