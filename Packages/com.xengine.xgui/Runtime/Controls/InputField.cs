using System;
using System.Globalization;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Package-owned single-line text editor. The committed value is kept separate from the platform
/// composition string so an IME can replace its preview without firing value-changed callbacks until
/// the platform commits it. Caret and selection positions are always snapped to Unicode text-element
/// (grapheme) boundaries; serialized field names mirror the legacy UIInputField for Echo redirects.
/// </summary>
[AddComponentMenu("UI/Input Field")]
[RequireComponent(typeof(RectTransform))]
public sealed class InputField : Selectable,
    IPointerDownHandler, IBeginDragHandler, IDragHandler,
    ISubmitHandler, ICancelHandler
{
    private const float BlinkRate = 0.53f;
    private const float InitialRepeatDelay = 0.4f;
    private const float RepeatInterval = 0.035f;

    private static readonly Color s_transparent = new(0f, 0f, 0f, 0f);

    // Keep these names and defaults in lockstep with XEngine.Runtime.UI.UIInputField.
    [SerializeField] private TextComponent? _textComponent;
    public TextComponent? TextComponent
    {
        get => _textComponent;
        set { _textComponent = value; MarkDisplayDirty(); }
    }

    [SerializeField] private TextComponent? _placeholder;
    public TextComponent? Placeholder
    {
        get => _placeholder;
        set { _placeholder = value; UpdatePlaceholder(); }
    }

    [SerializeField] private RectTransform? _caret;
    public RectTransform? Caret { get => _caret; set { _caret = value; _caretGraphic = null; _caretImage = null; } }

    [SerializeField] private RectTransform? _selection;
    public RectTransform? Selection { get => _selection; set { _selection = value; _selectionGraphic = null; _selectionImage = null; } }

    [SerializeField] private RectTransform? _textArea;
    public RectTransform? TextArea { get => _textArea; set => _textArea = value; }

    [SerializeField] private string _text = string.Empty;
    public string Text
    {
        get => _text;
        set
        {
            string sanitized = Sanitize(value);
            if (sanitized == _text) return;
            ReplaceCommittedText(sanitized, notify: true);
        }
    }

    [SerializeField] private int _characterLimit;
    public int CharacterLimit
    {
        get => _characterLimit;
        set
        {
            int limit = Math.Max(0, value);
            if (_characterLimit == limit) return;
            _characterLimit = limit;
            string sanitized = Sanitize(_text);
            if (sanitized != _text) ReplaceCommittedText(sanitized, notify: true);
            else MarkDisplayDirty();
        }
    }

    [SerializeField] private InputFieldContentType _contentType = InputFieldContentType.Standard;
    public InputFieldContentType ContentType
    {
        get => _contentType;
        set
        {
            if (_contentType == value) return;
            _contentType = value;
            _keyboardType = InputFieldContent.KeyboardType(value);
            if (InputFieldContent.IsSecure(value))
                _isPassword = true;
            string sanitized = Sanitize(_text);
            if (sanitized != _text) ReplaceCommittedText(sanitized, notify: true);
            else MarkDisplayDirty();
        }
    }

    [SerializeField] private TouchScreenKeyboardType _keyboardType = TouchScreenKeyboardType.Default;
    public TouchScreenKeyboardType KeyboardType
    {
        get => _keyboardType;
        set => _keyboardType = value;
    }

    [SerializeField] private bool _isPassword;
    public bool IsPassword { get => _isPassword; set { if (_isPassword == value) return; _isPassword = value; MarkDisplayDirty(); } }

    [SerializeField] private char _maskChar = '*';
    public char MaskChar { get => _maskChar; set { if (_maskChar == value) return; _maskChar = value; MarkDisplayDirty(); } }

    [SerializeField] private bool _readOnly;
    public bool ReadOnly { get => _readOnly; set => _readOnly = value; }

    [SerializeField] private bool _selectAllOnFocus = true;
    public bool SelectAllOnFocus { get => _selectAllOnFocus; set => _selectAllOnFocus = value; }

    [SerializeField] private Color _caretColor = new(0.90f, 0.90f, 0.92f, 1f);
    public Color CaretColor { get => _caretColor; set => _caretColor = value; }

    [SerializeField] private Color _selectionColor = new(0.25f, 0.47f, 0.85f, 0.5f);
    public Color SelectionColor { get => _selectionColor; set => _selectionColor = value; }

    public event Action<string>? OnValueChanged;
    public event Action<string>? OnEndEdit;
    public event Action<string>? OnSubmitted;

    [SerializeField] private XEngineAction _onValueChanged = new();
    public XEngineAction ValueChangedAction => _onValueChanged;

    [SerializeField] private XEngineAction _onEndEdit = new();
    public XEngineAction EndEditAction => _onEndEdit;

    [SerializeIgnore] private int _caretPos;
    [SerializeIgnore] private int _selectionAnchor;
    [SerializeIgnore] private int[] _boundaries = [0];
    [SerializeIgnore] private string _composition = string.Empty;
    [SerializeIgnore] private int _compositionStart;
    [SerializeIgnore] private float _scrollX;
    [SerializeIgnore] private float _blinkTimer;
    [SerializeIgnore] private bool _blinkOn = true;
    [SerializeIgnore] private KeyCode _repeatKey = KeyCode.Unknown;
    [SerializeIgnore] private float _repeatDelay;
    [SerializeIgnore] private bool _placeholderShown = true;
    [SerializeIgnore] private bool _displayDirty = true;
    [SerializeIgnore] private string _displayText = string.Empty;
    [SerializeIgnore] private int _displayCaret;
    [SerializeIgnore] private int _displaySelectionStart;
    [SerializeIgnore] private int _displaySelectionEnd;
    [SerializeIgnore] private ITextInputHandler? _textInputHandler;
    [SerializeIgnore] private Graphic? _caretGraphic;
    [SerializeIgnore] private Graphic? _selectionGraphic;
    [SerializeIgnore] private UIImage? _caretImage;
    [SerializeIgnore] private UIImage? _selectionImage;
    [SerializeIgnore] private bool _syncingInputState;

    private RectTransform Area => _textArea ?? _textComponent?.GameObject.RectTransform ?? GameObject.RectTransform!;
    private int SelMin => Math.Min(_selectionAnchor, _caretPos);
    private int SelMax => Math.Max(_selectionAnchor, _caretPos);
    public bool HasSelection => _selectionAnchor != _caretPos;
    public int CaretPosition => _caretPos;
    public int SelectionStart => SelMin;
    public int SelectionLength => SelMax - SelMin;
    public string Composition => _composition;
    public bool IsComposing => _composition.Length != 0;
    private bool IsFocused => ReferenceEquals(EventSystem.Current?.Selected, GameObject) && IsInteractable();

    public override void OnEnable()
    {
        base.OnEnable();
        RebuildBoundaries();
        ClampCaret();
        MarkDisplayDirty();
        UpdateDisplay();
        RefreshTextInputSubscription();
        if (IsFocused) BeginTextInput();
    }

    public override void OnDisable()
    {
        EndTextInput();
        UnsubscribeTextInput();
        base.OnDisable();
    }

    public override void OnValidate()
    {
        base.OnValidate();
        string sanitized = Sanitize(_text);
        if (sanitized != _text) _text = sanitized;
        RebuildBoundaries();
        ClampCaret();
        MarkDisplayDirty();
        UpdateDisplay();
    }

    public override void OnSelect()
    {
        base.OnSelect();
        if (_selectAllOnFocus) SelectAll();
        ResetBlink();
        BeginTextInput();
    }

    public override void OnDeselect()
    {
        base.OnDeselect();
        _composition = string.Empty;
        _selectionAnchor = _caretPos;
        MarkDisplayDirty();
        EndTextInput();
        EndEdit();
    }

    public void OnSubmit()
    {
        if (!IsInteractable()) return;
        try { OnSubmitted?.Invoke(_text); }
        catch (Exception ex) { Debug.LogError($"[InputField] OnSubmitted on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
        EventSystem.Current?.SetSelected(null);
    }

    public void OnCancel()
    {
        if (_composition.Length == 0) return;
        _composition = string.Empty;
        MarkDisplayDirty();
        ResetBlink();
        SyncTextInputState();
    }

    public override void OnPointerDown(PointerEventData e)
    {
        base.OnPointerDown(e);
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        SetCaret(IndexFromPointer(e), Input.IsShiftPressed);
        e.Use();
    }

    public void OnBeginDrag(PointerEventData e) { }

    public void OnDrag(PointerEventData e)
    {
        if (e.Button != MouseButton.Left || !IsInteractable()) return;
        SetCaret(IndexFromPointer(e), select: true);
        e.Use();
    }

    public override void Update()
    {
        base.Update();
        RefreshTextInputSubscription();
        if (!Application.IsPlaying) return;

        UpdateDisplay();
        bool focused = IsFocused;
        if (focused)
        {
            ProcessKeyboard();
            _blinkTimer += Time.DeltaTime;
            if (_blinkTimer >= BlinkRate)
            {
                _blinkTimer -= BlinkRate;
                _blinkOn = !_blinkOn;
            }
            UpdateCaretVisual();
        }
        else if (_scrollX != 0f)
        {
            _scrollX = 0f;
            ShiftText(0f);
        }
        ApplyCaretColors(focused);
    }

    private void RefreshTextInputSubscription()
    {
        ITextInputHandler? current = Input.Current as ITextInputHandler;
        if (ReferenceEquals(current, _textInputHandler)) return;
        UnsubscribeTextInput();
        if (current != null)
        {
            current.OnTextInput += HandleTextInput;
            _textInputHandler = current;
        }
    }

    private void UnsubscribeTextInput()
    {
        if (_textInputHandler == null) return;
        _textInputHandler.OnTextInput -= HandleTextInput;
        _textInputHandler = null;
    }

    private void BeginTextInput()
    {
        RefreshTextInputSubscription();
        if (_textInputHandler is not ITextInputService service || !service.IsTextInputSupported) return;
        service.SetTextInputOptions(_keyboardType, _isPassword || InputFieldContent.IsSecure(_contentType));
        SyncTextInputState();
        Input.SetTextInputEnabled(true);
    }

    private void EndTextInput()
    {
        if (_textInputHandler is ITextInputService service && service.IsTextInputSupported)
            Input.SetTextInputEnabled(false);
    }

    private void SyncTextInputState()
    {
        if (_syncingInputState) return;
        if (_textInputHandler is not ITextInputService service || !service.IsTextInputSupported) return;
        _syncingInputState = true;
        try { service.SetTextInputState(_text, SelMin, SelectionLength); }
        finally { _syncingInputState = false; }
    }

    private void HandleTextInput(TextInputState state)
    {
        if (_syncingInputState || !IsFocused || !state.IsActive) return;

        if (state.Text != _text)
        {
            if (_readOnly)
            {
                SyncTextInputState();
                return;
            }
            string sanitized = Sanitize(state.Text);
            ReplaceCommittedText(sanitized, notify: true);
            int start = SnapToBoundary(state.SelectionStart, forward: false);
            int end = SnapToBoundary(state.SelectionStart + Math.Max(0, state.SelectionLength), forward: true);
            _caretPos = start;
            _selectionAnchor = end;
            ClampCaret();
            MarkDisplayDirty();
            SyncTextInputState();
            return;
        }

        if (state.Composition != _composition)
        {
            if (_composition.Length == 0 && state.Composition.Length != 0)
                _compositionStart = _caretPos;
            _composition = state.Composition ?? string.Empty;
            MarkDisplayDirty();
            ResetBlink();
        }
    }

    private void ProcessKeyboard()
    {
        bool shift = Input.IsShiftPressed;
        bool ctrl = Input.IsCtrlPressed;

        if (_repeatKey != KeyCode.Unknown && !Input.GetKey(_repeatKey))
            _repeatKey = KeyCode.Unknown;

        if (ctrl)
        {
            if (Input.GetKeyDown(KeyCode.A)) SelectAll();
            if (Input.GetKeyDown(KeyCode.C)) Copy();
            if (Input.GetKeyDown(KeyCode.X)) Cut();
            if (Input.GetKeyDown(KeyCode.V)) Paste();
        }

        if (Repeat(KeyCode.Left))      MoveLeft(shift);
        if (Repeat(KeyCode.Right))     MoveRight(shift);
        if (Repeat(KeyCode.Home))      SetCaret(0, shift);
        if (Repeat(KeyCode.End))       SetCaret(_text.Length, shift);
        if (Repeat(KeyCode.Backspace)) Backspace();
        if (Repeat(KeyCode.Delete))    DeleteForward();

        // Desktop handlers expose InputString; mobile handlers deliver commits through TextInputState.
        if (!ctrl && !(_textInputHandler is ITextInputService { IsTextInputSupported: true }))
            Insert(Input.InputString);
    }

    private bool Repeat(KeyCode key)
    {
        if (Input.GetKeyDown(key))
        {
            _repeatKey = key;
            _repeatDelay = InitialRepeatDelay;
            return true;
        }
        if (_repeatKey == key && Input.GetKey(key))
        {
            _repeatDelay -= Time.DeltaTime;
            if (_repeatDelay <= 0f)
            {
                _repeatDelay = RepeatInterval;
                return true;
            }
        }
        return false;
    }

    private void MoveLeft(bool select)
    {
        if (!select && HasSelection) SetCaret(SelMin, false);
        else SetCaret(PreviousBoundary(_caretPos), select);
    }

    private void MoveRight(bool select)
    {
        if (!select && HasSelection) SetCaret(SelMax, false);
        else SetCaret(NextBoundary(_caretPos), select);
    }

    private void SetCaret(int position, bool select)
    {
        int snapped = SnapToBoundary(position, forward: position >= _caretPos);
        if (!select) _selectionAnchor = snapped;
        _caretPos = snapped;
        ResetBlink();
        MarkDisplayDirty();
        SyncTextInputState();
    }

    /// <summary>Selects the whole committed value and places the caret at its end.</summary>
    public void SelectAll()
    {
        _selectionAnchor = 0;
        _caretPos = _text.Length;
        _composition = string.Empty;
        MarkDisplayDirty();
        ResetBlink();
        SyncTextInputState();
    }

    private void Insert(string value)
    {
        if (_readOnly || string.IsNullOrEmpty(value)) return;
        string sanitized = Sanitize(value, applyLimit: false);
        if (sanitized.Length == 0) return;

        int available = _characterLimit == 0
            ? int.MaxValue
            : _characterLimit - GraphemeCount(_text) + (HasSelection ? GraphemeCount(_text.AsSpan(SelMin, SelectionLength)) : 0);
        sanitized = TrimToGraphemeCount(sanitized, Math.Max(0, available));
        if (sanitized.Length == 0 && !HasSelection) return;

        int insertion = SelMin;
        string next = _text.Remove(insertion, SelectionLength).Insert(insertion, sanitized);
        _caretPos = insertion + sanitized.Length;
        _selectionAnchor = _caretPos;
        _composition = string.Empty;
        ReplaceCommittedText(next, notify: true);
        SyncTextInputState();
    }

    private bool DeleteSelection()
    {
        if (_readOnly || !HasSelection) return false;
        int start = SelMin;
        ReplaceCommittedText(_text.Remove(start, SelectionLength), notify: true);
        _caretPos = _selectionAnchor = start;
        SyncTextInputState();
        return true;
    }

    private void Backspace()
    {
        if (_readOnly) return;
        if (DeleteSelection()) return;
        int previous = PreviousBoundary(_caretPos);
        if (previous == _caretPos) return;
        ReplaceCommittedText(_text.Remove(previous, _caretPos - previous), notify: true);
        _caretPos = _selectionAnchor = previous;
        SyncTextInputState();
    }

    private void DeleteForward()
    {
        if (_readOnly) return;
        if (DeleteSelection()) return;
        int next = NextBoundary(_caretPos);
        if (next == _caretPos) return;
        ReplaceCommittedText(_text.Remove(_caretPos, next - _caretPos), notify: true);
        _selectionAnchor = _caretPos;
        SyncTextInputState();
    }

    private void Copy()
    {
        if (_isPassword || !HasSelection) return;
        Input.Clipboard = _text.Substring(SelMin, SelectionLength);
    }

    private void Cut()
    {
        if (_isPassword || _readOnly || !HasSelection) return;
        Input.Clipboard = _text.Substring(SelMin, SelectionLength);
        DeleteSelection();
    }

    private void Paste()
    {
        if (_readOnly) return;
        string clip = Input.Clipboard;
        if (!string.IsNullOrEmpty(clip)) Insert(clip);
    }

    /// <summary>Assigns the committed value without firing value-changed callbacks.</summary>
    public void SetTextWithoutNotify(string value)
    {
        string sanitized = Sanitize(value);
        if (sanitized == _text) return;
        ReplaceCommittedText(sanitized, notify: false);
        SyncTextInputState();
    }

    private void ReplaceCommittedText(string value, bool notify)
    {
        _text = value ?? string.Empty;
        RebuildBoundaries();
        ClampCaret();
        _composition = string.Empty;
        MarkDisplayDirty();
        UpdateDisplay();
        ResetBlink();
        if (!notify) return;
        try { OnValueChanged?.Invoke(_text); }
        catch (Exception ex) { Debug.LogError($"[InputField] OnValueChanged on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
        _onValueChanged.Invoke();
    }

    private void EndEdit()
    {
        try { OnEndEdit?.Invoke(_text); }
        catch (Exception ex) { Debug.LogError($"[InputField] OnEndEdit on '{Name}' threw: {ex.Message}\n{ex.StackTrace}"); }
        _onEndEdit.Invoke();
    }

    private string Sanitize(string? value, bool applyLimit = true)
    {
        string input = InputFieldContent.Sanitize(value, _contentType, applyLimit ? 0 : 0);
        return applyLimit && _characterLimit > 0
            ? TrimToGraphemeCount(input, _characterLimit)
            : input;
    }

    private void RebuildBoundaries()
    {
        int[] starts = _text.Length == 0 ? Array.Empty<int>() : StringInfo.ParseCombiningCharacters(_text);
        _boundaries = new int[starts.Length + 1];
        if (starts.Length != 0) Array.Copy(starts, _boundaries, starts.Length);
        _boundaries[^1] = _text.Length;
    }

    private void ClampCaret()
    {
        _caretPos = SnapToBoundary(Math.Clamp(_caretPos, 0, _text.Length), forward: false);
        _selectionAnchor = SnapToBoundary(Math.Clamp(_selectionAnchor, 0, _text.Length), forward: false);
    }

    private int PreviousBoundary(int position)
    {
        int index = BoundaryIndex(position);
        return index > 0 ? _boundaries[index - 1] : 0;
    }

    private int NextBoundary(int position)
    {
        int index = BoundaryIndex(position);
        return index + 1 < _boundaries.Length ? _boundaries[index + 1] : _text.Length;
    }

    private int BoundaryIndex(int position)
    {
        int lo = 0, hi = _boundaries.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int value = _boundaries[mid];
            if (value == position) return mid;
            if (value < position) lo = mid + 1;
            else hi = mid - 1;
        }
        return Math.Clamp(lo, 0, _boundaries.Length - 1);
    }

    private int SnapToBoundary(int position, bool forward)
    {
        int index = BoundaryIndex(Math.Clamp(position, 0, _text.Length));
        int candidate = _boundaries[index];
        if (candidate == position || index == 0 || index == _boundaries.Length - 1) return candidate;
        int previous = _boundaries[index - 1];
        return forward ? candidate : previous;
    }

    private static int GraphemeCount(string value) =>
        value.Length == 0 ? 0 : StringInfo.ParseCombiningCharacters(value).Length;

    private static int GraphemeCount(ReadOnlySpan<char> value) => GraphemeCount(value.ToString());

    private static string TrimToGraphemeCount(string value, int maxCount)
    {
        if (maxCount <= 0) return string.Empty;
        int[] starts = value.Length == 0 ? Array.Empty<int>() : StringInfo.ParseCombiningCharacters(value);
        if (starts.Length <= maxCount) return value;
        return value.Substring(0, starts[maxCount]);
    }

    private void MarkDisplayDirty() => _displayDirty = true;

    private void RebuildDisplayCache()
    {
        string committed = _isPassword ? new string(_maskChar, _boundaries.Length - 1) : _text;
        int committedCaret = _isPassword ? BoundaryIndex(_caretPos) : _caretPos;
        int committedStart = _isPassword ? BoundaryIndex(SelMin) : SelMin;
        int committedEnd = _isPassword ? BoundaryIndex(SelMax) : SelMax;

        if (_composition.Length == 0)
        {
            _displayText = committed;
            _displayCaret = committedCaret;
            _displaySelectionStart = committedStart;
            _displaySelectionEnd = committedEnd;
        }
        else
        {
            int start = _isPassword ? BoundaryIndex(_compositionStart) : _compositionStart;
            string composition = _isPassword ? new string(_maskChar, GraphemeCount(_composition)) : _composition;
            _displayText = committed.Insert(start, composition);
            _displayCaret = start + composition.Length;
            _displaySelectionStart = _displaySelectionEnd = _displayCaret;
        }
        _displayDirty = false;
    }

    private string DisplayText
    {
        get
        {
            if (_displayDirty) RebuildDisplayCache();
            return _displayText;
        }
    }

    private void UpdateDisplay()
    {
        string display = DisplayText;
        if (_textComponent != null && _textComponent.Text != display)
            _textComponent.Text = display;
        UpdatePlaceholder();
    }

    private void UpdatePlaceholder()
    {
        if (_placeholder == null) return;
        bool show = _text.Length == 0 && _composition.Length == 0;
        if (show == _placeholderShown) return;
        _placeholderShown = show;
        _placeholder.GameObject.Enabled = show;
    }

    private void ResetBlink()
    {
        _blinkOn = true;
        _blinkTimer = 0f;
    }

    private int IndexFromPointer(PointerEventData e)
    {
        Rect area = Area.ComputedRect;
        return IndexFromLocalX(e.DesignPosition.X - area.Min.X + _scrollX);
    }

    private int IndexFromLocalX(float localX)
    {
        string display = DisplayText;
        if (_textComponent == null || display.Length == 0 || localX <= 0f) return 0;

        int[] visualBoundaries = StringInfo.ParseCombiningCharacters(display);
        float previous = 0f;
        for (int i = 0; i < visualBoundaries.Length; i++)
        {
            int end = visualBoundaries[i];
            float width = _textComponent.MeasureWidth(display.Substring(0, end));
            if (localX < (previous + width) * 0.5f)
                return CommittedIndexFromDisplay(i == 0 ? 0 : visualBoundaries[i - 1]);
            previous = width;
        }
        return _text.Length;
    }

    private int CommittedIndexFromDisplay(int displayIndex)
    {
        if (_composition.Length != 0)
        {
            int compositionStart = _isPassword ? BoundaryIndex(_compositionStart) : _compositionStart;
            if (displayIndex >= compositionStart && displayIndex <= _displayCaret)
                return _compositionStart;
            if (displayIndex > _displayCaret)
                displayIndex -= _displayCaret - compositionStart;
        }
        return _isPassword
            ? _boundaries[Math.Clamp(displayIndex, 0, _boundaries.Length - 1)]
            : SnapToBoundary(displayIndex, forward: false);
    }

    private void UpdateCaretVisual()
    {
        if (_textComponent == null) return;
        string display = DisplayText;
        int caret = Math.Clamp(_displayCaret, 0, display.Length);
        float caretX = _textComponent.MeasureWidth(display.Substring(0, caret));
        float viewW = Area.ComputedRect.Size.X;

        if (caretX - _scrollX > viewW) _scrollX = caretX - viewW;
        if (caretX - _scrollX < 0f) _scrollX = caretX;
        float totalW = _textComponent.MeasureWidth(display);
        _scrollX = Maths.Clamp(_scrollX, 0f, MathF.Max(0f, totalW - viewW));
        ShiftText(-_scrollX);

        if (_caret != null)
            _caret.AnchoredPosition = new Float2(caretX - _scrollX, _caret.AnchoredPosition.Y);

        if (_selection != null && HasSelection && _composition.Length == 0)
        {
            float startX = _textComponent.MeasureWidth(display.Substring(0, _displaySelectionStart));
            float endX = _textComponent.MeasureWidth(display.Substring(0, _displaySelectionEnd));
            _selection.AnchoredPosition = new Float2(startX - _scrollX, _selection.AnchoredPosition.Y);
            _selection.SizeDelta = new Float2(endX - startX, _selection.SizeDelta.Y);
        }
    }

    private void ShiftText(float x)
    {
        RectTransform? transform = _textComponent?.GameObject.RectTransform;
        if (transform == null || transform.AnchoredPosition.X == x) return;
        transform.AnchoredPosition = new Float2(x, transform.AnchoredPosition.Y);
    }

    private Graphic? CaretGraphic => _caretGraphic ??= _caret?.GameObject.GetComponent<Graphic>();
    private UIImage? CaretLegacyImage => _caretImage ??= _caret?.GameObject.GetComponent<UIImage>();
    private Graphic? SelectionGraphic => _selectionGraphic ??= _selection?.GameObject.GetComponent<Graphic>();
    private UIImage? SelectionLegacyImage => _selectionImage ??= _selection?.GameObject.GetComponent<UIImage>();

    private void ApplyCaretColors(bool focused)
    {
        Color caretColor = focused && _blinkOn ? _caretColor : s_transparent;
        Color selectionColor = focused && HasSelection && _composition.Length == 0 ? _selectionColor : s_transparent;
        if (CaretGraphic != null) CaretGraphic.Color = caretColor;
        if (CaretLegacyImage != null) CaretLegacyImage.Color = caretColor;
        if (SelectionGraphic != null) SelectionGraphic.Color = selectionColor;
        if (SelectionLegacyImage != null) SelectionLegacyImage.Color = selectionColor;
    }
}
