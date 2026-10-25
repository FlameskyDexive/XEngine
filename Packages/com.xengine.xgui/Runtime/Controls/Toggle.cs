using System;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>Binary selectable control with optional ToggleGroup exclusivity.</summary>
[AddComponentMenu("UI/Toggle")]
public sealed class Toggle : Selectable, IPointerClickHandler, ISubmitHandler
{
    [SerializeField] private bool _isOn;
    [SerializeField] private ToggleGroup? _group;
    [SerializeField] private XEngineAction _onValueChanged = new();

    public bool IsOn { get => _isOn; set => SetIsOn(value, true); }
    public ToggleGroup? Group { get => _group; set { _group?.Unregister(this); _group = value; _group?.Register(this); } }
    public XEngineAction ValueChangedAction => _onValueChanged;
    public event Action<bool>? OnValueChanged;

    public override void OnEnable()
    {
        base.OnEnable();
        _group?.Register(this);
    }

    public override void OnDisable()
    {
        _group?.Unregister(this);
        base.OnDisable();
    }

    public void OnPointerClick(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !IsInteractable()) return;

        // A selectable consumed the pointer even when its group's selection policy keeps it on.
        // Otherwise a Button or another click handler on an ancestor would receive the same click.
        e.Use();
        if (CanToggle()) SetIsOn(!_isOn, true);
    }

    public void OnSubmit()
    {
        if (IsInteractable() && CanToggle()) SetIsOn(!_isOn, true);
    }

    internal void SetIsOn(bool value, bool notify)
    {
        if (value && _group != null) _group.NotifyToggleOn(this);
        if (_isOn == value) return;
        _isOn = value;
        if (!notify) return;
        try { OnValueChanged?.Invoke(value); } catch (Exception ex) { Debug.LogError($"[Toggle] callback on '{Name}' threw: {ex.Message}"); }
        _onValueChanged.Invoke();
    }

    private bool CanToggle() => !_isOn || _group == null || _group.CanTurnOff(this);
}

[AddComponentMenu("UI/Toggle Group")]
public sealed class ToggleGroup : UIBehaviour
{
    [SerializeField] private bool _allowSwitchOff;
    [SerializeIgnore] private readonly System.Collections.Generic.List<Toggle> _toggles = new();
    public bool AllowSwitchOff { get => _allowSwitchOff; set => _allowSwitchOff = value; }
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }
    internal void Register(Toggle toggle) { if (!_toggles.Contains(toggle)) _toggles.Add(toggle); if (toggle.IsOn) NotifyToggleOn(toggle); }
    internal void Unregister(Toggle toggle) => _toggles.Remove(toggle);
    internal void NotifyToggleOn(Toggle source)
    {
        for (int i = 0; i < _toggles.Count; i++)
            if (!ReferenceEquals(_toggles[i], source) && _toggles[i].IsOn)
                _toggles[i].SetIsOn(false, true);
    }
    internal bool CanTurnOff(Toggle toggle) => _allowSwitchOff || !_toggles.Contains(toggle) || CountOn() > 1;
    private int CountOn() { int count = 0; for (int i = 0; i < _toggles.Count; i++) if (_toggles[i].IsOn) count++; return count; }
}
