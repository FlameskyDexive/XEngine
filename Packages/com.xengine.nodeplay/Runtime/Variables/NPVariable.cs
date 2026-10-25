using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Runtime;

namespace XEngine.NodePlay;

/// <summary>
/// One blackboard entry. The value is stored as <see cref="Value"/> typed <see cref="object"/>,
/// so any serializable type round-trips through Echo text under its own `$type` envelope;
/// typed access goes through <see cref="GetValue{T}(T)"/> / <see cref="TrySetObject"/>, which fall
/// back to registered type converters on mismatch. Exposed (<see cref="IsPublic"/>) variables form
/// the override surface for subgraph parameter mapping.
/// </summary>
public class NPVariable
{
    public string Name = "";
    public string ValueId = NPNode.NewId();
    public bool IsPublic;

    /// <summary>The stored value (never null; empty string/object default by creator convention).</summary>
    public object Value = "";

    /// <summary>Optional scene data binding path ("<c>Type.Member</c>" or "<c>Member</c>"),
    /// resolved against the runner's agent when the graph starts.</summary>
    public string PropertyPath = "";

    /// <summary>Raised after every successful write (read-out mappings and the network-sync hook
    /// reserved for post-v1 subscribe here).</summary>
    public event Action<NPVariable>? OnValueChanged;

    /// <summary>Effective runtime type of the stored value.</summary>
    public Type ValueType => Value?.GetType() ?? typeof(object);

    public void SetRawValue(object newValue)
    {
        if (ReferenceEquals(Value, newValue))
            return;
        Value = newValue ?? throw new ArgumentNullException(nameof(newValue), "remove the variable instead of storing null");
        OnValueChanged?.Invoke(this);
    }

    /// <summary>Typed read with converter fallback; returns <paramref name="fallback"/> when unusable.</summary>
    public T GetValue<T>(T fallback)
    {
        if (Value is T direct)
            return direct;

        object? converted = NPTypeConverter.Convert(Value, typeof(T));
        return converted is T ok ? ok : fallback;
    }

    /// <summary>Object write honoring the stored value's type via conversion.</summary>
    public bool TrySetObject(object? incoming)
    {
        if (incoming == null)
            return false;
        Type current = ValueType;
        if (!current.IsInstanceOfType(incoming))
        {
            incoming = NPTypeConverter.Convert(incoming, current);
            if (incoming == null)
                return false;
        }

        SetRawValue(incoming);
        return true;
    }

    // ---- scene data binding -------------------------------------------------------------

    private IMemberChannel? _channel;

    /// <summary>
    /// Resolve <see cref="PropertyPath"/> against the agent's components with cached member handles.
    /// Subsequent writes to this variable are pushed into the scene member; the initial scene value
    /// seeds the variable unless <paramref name="pushCurrentIntoScene"/> is set.
    /// Returns an error string on failure, null on success/skip.
    /// </summary>
    public string? InitializeBinding(XEngine.Runtime.MonoBehaviour? agent, bool pushCurrentIntoScene)
    {
        _channel = null;
        UnsubscribeBinding();
        if (agent == null || string.IsNullOrWhiteSpace(PropertyPath))
            return null;

        IMemberChannel? channel = MemberChannel.Resolve(agent, PropertyPath.Trim());
        if (channel == null)
            return $"variable '{Name}': no bindable member '{PropertyPath}' on '{agent.Name}'";
        if (!channel.CanWrite && pushCurrentIntoScene)
            return $"variable '{Name}': member '{PropertyPath}' is read-only";

        // Seed direction: by default the scene value initializes the variable; authors can push
        // the variable's current value into the scene instead.
        if (!pushCurrentIntoScene)
            Value = NPTypeConverter.Convert(channel.Read(), ValueType) ?? Value;
        else
            channel.Write(Value);

        _channel = channel;
        if (channel.CanWrite)
            OnValueChanged += PushToScene;

        return null;
    }

    /// <summary>Tear down a previously installed binding (graph stop / re-init).</summary>
    public void DeinitBinding()
    {
        _channel = null;
        UnsubscribeBinding();
    }

    private void UnsubscribeBinding() => OnValueChanged -= PushToScene;

    private void PushToScene(NPVariable variable) => _channel?.Write(variable.Value);

    private interface IMemberChannel
    {
        bool CanWrite { get; }
        object? Read();
        void Write(object? value);
    }

    /// <summary>Cached field-or-property accessor over the agent's first matching component.</summary>
    private sealed class MemberChannel : IMemberChannel
    {
        private readonly XEngine.Runtime.MonoBehaviour _target;
        private readonly FieldInfo? _field;
        private readonly PropertyInfo? _property;

        public static IMemberChannel? Resolve(XEngine.Runtime.MonoBehaviour agent, string path)
        {
            int dot = path.LastIndexOf('.');
            string typeName = dot > 0 ? path[..dot] : "";
            string memberName = dot > 0 ? path[(dot + 1)..] : path;

            XEngine.Runtime.MonoBehaviour target = agent;
            if (typeName.Length > 0)
            {
                Type? componentType = RuntimeUtils.FindType(typeName);
                if (componentType == null || !typeof(XEngine.Runtime.MonoBehaviour).IsAssignableFrom(componentType))
                    return null;
                foreach (XEngine.Runtime.MonoBehaviour component in agent.GetComponents(componentType))
                {
                    target = component;
                    break;
                }
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (Type? t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo? field = t.GetField(memberName, flags);
                if (field != null)
                    return new MemberChannel(target, field, null);

                PropertyInfo? property = t.GetProperty(memberName, flags);
                if (property?.GetGetMethod() != null)
                    return new MemberChannel(target, null, property);
            }

            return null;
        }

        private MemberChannel(XEngine.Runtime.MonoBehaviour target, FieldInfo? field, PropertyInfo? property)
        {
            _target = target;
            _field = field;
            _property = property;
        }

        public bool CanWrite => _field != null || (_property?.CanWrite ?? false);

        public object? Read() => _field != null ? _field.GetValue(_target) : _property!.GetValue(_target);

        public void Write(object? value)
        {
            if (_field != null)
                _field.SetValue(_target, value);
            else
                _property!.SetValue(_target, value);
        }
    }
}
