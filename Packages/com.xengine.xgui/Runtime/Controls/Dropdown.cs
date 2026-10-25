using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>
/// Package-owned dropdown selector. A disabled <see cref="ItemTemplate"/> supplies the visual and
/// interactive structure for pooled entries; it must contain a package <see cref="Button"/>,
/// <see cref="Image"/>, and <see cref="TextComponent"/>. The list can live under a separate popup
/// root so it is not clipped by the control's immediate hierarchy.
/// </summary>
[AddComponentMenu("UI/Dropdown")]
[ComponentIcon("\uf078")]
[RequireComponent(typeof(RectTransform))]
public sealed class Dropdown : Selectable, IPointerClickHandler, ISubmitHandler, ICancelHandler
{
    // Keep these names and defaults in lockstep with XEngine.Runtime.UI.UIDropdown.
    [SerializeField] private List<string> _options = new();
    public List<string> Options => _options;

    [SerializeField] private int _value;
    public int Value { get => _value; set => SetValue(value, notify: true); }

    [SerializeField] private TextComponent? _captionText;
    public TextComponent? CaptionText { get => _captionText; set { _captionText = value; UpdateCaption(); } }

    [SerializeField] private RectTransform? _optionsRoot;
    public RectTransform? OptionsRoot { get => _optionsRoot; set { _optionsRoot = value; MarkTemplateChanged(); } }

    [SerializeField] private float _itemHeight = 24f;
    public float ItemHeight { get => _itemHeight; set { _itemHeight = MathF.Max(1f, value); RebuildOpenItems(); } }

    [SerializeField] private int _itemTextSize = 16;
    public int ItemTextSize { get => _itemTextSize; set { _itemTextSize = Math.Max(1, value); RebuildOpenItems(); } }

    [SerializeField] private Color _itemColor = new(0.16f, 0.16f, 0.20f, 1f);
    public Color ItemColor { get => _itemColor; set { _itemColor = value; RebuildOpenItems(); } }

    [SerializeField] private Color _itemSelectedColor = new(0.24f, 0.40f, 0.75f, 1f);
    public Color ItemSelectedColor { get => _itemSelectedColor; set { _itemSelectedColor = value; RebuildOpenItems(); } }

    [SerializeField] private Color _itemTextColor = new(0.88f, 0.88f, 0.90f, 1f);
    public Color ItemTextColor { get => _itemTextColor; set { _itemTextColor = value; RebuildOpenItems(); } }

    [SerializeField] private XEngineAction _onValueChanged = new();
    public XEngineAction ValueChangedAction => _onValueChanged;
    public event Action<int>? OnValueChanged;

    /// <summary>Disabled scene template used to instantiate each popup row.</summary>
    [SerializeField] private RectTransform? _itemTemplate;
    public RectTransform? ItemTemplate { get => _itemTemplate; set { _itemTemplate = value; MarkTemplateChanged(); } }

    /// <summary>
    /// Optional root of the independent popup layer. When omitted, <see cref="OptionsRoot"/> is
    /// enabled and disabled directly. Put this under the canvas to escape a clipped control subtree.
    /// </summary>
    [SerializeField] private RectTransform? _overlayRoot;
    public RectTransform? OverlayRoot { get => _overlayRoot; set { _overlayRoot = value; MarkTemplateChanged(); } }

    [SerializeField] private bool _flipOnOverflow = true;
    public bool FlipOnOverflow { get => _flipOnOverflow; set => _flipOnOverflow = value; }

    [SerializeIgnore] private bool _open;
    [SerializeIgnore] private GameObject? _focusBeforeOpen;
    [SerializeIgnore] private readonly List<Item> _items = new();
    [SerializeIgnore] private string? _lastTemplateError;

    /// <summary>True while the popup is visible.</summary>
    public bool IsOpen => _open;

    /// <summary>Last template validation failure, or <see langword="null"/> after a valid open.</summary>
    public string? LastTemplateError => _lastTemplateError;

    public override void OnEnable()
    {
        base.OnEnable();
        ClampValue();
        UpdateCaption();
        HidePopup();
    }

    public override void OnDisable()
    {
        Close(restoreFocus: false);
        base.OnDisable();
    }

    public override void OnValidate()
    {
        base.OnValidate();
        _itemHeight = MathF.Max(1f, _itemHeight);
        _itemTextSize = Math.Max(1, _itemTextSize);
        ClampValue();
        UpdateCaption();
    }

    public void ClearOptions()
    {
        _options.Clear();
        ClampValue();
        UpdateCaption();
        RebuildOpenItems();
    }

    public void AddOption(string option)
    {
        _options.Add(option ?? string.Empty);
        ClampValue();
        UpdateCaption();
        RebuildOpenItems();
    }

    public void SetOptions(IEnumerable<string>? options)
    {
        _options.Clear();
        if (options != null)
        {
            foreach (string option in options)
                _options.Add(option ?? string.Empty);
        }
        ClampValue();
        UpdateCaption();
        RebuildOpenItems();
    }

    /// <summary>Changes the selection without invoking either serialized or C# callbacks.</summary>
    public void SetValueWithoutNotify(int value) => SetValue(value, notify: false);

    public override void OnPointerDown(PointerEventData e)
    {
        base.OnPointerDown(e);
        if (e.Button == MouseButton.Left && IsInteractable()) e.Use();
    }

    public void OnPointerClick(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        Toggle();
        e.Use();
    }

    public void OnSubmit()
    {
        if (IsInteractable()) Toggle();
    }

    public void OnCancel() => Close();

    public override void Update()
    {
        base.Update();
        if (!Application.IsPlaying || !_open) return;

        // The popup may be parented under a separate overlay, so neither subtree may count as an
        // outside press. This intentionally runs only while the popup is visible.
        if (Input.GetMouseButtonDown(0) && !IsWithinPopup(EventSystem.Current?.Hovered))
            Close();
    }

    /// <summary>Shows the popup, returning false when its configured template is incomplete.</summary>
    public bool Show()
    {
        if (_open) return true;
        if (!Application.IsPlaying || !IsInteractable()) return false;
        if (!ValidateTemplate()) return false;

        _focusBeforeOpen = EventSystem.Current?.Selected;
        _lastTemplateError = null;
        _open = true;
        _itemTemplate!.GameObject.Enabled = false;
        ShowPopup();
        RebuildItems();
        PositionPopup();

        if (_items.Count != 0)
        {
            int selected = Math.Clamp(_value, 0, _items.Count - 1);
            EventSystem.Current?.SetSelected(_items[selected].Button.GameObject);
        }
        return true;
    }

    /// <summary>Hides the popup and restores the control that had focus before it opened.</summary>
    public void Hide() => Close();

    public void Toggle()
    {
        if (_open) Close();
        else Show();
    }

    private void Close(bool restoreFocus = true)
    {
        if (!_open) return;
        _open = false;
        HidePopup();

        GameObject? restore = _focusBeforeOpen;
        _focusBeforeOpen = null;
        if (restoreFocus && restore is { IsDisposed: false, EnabledInHierarchy: true })
            EventSystem.Current?.SetSelected(restore);
    }

    private void SetValue(int value, bool notify)
    {
        int clamped = _options.Count == 0 ? 0 : Math.Clamp(value, 0, _options.Count - 1);
        bool changed = clamped != _value;
        _value = clamped;
        UpdateCaption();
        RebuildOpenItems();
        if (!changed || !notify) return;

        try { OnValueChanged?.Invoke(_value); }
        catch (Exception ex) { Debug.LogError($"[Dropdown] OnValueChanged on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
        _onValueChanged.Invoke();
    }

    private void ClampValue() => _value = _options.Count == 0 ? 0 : Math.Clamp(_value, 0, _options.Count - 1);

    private void UpdateCaption()
    {
        if (_captionText != null)
            _captionText.Text = _value >= 0 && _value < _options.Count ? _options[_value] : string.Empty;
    }

    private void RebuildOpenItems()
    {
        if (_open) RebuildItems();
    }

    private void RebuildItems()
    {
        if (_optionsRoot == null) return;
        while (_items.Count < _options.Count)
        {
            Item? item = CreateItem(_items.Count);
            if (item == null)
            {
                Close(restoreFocus: false);
                return;
            }
            _items.Add(item);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            Item item = _items[i];
            bool used = i < _options.Count;
            item.GameObject.Enabled = used;
            if (!used) continue;

            item.Label.Text = _options[i];
            item.Label.Size = _itemTextSize;
            item.Label.TextColor = _itemTextColor;
            item.Background.Color = i == _value ? _itemSelectedColor : _itemColor;

            RectTransform rect = item.GameObject.RectTransform!;
            rect.AnchorMin = new Float2(0f, 1f);
            rect.AnchorMax = new Float2(1f, 1f);
            rect.Pivot = new Float2(0.5f, 1f);
            rect.SizeDelta = new Float2(0f, _itemHeight);
            rect.AnchoredPosition = new Float2(0f, -i * _itemHeight);
        }

        _optionsRoot.SizeDelta = new Float2(_optionsRoot.SizeDelta.X, _options.Count * _itemHeight);
    }

    private Item? CreateItem(int index)
    {
        if (_itemTemplate == null || _optionsRoot == null) return null;
        EchoObject snapshot = Serializer.Serialize(typeof(GameObject), _itemTemplate.GameObject);
        GameObject? instance = Serializer.Deserialize<GameObject>(snapshot);
        if (instance == null)
        {
            ReportTemplateError("ItemTemplate", "could not be deserialized into a GameObject");
            return null;
        }

        instance.Name = $"Dropdown Item {index}";
        instance.EnsureRectTransform();
        instance.Enabled = false;
        instance.SetParent(_optionsRoot.GameObject, worldPositionStays: false);

        Button? button = instance.GetComponentInChildren<Button>(includeSelf: true, includeInactive: true);
        Image? background = instance.GetComponentInChildren<Image>(includeSelf: true, includeInactive: true);
        TextComponent? label = instance.GetComponentInChildren<TextComponent>(includeSelf: true, includeInactive: true);
        if (button == null || background == null || label == null)
        {
            instance.Dispose();
            ReportTemplateError("ItemTemplate", "requires a package Button, Image, and TextComponent in its hierarchy");
            return null;
        }
        return new Item(this, index, instance, button, background, label);
    }

    private bool ValidateTemplate()
    {
        if (_optionsRoot == null)
        {
            ReportTemplateError("OptionsRoot", "is not assigned");
            return false;
        }
        if (_itemTemplate == null)
        {
            ReportTemplateError("ItemTemplate", "is not assigned");
            return false;
        }
        if (_itemTemplate.GameObject.IsDisposed || _optionsRoot.GameObject.IsDisposed)
        {
            ReportTemplateError("ItemTemplate", "references a disposed GameObject");
            return false;
        }
        if (ReferenceEquals(_itemTemplate.GameObject, _optionsRoot.GameObject))
        {
            ReportTemplateError("ItemTemplate", "cannot be the same GameObject as OptionsRoot");
            return false;
        }
        if (_itemTemplate.GameObject.GetComponentInChildren<Button>(includeSelf: true, includeInactive: true) == null ||
            _itemTemplate.GameObject.GetComponentInChildren<Image>(includeSelf: true, includeInactive: true) == null ||
            _itemTemplate.GameObject.GetComponentInChildren<TextComponent>(includeSelf: true, includeInactive: true) == null)
        {
            ReportTemplateError("ItemTemplate", "requires a package Button, Image, and TextComponent in its hierarchy");
            return false;
        }
        return true;
    }

    private void PositionPopup()
    {
        if (_optionsRoot == null || GameObject.RectTransform == null) return;
        Rect control = GameObject.RectTransform.ComputedRect;
        float width = _optionsRoot.ComputedRect.Size.X;
        if (width <= 0f) width = control.Size.X;
        float height = _options.Count * _itemHeight;
        float minY = control.Min.Y - height;

        if (_flipOnOverflow && TryGetCanvasBounds(out Rect canvas) && minY < canvas.Min.Y && control.Max.Y + height <= canvas.Max.Y)
            minY = control.Max.Y;

        _optionsRoot.ComputedRect = new Rect(control.Min.X, minY, control.Min.X + width, minY + height);
    }

    private bool TryGetCanvasBounds(out Rect bounds)
    {
        for (GameObject? current = GameObject; current != null; current = current.Parent)
        {
            GameCanvas? canvas = current.GetComponent<GameCanvas>();
            if (canvas != null)
            {
                bounds = canvas.RootRect;
                return bounds.Size.X > 0f && bounds.Size.Y > 0f;
            }
        }
        bounds = default;
        return false;
    }

    private bool IsWithinPopup(GameObject? target)
    {
        return IsDescendantOf(target, GameObject)
            || IsDescendantOf(target, _optionsRoot?.GameObject)
            || IsDescendantOf(target, _overlayRoot?.GameObject);
    }

    private static bool IsDescendantOf(GameObject? target, GameObject? ancestor)
    {
        for (GameObject? current = target; current != null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void ShowPopup()
    {
        if (_overlayRoot != null && !ReferenceEquals(_overlayRoot, _optionsRoot))
            _overlayRoot.GameObject.Enabled = true;
        if (_optionsRoot != null)
            _optionsRoot.GameObject.Enabled = true;
    }

    private void HidePopup()
    {
        if (_optionsRoot is { GameObject.IsDisposed: false })
            _optionsRoot.GameObject.Enabled = false;
        if (_overlayRoot is { GameObject.IsDisposed: false } && !ReferenceEquals(_overlayRoot, _optionsRoot))
            _overlayRoot.GameObject.Enabled = false;
    }

    private void MarkTemplateChanged()
    {
        _lastTemplateError = null;
        if (_open)
        {
            Close(restoreFocus: false);
            Show();
        }
    }

    private void ReportTemplateError(string field, string problem)
    {
        string error = $"{GetHierarchyPath()}/Dropdown.{field} {problem}.";
        if (string.Equals(_lastTemplateError, error, StringComparison.Ordinal)) return;
        _lastTemplateError = error;
        Debug.LogError($"[XGUI Dropdown] {error}");
    }

    private string GetHierarchyPath()
    {
        var path = new System.Text.StringBuilder();
        for (GameObject? current = GameObject; current != null; current = current.Parent)
        {
            if (path.Length != 0) path.Insert(0, '/');
            path.Insert(0, current.Name);
        }
        return path.ToString();
    }

    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public sealed class ItemCancelHandler : MonoBehaviour, ICancelHandler
    {
        [SerializeIgnore] private Dropdown? _owner;

        internal void Initialize(Dropdown owner) => _owner = owner;

        public void OnCancel() => _owner?.Close();
    }

    private sealed class Item
    {
        private readonly Dropdown _owner;
        private readonly int _index;

        public Item(Dropdown owner, int index, GameObject gameObject, Button button, Image background, TextComponent label)
        {
            _owner = owner;
            _index = index;
            GameObject = gameObject;
            Button = button;
            Background = background;
            Label = label;
            ItemCancelHandler cancelHandler = gameObject.GetComponent<ItemCancelHandler>()
                ?? gameObject.AddComponent<ItemCancelHandler>();
            cancelHandler.Initialize(owner);
            button.OnClick += OnClicked;
        }

        public GameObject GameObject { get; }
        public Button Button { get; }
        public Image Background { get; }
        public TextComponent Label { get; }

        private void OnClicked()
        {
            _owner.SetValue(_index, notify: true);
            _owner.Close();
        }
    }
}
