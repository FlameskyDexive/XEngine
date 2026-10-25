using System;

namespace XEngine.NodePlay;

/// <summary>
/// The everywhere-parameter for node/task fields (NodeCanvas's BBParameter): either an inline
/// literal, or a blackboard variable reference by name resolved against the executing scope.
/// Writes to unknown "_"-prefixed names auto-promote into the graph blackboard ("dynamic vars").
/// </summary>
public class NPParameter
{
    /// <summary>Variable to read/write; empty means "use <see cref="InlineValue"/>".
    /// Prefix "_" marks a dynamic name that self-registers on first write.</summary>
    public string VariableName = "";

    /// <summary>Fallback used when not bound or the lookup misses.</summary>
    public object InlineValue = "";

    public bool IsBound => !string.IsNullOrEmpty(VariableName);

    // The [NONE] marker of the reference design maps to the empty VariableName.

    public object GetValue(INPBlackboard? bb)
    {
        if (!IsBound)
            return InlineValue;

        NPBlackboardSource? source = bb as NPBlackboardSource;
        if (source == null)
            return InlineValue;

        NPVariable? variable = source.Get(VariableName);
        if (variable == null)
        {
            if (VariableName.StartsWith("_"))
                variable = source.Ensure(VariableName, "");
            else
                return InlineValue;
        }

        return variable.Value;
    }

    public T GetValue<T>(INPBlackboard? bb, T fallback)
    {
        if (!IsBound)
            return CoerceOr(InlineValue, fallback);

        if (bb is not NPBlackboardSource source)
            return CoerceOr(InlineValue, fallback);

        NPVariable? variable = source.Get(VariableName);
        if (variable == null && VariableName.StartsWith("_"))
            variable = source.Ensure(VariableName, "");
        return variable != null ? variable.GetValue(fallback) : CoerceOr(InlineValue, fallback);
    }

    public void SetValue(INPBlackboard? bb, object value)
    {
        if (!IsBound || bb is not NPBlackboardSource source)
            return;
        source.SetValue(VariableName, value);
    }

    private static T CoerceOr<T>(object raw, T fallback)
    {
        if (raw is T direct)
            return direct;
        object? converted = NPTypeConverter.Convert(raw, typeof(T));
        return converted is T ok ? ok : fallback;
    }
}
