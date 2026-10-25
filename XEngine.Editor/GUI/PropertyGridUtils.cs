using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Editor.Core;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Editor.GUI;

public static class PropertyGridUtils
{
    /// <summary>
    /// Set of overridden field names for the current component being drawn.
    /// Set by the inspector before drawing a prefab instance's component.
    /// </summary>
    [ThreadStatic]
    public static HashSet<string>? OverriddenFields;

    private static readonly Action<object> s_noopChanged = _ => { };

    /// <summary>Draw a full property grid for an object.</summary>
    public static void Draw(Paper paper, string id, object target, Action<object>? onChanged = null, int depth = 0)
    {
        Origami.PropertyGrid(paper, id, target, EditorApplication.PropertyGridConfig)
            .OnChanged(onChanged ?? s_noopChanged)
            .Overrides(OverriddenFields)
            .Depth(depth)
            .Show();
    }

    /// <summary>Draw a property grid editing several objects at once (shared fields only; differing
    /// values are flagged as mixed and edits apply to every target).</summary>
    public static void DrawMulti(Paper paper, string id, IReadOnlyList<object> targets, Action<object>? onChanged = null, int depth = 0)
    {
        Origami.PropertyGrid(paper, id, targets, EditorApplication.PropertyGridConfig)
            .OnChanged(onChanged ?? s_noopChanged)
            .Overrides(OverriddenFields)
            .Depth(depth)
            .Show();
    }

    /// <summary>Draw a single field with label and control.</summary>
    public static void DrawField(Paper paper, string id, string label, Type type, object? value,
        Action<object?> onChange, int depth = 0)
    {
        PropertyGridRenderer.DrawField(paper, id, label, type, value,
            EditorApplication.PropertyGridConfig, onChange, depth);
    }

    /// <summary>Convert "myFieldName" to "My Field Name".</summary>
    public static string NicifyName(string name) => PropertyGridRenderer.FormatFieldName(name);

    /// <summary>Get serializable fields for a type.</summary>
    public static FieldInfo[] GetSerializableFields(Type type) => PropertyGridRenderer.GetSerializableFields(type);
}
