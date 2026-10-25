// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

public enum SelectionState
{
    Normal,
    Highlighted,
    Pressed,
    Selected,
    Disabled,
}

/// <summary>
/// Base class for every interactive UI widget - buttons, toggles, sliders, dropdowns.
/// Tracks the pointer state machine, drives a sibling <see cref="UIImage"/>'s color
/// across the four states, fires SFX through <see cref="UISounds"/>, and exposes
/// per-instance overrides for both the colors and the audio.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class Selectable : UIBehaviour,
    IPointerEnterHandler, IPointerExitHandler,
    IPointerDownHandler, IPointerUpHandler,
    ISelectHandler, IDeselectHandler, IMoveHandler
{
    public enum Transition { None, ColorTint, SpriteSwap }

    /// <summary>State-specific source sprites used when <see cref="Transition.SpriteSwap"/> is active.</summary>
    [Serializable]
    public sealed class SpriteSwapState
    {
        [SerializeField] private AssetRef<Sprite> _highlightedSprite;
        [SerializeField] private AssetRef<Sprite> _pressedSprite;
        [SerializeField] private AssetRef<Sprite> _selectedSprite;
        [SerializeField] private AssetRef<Sprite> _disabledSprite;

        public AssetRef<Sprite> HighlightedSprite { get => _highlightedSprite; set => _highlightedSprite = value; }
        public AssetRef<Sprite> PressedSprite { get => _pressedSprite; set => _pressedSprite = value; }
        public AssetRef<Sprite> SelectedSprite { get => _selectedSprite; set => _selectedSprite = value; }
        public AssetRef<Sprite> DisabledSprite { get => _disabledSprite; set => _disabledSprite = value; }
    }

    [Serializable]
    public sealed class Navigation
    {
        public enum Mode { None, Automatic, Horizontal, Vertical, Explicit }
        [SerializeField] private Mode _mode = Mode.Automatic;
        [SerializeField] private Selectable? _selectOnUp;
        [SerializeField] private Selectable? _selectOnDown;
        [SerializeField] private Selectable? _selectOnLeft;
        [SerializeField] private Selectable? _selectOnRight;
        public Mode NavigationMode { get => _mode; set => _mode = value; }
        public Selectable? SelectOnUp { get => _selectOnUp; set => _selectOnUp = value; }
        public Selectable? SelectOnDown { get => _selectOnDown; set => _selectOnDown = value; }
        public Selectable? SelectOnLeft { get => _selectOnLeft; set => _selectOnLeft = value; }
        public Selectable? SelectOnRight { get => _selectOnRight; set => _selectOnRight = value; }
    }

    [SerializeField] private Navigation _navigation = new();
    [SerializeField] private Transition _transition = Transition.ColorTint;
    [SerializeField] private SpriteSwapState _spriteState = new();
    public Navigation NavigationSettings { get => _navigation; set => _navigation = value ?? new Navigation(); }
    public Transition TransitionMode
    {
        get => _transition;
        set
        {
            if (_transition == value) return;
            if (_transition == Transition.SpriteSwap) RestoreDefaultSprite();
            _transition = value;
            RefreshState(immediate: true);
        }
    }
    public SpriteSwapState SpriteState { get => _spriteState; set { _spriteState = value ?? new SpriteSwapState(); RefreshState(immediate: true); } }
    // ============================================================
    // Interactability
    // ============================================================

    [SerializeField] private bool _interactable = true;
    /// <summary>When false, the widget is rendered in the Disabled state and ignores clicks (but plays the denied SFX).</summary>
    public bool Interactable
    {
        get => _interactable;
        set
        {
            if (_interactable == value) return;
            _interactable = value;
            RefreshState(immediate: false);
        }
    }

    /// <summary>
    /// The effective interactable state: the local <see cref="Interactable"/> flag AND no enclosing
    /// <see cref="CanvasGroup"/> (up to the canvas root, or the nearest one that ignores its parents)
    /// has <see cref="CanvasGroup.Interactable"/> turned off. Pointer/submit handling gates on this, so
    /// a non-interactable group makes its whole subtree inert while still blocking click-through.
    /// </summary>
    public bool IsInteractable()
    {
        if (!_interactable) return false;

        GameObject? node = GameObject;
        while (node != null)
        {
            CanvasGroup? grp = node.GetComponent<CanvasGroup>();
            if (grp != null && grp.EnabledInHierarchy)
            {
                if (!grp.Interactable) return false;
                if (grp.IgnoreParentGroups) break;
            }
            if (node.GetComponent<GameCanvas>() != null) break; // reached the canvas root
            node = node.Parent;
        }
        return true;
    }

    /// <summary>Re-evaluate the visual state - called when an ancestor <see cref="CanvasGroup"/>
    /// toggles interactivity (there is no automatic notification for that).</summary>
    public void RefreshInteractable() => RefreshState(immediate: false);

    // ============================================================
    // Target graphic - the old UIImage field remains serialized for legacy scenes. New package
    // graphics use the MonoBehaviour component bridge below, without making Runtime reference XGUI.
    // ============================================================

    [SerializeField] private UIImage? _targetGraphic;
    [SerializeField] private MonoBehaviour? _targetGraphicComponent;
    [SerializeIgnore] private ISelectableGraphic? _resolvedGraphicTarget;
    [SerializeIgnore] private ISelectableSpriteGraphic? _spriteSwapTarget;
    [SerializeIgnore] private AssetRef<Sprite> _defaultSprite;

    /// <summary>Any component implementing the runtime color bridge, including XGUI Graphic.</summary>
    public MonoBehaviour? TargetGraphicComponent
    {
        get => _targetGraphicComponent;
        set
        {
            RestoreDefaultSprite();
            _targetGraphicComponent = value is ISelectableGraphic ? value : null;
            _resolvedGraphicTarget = _targetGraphicComponent as ISelectableGraphic;
            ResetSpriteSwapTarget();
            RefreshState(immediate: true);
        }
    }

    /// <summary>Runtime bridge used by package controls; not serialized as an interface value.</summary>
    public ISelectableGraphic? GraphicTarget
        => ResolveGraphicTarget();

    /// <summary>The <see cref="UIImage"/> whose <c>Color</c> the state machine drives. Defaults to a UIImage on this GameObject.</summary>
    public UIImage? TargetGraphic
    {
        get
        {
            if (_targetGraphic != null) return _targetGraphic;
            if (_targetGraphicComponent is UIImage legacy) return _targetGraphic = legacy;
            return _targetGraphic = GetComponent<UIImage>();
        }
        set
        {
            RestoreDefaultSprite();
            _targetGraphic = value;
            _targetGraphicComponent = value;
            _resolvedGraphicTarget = value;
            ResetSpriteSwapTarget();
            RefreshState(immediate: true);
        }
    }

    // ============================================================
    // Color tinting per state
    // ============================================================

    [SerializeField] private Color _normalColor      = new(1f, 1f, 1f, 1f);
    [SerializeField] private Color _highlightedColor = new(0.96f, 0.96f, 0.96f, 1f);
    [SerializeField] private Color _pressedColor     = new(0.78f, 0.78f, 0.78f, 1f);
    [SerializeField] private Color _selectedColor    = new(0.96f, 0.96f, 0.96f, 1f);
    [SerializeField] private Color _disabledColor    = new(0.78f, 0.78f, 0.78f, 0.5f);

    public Color NormalColor      { get => _normalColor;      set { _normalColor = value;      RefreshState(immediate: false); } }
    public Color HighlightedColor { get => _highlightedColor; set { _highlightedColor = value; RefreshState(immediate: false); } }
    public Color PressedColor     { get => _pressedColor;     set { _pressedColor = value;     RefreshState(immediate: false); } }
    public Color SelectedColor    { get => _selectedColor;    set { _selectedColor = value;    RefreshState(immediate: false); } }
    public Color DisabledColor    { get => _disabledColor;    set { _disabledColor = value;    RefreshState(immediate: false); } }

    /// <summary>Seconds the tint takes to lerp to a new target color. 0 = snap.</summary>
    [SerializeField] private float _transitionDuration = 0.08f;
    public float TransitionDuration { get => _transitionDuration; set => _transitionDuration = Maths.Max(0f, value); }

    // ============================================================
    // Runtime state
    // ============================================================

    [SerializeIgnore] private bool _isHovered;
    [SerializeIgnore] private bool _isPressed;
    [SerializeIgnore] private bool _isSelected;
    [SerializeIgnore] private SelectionState _currentState = SelectionState.Normal;
    [SerializeIgnore] private Color _displayedColor = Color.White;
    [SerializeIgnore] private Color _fromColor = Color.White;
    [SerializeIgnore] private Color _toColor = Color.White;
    [SerializeIgnore] private float _transitionElapsed;

    /// <summary>The current high-level state. Read-only for derived classes.</summary>
    public SelectionState CurrentState => _currentState;

    /// <summary>Whether the pointer is currently hovering this widget.</summary>
    public bool IsHovered => _isHovered;

    /// <summary>Whether the widget is currently held down.</summary>
    public bool IsPressed => _isPressed;

    // ============================================================
    // UIBehaviour overrides - Selectable has no geometry of its own.
    // ============================================================

    /// <inheritdoc/>
    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { /* no geometry */ }

    public override bool IsRaycastCandidate => true;

    public override void OnEnable()
    {
        base.OnEnable();
        RefreshState(immediate: true);
    }

    /// <summary>Drives the color lerp toward the current state's target. Called every frame.</summary>
    public override void Update()
    {
        if (!Application.IsPlaying) return;
        if (_transition == Transition.SpriteSwap) return;
        ISelectableGraphic? target = ResolveGraphicTarget();
        if (target == null) return;

        float dur = _transitionDuration;
        if (dur <= 0f || _transitionElapsed >= dur)
        {
            if (target.Color != _toColor)
                target.Color = _toColor;
            _displayedColor = _toColor;
            return;
        }

        _transitionElapsed += Time.DeltaTime;
        float t = Maths.Clamp(_transitionElapsed / dur, 0f, 1f);
        _displayedColor = Color.Lerp(_fromColor, _toColor, t);
        target.Color = _displayedColor;
    }

    // ============================================================
    // Pointer
    // ============================================================

    public virtual void OnPointerEnter(PointerEventData e)
    {
        _isHovered = true;
        RefreshState(immediate: false);
    }

    public virtual void OnPointerExit(PointerEventData e)
    {
        _isHovered = false;
        // A press-then-leave keeps the pressed visual until release, matching common UI behavior.
        if (!_isPressed) RefreshState(immediate: false);
    }

    public virtual void OnPointerDown(PointerEventData e)
    {
        if (e.Button != MouseButton.Left) return;

        if (!IsInteractable()) return;

        _isPressed = true;
        RefreshState(immediate: false);
    }

    public virtual void OnPointerUp(PointerEventData e)
    {
        if (e.Button != MouseButton.Left) return;
        _isPressed = false;
        RefreshState(immediate: false);
    }

    public virtual void OnSelect()
    {
        _isSelected = true;
        RefreshState(immediate: false);
    }

    public virtual void OnDeselect()
    {
        _isSelected = false;
        RefreshState(immediate: false);
    }

    public virtual void OnMove(MoveDirection direction)
    {
        if (!IsInteractable() || _navigation.NavigationMode == Navigation.Mode.None) return;
        Selectable? target = direction switch
        {
            MoveDirection.Up => _navigation.SelectOnUp,
            MoveDirection.Down => _navigation.SelectOnDown,
            MoveDirection.Left => _navigation.SelectOnLeft,
            MoveDirection.Right => _navigation.SelectOnRight,
            _ => null,
        };
        if (target == null && _navigation.NavigationMode != Navigation.Mode.Explicit)
            target = FindAutomatic(direction);
        if (target is { EnabledInHierarchy: true } && target.IsInteractable())
            EventSystem.ForScene(GameObject.Scene)?.SetSelected(target.GameObject);
    }

    private Selectable? FindAutomatic(MoveDirection direction)
    {
        Scene? scene = GameObject.Scene;
        if (scene == null) return null;
        if (_navigation.NavigationMode == Navigation.Mode.Horizontal
            && direction is not (MoveDirection.Left or MoveDirection.Right)) return null;
        if (_navigation.NavigationMode == Navigation.Mode.Vertical
            && direction is not (MoveDirection.Up or MoveDirection.Down)) return null;

        Rect sourceRect = GameObject.RectTransform?.ComputedRect ?? default;
        Float2 source = (sourceRect.Min + sourceRect.Max) * 0.5f;
        Selectable? best = null;
        float bestScore = float.MaxValue;
        foreach (GameObject go in scene.ActiveObjects)
        {
            Selectable? candidate = go.GetComponent<Selectable>();
            if (candidate == null || ReferenceEquals(candidate, this) || !candidate.EnabledInHierarchy || !candidate.IsInteractable()) continue;
            RectTransform? rt = go.RectTransform;
            if (rt == null) continue;
            Rect rect = rt.ComputedRect;
            Float2 delta = (rect.Min + rect.Max) * 0.5f - source;
            bool valid = direction switch
            {
                MoveDirection.Up => delta.Y > 0f,
                MoveDirection.Down => delta.Y < 0f,
                MoveDirection.Left => delta.X < 0f,
                MoveDirection.Right => delta.X > 0f,
                _ => false,
            };
            if (!valid) continue;
            float primary = direction is MoveDirection.Left or MoveDirection.Right ? MathF.Abs(delta.X) : MathF.Abs(delta.Y);
            float secondary = direction is MoveDirection.Left or MoveDirection.Right ? MathF.Abs(delta.Y) : MathF.Abs(delta.X);
            float score = primary * primary + secondary * secondary * 4f;
            if (score < bestScore) { bestScore = score; best = candidate; }
        }
        return best;
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>Re-evaluates the active <see cref="SelectionState"/> and starts a tint lerp toward it.</summary>
    protected void RefreshState(bool immediate)
    {
        SelectionState next = ComputeState();
        if (next == _currentState && !immediate) return;

        _currentState = next;
        if (_transition == Transition.SpriteSwap)
        {
            ApplySpriteSwap();
            return;
        }

        Color targetColor = next switch
        {
            SelectionState.Disabled    => _disabledColor,
            SelectionState.Pressed     => _pressedColor,
            SelectionState.Highlighted => _highlightedColor,
            SelectionState.Selected    => _selectedColor,
            _                          => _normalColor,
        };

        _fromColor = _displayedColor;
        _toColor = targetColor;
        bool instant = immediate || _transition == Transition.None;
        _transitionElapsed = instant ? float.PositiveInfinity : 0f;

        ISelectableGraphic? target = ResolveGraphicTarget();
        if (instant && target != null)
        {
            target.Color = targetColor;
            _displayedColor = targetColor;
        }
    }

    private ISelectableGraphic? ResolveGraphicTarget()
    {
        if (_resolvedGraphicTarget != null) return _resolvedGraphicTarget;
        if (_targetGraphicComponent is ISelectableGraphic component)
            return _resolvedGraphicTarget = component;
        if (_targetGraphic != null)
            return _resolvedGraphicTarget = _targetGraphic;
        return _resolvedGraphicTarget = GetComponent<UIImage>();
    }

    private void ApplySpriteSwap()
    {
        ISelectableSpriteGraphic? target = ResolveSpriteSwapTarget();
        if (target == null) return;

        AssetRef<Sprite> sprite = _currentState switch
        {
            SelectionState.Highlighted => _spriteState.HighlightedSprite,
            SelectionState.Pressed => _spriteState.PressedSprite,
            SelectionState.Selected => _spriteState.SelectedSprite,
            SelectionState.Disabled => _spriteState.DisabledSprite,
            _ => _defaultSprite,
        };
        target.Sprite = sprite.IsExplicitNull ? _defaultSprite : sprite;
    }

    private ISelectableSpriteGraphic? ResolveSpriteSwapTarget()
    {
        if (ResolveGraphicTarget() is not ISelectableSpriteGraphic target) return null;
        if (!ReferenceEquals(_spriteSwapTarget, target))
        {
            _spriteSwapTarget = target;
            _defaultSprite = target.Sprite;
        }
        return target;
    }

    private void ResetSpriteSwapTarget()
    {
        _spriteSwapTarget = null;
        _defaultSprite = default;
    }

    private void RestoreDefaultSprite()
    {
        if (_spriteSwapTarget != null)
            _spriteSwapTarget.Sprite = _defaultSprite;
    }

    private SelectionState ComputeState()
    {
        if (!IsInteractable()) return SelectionState.Disabled;
        if (_isPressed)     return SelectionState.Pressed;
        if (_isHovered)     return SelectionState.Highlighted;
        if (_isSelected)    return SelectionState.Selected;
        return SelectionState.Normal;
    }
}
