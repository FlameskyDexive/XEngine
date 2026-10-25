using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.NodePlay;

/// <summary>
/// Base of all reusable behaviour units (actions and conditions) placed inside nodes.
/// A task binds to the executing agent once per run via <see cref="Bind"/> — subclasses override
/// <see cref="OnInit"/> to resolve agent-typed members and return an error string when something
/// required is missing, which surfaces in the graph console instead of crashing play mode.
/// </summary>
/// <remarks>
/// Tasks are persisted polymorphically as plain public fields, mirroring the node persistence
/// contract. Per-run state is <see cref="SerializeIgnoreAttribute"/>-excluded.
/// </remarks>
public abstract class NPTask
{
    public string Label = "";

    public MonoBehaviour? Agent { get; private set; }
    public INPBlackboard? Blackboard { get; private set; }
    public NPGraphAsset? OwnerSystem { get; private set; }

    private string? _initError;
    private bool _bound;

    /// <summary>Explicit label, else the NPName display name, else the CLR name.</summary>
    public virtual string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Label))
                return Label;
            NPNameAttribute? attr = GetType().GetCustomAttribute<NPNameAttribute>();
            return attr?.Name ?? SplitCamelCase(GetType().Name);
        }
    }

    public string Description => GetType().GetCustomAttribute<NPDescriptionAttribute>()?.Text ?? "";

    public string Category => GetType().GetCustomAttribute<NPCategoryAttribute>()?.Category ?? "";

    /// <summary>Error from the last bind/init pass; null when ready to run.</summary>
    public string? InitError => _initError;

    /// <summary>
    /// Bind this task to a running context. Cheap when already bound to the same agent;
    /// returns an error string for broken configuration (required fields missing etc.).
    /// </summary>
    public string? Bind(MonoBehaviour? agent, INPBlackboard blackboard, NPGraphAsset ownerSystem)
    {
        if (_bound && ReferenceEquals(Agent, agent))
            return _initError;

        Agent = agent;
        Blackboard = blackboard;
        OwnerSystem = ownerSystem;

        InjectFromAgent();
        _initError = OnInit() ?? CheckRequiredFields();
        if (_initError == null)
            OnPostInit();
        _bound = true;
        return _initError;
    }

    /// <summary>Clears per-run bindings so the task can be re-bound to another agent.</summary>
    public void Unbind()
    {
        _bound = false;
        _initError = null;
        Agent = null;
        Blackboard = null;
    }

    protected virtual string? OnInit() => null;

    /// <summary>Raised after a successful init; hook for precomputing delegates.</summary>
    protected virtual void OnPostInit()
    {
    }

    // ---- attribute plumbing -------------------------------------------------

    private static readonly Dictionary<Type, FieldInfo[]> _requiredFieldsCache = new();

    private string? CheckRequiredFields()
    {
        if (!_requiredFieldsCache.TryGetValue(GetType(), out FieldInfo[]? fields))
        {
            List<FieldInfo> found = new();
            for (Type? t = GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.IsDefined(typeof(NPRequiredFieldAttribute), false))
                        found.Add(f);
                }
            }

            fields = found.ToArray();
            _requiredFieldsCache[GetType()] = fields;
        }

        for (int i = 0; i < fields.Length; i++)
        {
            object? value = fields[i].GetValue(this);
            if (value == null || value as string == "")
                return $"'{DisplayName}' requires field '{fields[i].Name}' to be assigned.";
        }

        return null;
    }

    private void InjectFromAgent()
    {
        if (Agent == null)
            return;

        for (Type? t = GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!f.IsDefined(typeof(NPGetFromAgentAttribute), false))
                    continue;

                Type targetType = f.FieldType;
                foreach (MonoBehaviour component in Agent.GetComponents(targetType))
                {
                    f.SetValue(this, component);
                    break;
                }
            }
        }
    }

    public static string SplitCamelCase(string input)
    {
        // "PrioritySelector" -> "Priority Selector" — menu-facing friendliness without LINQ.
        if (string.IsNullOrEmpty(input))
            return input;
        Span<char> buffer = stackalloc char[input.Length * 2];
        int w = 0;
        buffer[w++] = input[0];
        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]) && !char.IsUpper(input[i - 1]) && w + 1 < buffer.Length)
                buffer[w++] = ' ';
            buffer[w++] = input[i];
        }

        return new string(buffer.Slice(0, w));
    }
}
