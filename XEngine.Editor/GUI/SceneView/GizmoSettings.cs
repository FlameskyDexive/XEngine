using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Editor.Projects.Scripting;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.GUI.SceneView;

/// <summary>
/// Global gizmo state behind the Gizmos window that the Scene and Game views share, matching Unity:
/// the "3D Icons" / "Fade Gizmos" / "Gizmo Scale" sliders, the selection outline + wire toggles, and
/// a per-component-type icon/gizmo pair.
///
/// Component types are discovered lazily: the first time a component is asked whether it may draw, it
/// is added to <see cref="Types"/> and can be toggled from then on. Keyed by type name so a hidden
/// type survives both layout persistence and a script hot-reload (which produces a new
/// <see cref="Type"/> for the same name).
/// </summary>
public static class GizmoSettings
{
    /// <summary>One row of the Gizmos window's component table.</summary>
    public sealed class TypeEntry
    {
        public required string Name { get; init; }

        /// <summary>Whether this type's <c>DrawGizmos</c> runs at all.</summary>
        public bool GizmoVisible = true;

        /// <summary>Whether this type's billboarded icon is recorded (Unity's separate icon column).</summary>
        public bool IconVisible = true;

        /// <summary>Set once the type has actually drawn an icon, so the icon column can dim rows without one.</summary>
        public bool HasIcon;

        /// <summary>True for types compiled from the project's own scripts — Unity's "Scripts" section.</summary>
        public bool IsScript;
    }

    // ── Header block (Unity's Gizmos window top section) ──────────────────────────────

    /// <summary>Draw billboarded component icons (lights, cameras, audio sources...).</summary>
    public static bool Show3DIcons
    {
        get => Debug.GizmoIconsVisible;
        set => Debug.GizmoIconsVisible = value;
    }

    /// <summary>Size multiplier for billboarded component icons.</summary>
    public static float IconScale
    {
        get => Debug.GizmoIconScale;
        set => Debug.GizmoIconScale = Math.Clamp(value, IconScaleMin, IconScaleMax);
    }

    public const float IconScaleMin = 0.1f;
    public const float IconScaleMax = 5f;

    /// <summary>
    /// Uniform scale on every component's gizmo geometry, taken about that component's own position.
    /// Not a Unity control — added because authored gizmo sizes are fixed world sizes and a scene
    /// whose content is a few units across drowns under them.
    /// </summary>
    public static float GizmoScale
    {
        get => Debug.GizmoScale;
        set => Debug.GizmoScale = Math.Clamp(value, GizmoScaleMin, GizmoScaleMax);
    }

    public const float GizmoScaleMin = 0.05f;
    public const float GizmoScaleMax = 2f;

    /// <summary>Fade out gizmos that cover fewer than <see cref="FadeSize"/> pixels on screen.</summary>
    public static bool FadeGizmos { get; set; } = true;

    /// <summary>Screen size in pixels below which a gizmo is fully faded out.</summary>
    public static float FadeSize
    {
        get => _fadeSize;
        set => _fadeSize = Math.Clamp(value, FadeSizeMin, FadeSizeMax);
    }

    private static float _fadeSize = 12f;
    public const float FadeSizeMin = 0f;
    public const float FadeSizeMax = 100f;

    /// <summary>Screen-space silhouette around the selection (Unity's orange ring).</summary>
    public static bool SelectionOutline { get; set; } = true;

    /// <summary>Wireframe bounds box around the selection.</summary>
    public static bool SelectionWire { get; set; } = true;

    // ── Per-type table ────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, TypeEntry> _entries = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _hiddenGizmosFromDisk = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _hiddenIconsFromDisk = new(StringComparer.Ordinal);
    private static readonly List<TypeEntry> _sorted = new();
    private static bool _sortDirty = true;
    private static bool _installed;

    /// <summary>Component types seen this session, alphabetically. Built-in types first, then scripts.</summary>
    public static IReadOnlyList<TypeEntry> Types
    {
        get
        {
            if (_sortDirty)
            {
                _sorted.Clear();
                foreach (TypeEntry entry in _entries.Values)
                    _sorted.Add(entry);
                _sorted.Sort(static (a, b) =>
                {
                    if (a.IsScript != b.IsScript) return a.IsScript ? 1 : -1;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                _sortDirty = false;
            }
            return _sorted;
        }
    }

    /// <summary>Hook the runtime gizmo filters. Idempotent; called by the Scene view on first use.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Debug.GizmoFilter = Filter;
        Debug.GizmoIconFilter = IconFilter;
        Debug.GizmoFadeEvaluator = EvaluateFade;
    }

    private static bool Filter(MonoBehaviour component)
    {
        if (!SceneVisibilityManager.IsVisibleInHierarchy(component.GameObject))
            return false;
        return Register(component).GizmoVisible;
    }

    private static bool IconFilter(MonoBehaviour component)
    {
        TypeEntry entry = Register(component);
        if (!entry.HasIcon) entry.HasIcon = true;
        return entry.IconVisible;
    }

    // A miss means "type not seen yet", so this single probe per component per frame doubles as the
    // discovery pass for the Gizmos window's type list.
    private static TypeEntry Register(MonoBehaviour component)
    {
        Type type = component.GetType();
        string name = type.Name;
        if (_entries.TryGetValue(name, out TypeEntry? entry))
            return entry;

        entry = new TypeEntry
        {
            Name = name,
            GizmoVisible = !_hiddenGizmosFromDisk.Contains(name),
            IconVisible = !_hiddenIconsFromDisk.Contains(name),
            IsScript = IsScriptAssembly(type.Assembly),
        };
        _entries[name] = entry;
        _sortDirty = true;
        return entry;
    }

    private static bool IsScriptAssembly(Assembly assembly)
    {
        IReadOnlyList<Assembly> scripts = ScriptAssemblyManager.GetScriptAssemblies();
        for (int i = 0; i < scripts.Count; i++)
            if (ReferenceEquals(scripts[i], assembly)) return true;
        return false;
    }

    // ── Fade ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set by the Scene view before it renders so the fade can be measured in that view's pixels.
    /// Gizmos are recorded once per editor frame and consumed by whichever camera draws them, so the
    /// Scene view — the view gizmos exist for — owns the metric.
    /// </summary>
    /// <param name="pixelsPerUnit">
    /// Perspective: <c>viewportHeight / (2*tan(fovY/2))</c>, divided by distance at evaluation time.
    /// Orthographic: <c>viewportHeight / (2*orthographicSize)</c>, used as-is.
    /// </param>
    public static void SetFadeReference(Float3 cameraPosition, bool orthographic, float pixelsPerUnit)
    {
        _fadeCameraPosition = cameraPosition;
        _fadeOrthographic = orthographic;
        _fadePixelsPerUnit = pixelsPerUnit;
    }

    private static Float3 _fadeCameraPosition;
    private static bool _fadeOrthographic;
    private static float _fadePixelsPerUnit;

    /// <summary>
    /// Unity's "Fade Gizmos": a gizmo covering fewer than <see cref="FadeSize"/> pixels fades out,
    /// ramped over the octave below the threshold so it does not pop.
    /// </summary>
    private static float EvaluateFade(Float3 center, float radius)
    {
        if (!FadeGizmos || FadeSize <= 0f || _fadePixelsPerUnit <= 0f) return 1f;

        float pixels;
        if (_fadeOrthographic)
            pixels = radius * 2f * _fadePixelsPerUnit;
        else
        {
            float distance = Float3.Length(center - _fadeCameraPosition);
            if (distance < 0.0001f) return 1f;
            pixels = radius * 2f * _fadePixelsPerUnit / distance;
        }

        if (pixels >= FadeSize) return 1f;
        float half = FadeSize * 0.5f;
        if (pixels <= half) return 0f;
        return (pixels - half) / half;
    }

    // ── Queries / mutations used by the Gizmos window ─────────────────────────────────

    // Before a type has drawn once there is no entry, so the answer has to come from the restored
    // hidden list — otherwise a type hidden in a saved layout reads back as visible until the first
    // frame that draws it.
    public static bool IsGizmoVisible(string typeName)
        => _entries.TryGetValue(typeName, out TypeEntry? e)
            ? e.GizmoVisible
            : !_hiddenGizmosFromDisk.Contains(typeName);

    public static void SetGizmoVisible(string typeName, bool visible)
    {
        if (_entries.TryGetValue(typeName, out TypeEntry? e)) e.GizmoVisible = visible;
        if (visible) _hiddenGizmosFromDisk.Remove(typeName);
        else _hiddenGizmosFromDisk.Add(typeName);
    }

    public static bool IsIconVisible(string typeName)
        => _entries.TryGetValue(typeName, out TypeEntry? e)
            ? e.IconVisible
            : !_hiddenIconsFromDisk.Contains(typeName);

    public static void SetIconVisible(string typeName, bool visible)
    {
        if (_entries.TryGetValue(typeName, out TypeEntry? e)) e.IconVisible = visible;
        if (visible) _hiddenIconsFromDisk.Remove(typeName);
        else _hiddenIconsFromDisk.Add(typeName);
    }

    public static void SetAllVisible(bool visible)
    {
        foreach (TypeEntry entry in Types)
        {
            SetGizmoVisible(entry.Name, visible);
            SetIconVisible(entry.Name, visible);
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────────────

    /// <summary>Type names whose gizmo is hidden, for layout persistence.</summary>
    public static IEnumerable<string> HiddenTypeNames() => _hiddenGizmosFromDisk;

    /// <summary>Type names whose icon is hidden, for layout persistence.</summary>
    public static IEnumerable<string> HiddenIconTypeNames() => _hiddenIconsFromDisk;

    /// <summary>Re-apply hidden gizmo type names restored from a saved layout.</summary>
    public static void RestoreHiddenTypeNames(IEnumerable<string> names)
    {
        foreach (string name in names)
            SetGizmoVisible(name, false);
    }

    /// <summary>Re-apply hidden icon type names restored from a saved layout.</summary>
    public static void RestoreHiddenIconTypeNames(IEnumerable<string> names)
    {
        foreach (string name in names)
            SetIconVisible(name, false);
    }
}
