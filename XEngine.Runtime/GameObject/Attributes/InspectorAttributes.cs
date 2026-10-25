using System;

namespace XEngine.Runtime;

/// <summary>Hides a field from the inspector but keeps it serialized.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class HideInInspectorAttribute : Attribute { }

/// <summary>Draws a float/int field as a slider with min/max range.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class RangeAttribute : Attribute
{
    public float Min { get; }
    public float Max { get; }
    public RangeAttribute(float min, float max) { Min = min; Max = max; }
}

/// <summary>Draws a header label above the field in the inspector.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
public class HeaderAttribute : Attribute
{
    public string Text { get; }
    public HeaderAttribute(string text) => Text = text;
}

/// <summary>Adds vertical spacing before the field in the inspector.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class SpaceAttribute : Attribute
{
    public float Height { get; }
    public SpaceAttribute(float height = 8f) => Height = height;
}

/// <summary>Shows a tooltip when hovering the field label.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class TooltipAttribute : Attribute
{
    public string Text { get; }
    public TooltipAttribute(string text) => Text = text;
}

/// <summary>Makes the field read-only in the inspector.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class ReadOnlyAttribute : Attribute { }

/// <summary>Draws a button in the inspector that calls this method when clicked.</summary>
[AttributeUsage(AttributeTargets.Method)]
public class ButtonAttribute : Attribute
{
    public string? Label { get; }
    public ButtonAttribute(string? label = null) => Label = label;
}

/// <summary>Only shows the field when the named bool field/property is true.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class ShowIfAttribute : Attribute
{
    public string ConditionMember { get; }
    public ShowIfAttribute(string conditionMember) => ConditionMember = conditionMember;
}

/// <summary>Draws a string field as a multi-line text area.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class TextAreaAttribute : Attribute
{
    public int MinLines { get; }
    public int MaxLines { get; }
    public TextAreaAttribute(int minLines = 3, int maxLines = 5) { MinLines = minLines; MaxLines = maxLines; }
}

/// <summary>Clamps a numeric field to a minimum value in the inspector.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class MinAttribute : Attribute
{
    public float Min { get; }
    public MinAttribute(float min) => Min = min;
}

/// <summary>Draws a string field as a fixed-height multi-line text area.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class MultilineAttribute : Attribute
{
    public int Lines { get; }
    public MultilineAttribute(int lines = 3) => Lines = lines;
}

/// <summary>
/// Defers writing the field until the edit is committed with Enter (Escape reverts),
/// instead of on every keystroke.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public class DelayedAttribute : Attribute { }

/// <summary>Overrides the display name of the field in the inspector.</summary>
[AttributeUsage(AttributeTargets.Field)]
public class InspectorNameAttribute : Attribute
{
    public string DisplayName { get; }
    public InspectorNameAttribute(string displayName) => DisplayName = displayName;
}

/// <summary>
/// Greys the field out (visible but not editable) unless the named bool field/property is true.
/// Use for values only meaningful behind an enabling toggle. Ported from upstream (01658335).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public class EnableIfAttribute : Attribute
{
    public string ConditionMember { get; }
    public EnableIfAttribute(string conditionMember) => ConditionMember = conditionMember;
}

/// <summary>
/// Organizes a MonoBehaviour in the Add Component menu.
/// Path uses '/' for categories, e.g. "Physics/Rigidbody".
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class AddComponentMenuAttribute : Attribute
{
    public string Path { get; }
    public string Icon { get; }
    public AddComponentMenuAttribute(string path, string icon = "") { Path = path; Icon = icon; }
}
