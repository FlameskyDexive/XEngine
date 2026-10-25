using System;

namespace XEngine.NodePlay;

/// <summary>
/// Display/menu name for a node or task type, e.g. <c>[NPName("Selector", 90)]</c>.
/// Negative priority sorts last; types without the attribute fall back to the CLR name.
/// Consumed by the add-node browser and inspector headers.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NPNameAttribute : Attribute
{
    public string Name { get; }
    public int Priority { get; } = 0;

    public NPNameAttribute(string name, int priority = 0)
    {
        Name = name;
        Priority = priority;
    }
}

/// <summary>
/// Browser folder path for a node/task type, e.g. <c>[NPCategory("Composites")]</c>
/// (slashes create nested folders).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NPCategoryAttribute : Attribute
{
    public string Category { get; }

    public NPCategoryAttribute(string category) => Category = category;
}

/// <summary>Author-facing documentation shown as tooltip / inline help in the node browser.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Field | AttributeTargets.Property)]
public sealed class NPDescriptionAttribute : Attribute
{
    public string Text { get; }

    public NPDescriptionAttribute(string text) => Text = text;
}

/// <summary>
/// Marks a task field that must be assigned before the task can run; unassigned fields abort
/// initialization with an actionable error instead of failing silently at play time.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NPRequiredFieldAttribute : Attribute
{
}

/// <summary>
/// Marks a task field whose value is fetched from the agent's component of the annotated type
/// when the task initializes (dependency injection from the behaviour owner).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NPGetFromAgentAttribute : Attribute
{
}

/// <summary>
/// Editor hint: draw the annotated float/int field as a slider between min and max.
/// Honored by the inspector property-grid handler (M19).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NPSliderAttribute : Attribute
{
    public float Min { get; }
    public float Max { get; }

    public NPSliderAttribute(float min, float max)
    {
        Min = min;
        Max = max;
    }
}

/// <summary>
/// Editor hint: only draw the annotated field when the named bool field/property on the same
/// object is true. Honored by the inspector property-grid handler (M19).
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NPShowIfAttribute : Attribute
{
    public string ConditionMember { get; }

    public NPShowIfAttribute(string conditionMember) => ConditionMember = conditionMember;
}
