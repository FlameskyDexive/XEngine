// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Input stages an event-system module may drive for a frame.</summary>
[Flags]
public enum EventSystemInputMode
{
    None = 0,
    Pointer = 1,
    Keyboard = 2,
    All = Pointer | Keyboard,
}

/// <summary>
/// Runtime-neutral contract implemented by package-owned input modules.
/// The host only knows this contract; concrete pointer, keyboard and platform modules stay in XGUI
/// or another package and therefore do not leak into the Runtime assembly.
/// </summary>
public interface IEventSystemInputModule
{
    bool IsModuleSupported { get; }
    bool ShouldActivateModule { get; }
    void ActivateModule(EventSystem eventSystem);
    void DeactivateModule(EventSystem eventSystem);
    void Process(EventSystem eventSystem);
}

/// <summary>
/// Drives GameObject-UI input, Unity-style. A scene needs exactly one enabled <see cref="EventSystem"/>
/// for its <see cref="GameCanvas"/>es to receive pointer and keyboard events. Each frame it hit-tests the
/// pointer against the scene's canvases (via <see cref="UIRaycaster"/>) and dispatches enter/exit, press,
/// drag, click, scroll, submit/cancel and selection events to the components under it.
/// </summary>
/// <remarks>
/// This is entirely game-space: it ticks from <see cref="Update"/> (play mode only) and nothing in the
/// engine loop drives it. A <see cref="GameCanvas"/> has no dependency on it - delete this component and
/// canvases still render, they just stop receiving input - so you can drop in your own event system.
/// Access the active one through <see cref="Current"/>.
/// </remarks>
[AddComponentMenu("UI/Event System")]
[ComponentIcon("")] // ArrowPointer
public sealed class EventSystem : MonoBehaviour
{
    [SerializeIgnore] private static EventSystem? s_current;
    private static readonly List<EventSystem> s_systems = new(4);

    /// <summary>Pointer position in window/viewport pixels, as consumed by the latest UI tick.</summary>
    public static Float2 LastPointerWindow { get; private set; }

    /// <summary>Window/viewport size that the <see cref="LastPointerWindow"/> position is relative to.</summary>
    public static Float2 LastWindowSize { get; private set; }

    /// <summary>The active event system driving UI input, or null when none is enabled.</summary>
    public static EventSystem? Current => s_current;

    /// <summary>
    /// Resolves the enabled event system that belongs to a scene. The global <see cref="Current"/>
    /// reference is retained for ordinary input dispatch, but navigation can be invoked by a
    /// component in a scene that is being enabled while an old scene is still unwinding. In that
    /// transition the global slot may briefly point at the old system, so callers that already know
    /// their scene must use this resolver.
    /// </summary>
    internal static EventSystem? ForScene(Scene? scene)
    {
        EventSystem? current = s_current;
        if (scene == null)
            return current is { EnabledInHierarchy: true } ? current : null;

        if (current is { EnabledInHierarchy: true } && ReferenceEquals(current.Scene, scene))
            return current;

        foreach (EventSystem candidate in scene.FindObjectsOfType<EventSystem>())
        {
            if (!candidate.EnabledInHierarchy) continue;
            s_current = candidate;
            return candidate;
        }

        return null;
    }

    // ============================================================
    // Configuration
    // ============================================================

    [SerializeField] private float _dragThreshold = 4f;
    /// <summary>Pixels the pointer must move after a press before it counts as a drag.</summary>
    public float DragThreshold { get => _dragThreshold; set => _dragThreshold = value; }

    [SerializeField] private float _multiClickWindow = 0.4f;
    /// <summary>Seconds within which a repeat click on the same target increments the click streak.</summary>
    public float MultiClickWindow { get => _multiClickWindow; set => _multiClickWindow = value; }

    [SerializeField] private KeyCode _submitKey = KeyCode.Enter;
    public KeyCode SubmitKey { get => _submitKey; set => _submitKey = value; }

    [SerializeField] private KeyCode _cancelKey = KeyCode.Escape;
    public KeyCode CancelKey { get => _cancelKey; set => _cancelKey = value; }

    [SerializeField] private int _gamepadIndex;
    /// <summary>Connected gamepad slot used for UI navigation, submit and cancel.</summary>
    public int GamepadIndex { get => _gamepadIndex; set => _gamepadIndex = Math.Max(0, value); }

    [SerializeField] private GamepadButton _submitGamepadButton = GamepadButton.A;
    /// <summary>Gamepad button that submits the selected control.</summary>
    public GamepadButton SubmitGamepadButton { get => _submitGamepadButton; set => _submitGamepadButton = value; }

    [SerializeField] private GamepadButton _cancelGamepadButton = GamepadButton.B;
    /// <summary>Gamepad button that cancels the selected control and clears focus when it is unhandled.</summary>
    public GamepadButton CancelGamepadButton { get => _cancelGamepadButton; set => _cancelGamepadButton = value; }

    [SerializeField] private float _gamepadNavigationThreshold = 0.5f;
    /// <summary>Left-stick magnitude required to enter a UI navigation direction.</summary>
    public float GamepadNavigationThreshold
    {
        get => _gamepadNavigationThreshold;
        set => _gamepadNavigationThreshold = Maths.Clamp(value, 0.01f, 1f);
    }

    // ============================================================
    // Host viewport override
    // ============================================================

    public struct HostViewport
    {
        public Float2 ReferenceSize;
        public Float2 PointerPosition;
        public bool ReceivesInput;
    }

    /// <summary>
    /// The active host viewport, or <c>null</c> when running standalone (no editor). Hosts (the editor
    /// game view) write this every frame so input is sourced from a render-target region instead of the
    /// OS window. A host that stops being active sets <see cref="HostViewport.ReceivesInput"/> to false.
    /// </summary>
    public HostViewport? Viewport { get; set; }

    // ============================================================
    // State
    // ============================================================

    [SerializeIgnore] private GameObject? _hovered;
    /// <summary>The GameObject currently under the pointer (top-most UI hit). Null when none.</summary>
    public GameObject? Hovered => _hovered;

    [SerializeIgnore] private GameObject? _selected;
    /// <summary>The focused element - receives keyboard navigation, submit and cancel. See <see cref="SetSelected"/>.</summary>
    public GameObject? Selected => _selected;

    [SerializeIgnore] private Float2 _pointerPosition;
    /// <summary>Pointer position in window pixels (top-left origin, +Y down).</summary>
    public Float2 PointerPosition => _pointerPosition;

    [SerializeIgnore] private readonly PointerEventData _left = new() { Button = MouseButton.Left };
    [SerializeIgnore] private readonly PointerEventData _right = new() { Button = MouseButton.Right };
    [SerializeIgnore] private readonly PointerEventData _middle = new() { Button = MouseButton.Middle };
    /// <summary>The pointer event data for the left mouse button - usually what handlers read.</summary>
    public PointerEventData Left => _left;

    [SerializeIgnore] private GameObject? _lastHovered;
    [SerializeIgnore] private Float2 _lastPointerPos;
    // Pointer ids supplied by Android/HarmonyOS are not tied to touch-array indexes. Keep their
    // state keyed by id so compaction after one finger ends cannot steal another finger's drag.
    [SerializeIgnore] private readonly Dictionary<int, PointerEventData> _touchPointers = new();
    [SerializeIgnore] private readonly Dictionary<int, GameObject?> _lastTouchHovered = new();
    [SerializeIgnore] private MoveDirection _heldGamepadMoveDirection;
    [SerializeIgnore] private bool _hasHeldGamepadMoveDirection;
    [SerializeIgnore] private IEventSystemInputModule? _currentInputModule;

    /// <summary>The package or project input module currently driving this event system, if any.</summary>
    public IEventSystemInputModule? CurrentInputModule => _currentInputModule;

    // ============================================================
    // Lifecycle
    // ============================================================

    public override void OnEnable()
    {
        // First enabled instance wins; any extras stay inert so the tick only runs once per frame.
        RegisterLive(this);
        if (s_current is null || s_current.IsDisposed || !s_current.EnabledInHierarchy)
            s_current = this;
        _currentInputModule = null;
    }

    public override void OnDisable()
    {
        if (_currentInputModule != null)
        {
            try { _currentInputModule.DeactivateModule(this); }
            catch (Exception ex) { Debug.LogError($"[EventSystem] input module deactivation failed: {ex.Message}"); }
            _currentInputModule = null;
        }
        UnregisterLive(this);
        if (ReferenceEquals(s_current, this))
        {
            ClearState(); // drop focus/hover so nothing dangles once input stops (e.g. leaving play mode)
            s_current = FindEnabledReplacement();
        }
    }

    public override void OnRemovedFromScene()
    {
        UnregisterLive(this);
        if (ReferenceEquals(s_current, this))
        {
            ClearState();
            s_current = FindEnabledReplacement();
        }
    }

    public override void Update()
    {
        // Reclaim the role if the active system went away, so a surviving instance keeps input alive.
        if (s_current is null || s_current.IsDisposed) s_current = this;
        if (!ReferenceEquals(s_current, this)) return;

        IEventSystemInputModule? module = _currentInputModule;
        if (module == null || !module.IsModuleSupported || !module.ShouldActivateModule)
        {
            module = ResolveInputModule();
            if (!ReferenceEquals(module, _currentInputModule))
                SwitchInputModule(module);
        }

        if (module != null)
        {
            try { module.Process(this); }
            catch (Exception ex) { Debug.LogError($"[EventSystem] input module '{module.GetType().Name}' failed: {ex.Message}\n{ex.StackTrace}"); }
        }
        else
        {
            // Preserve the pre-module behavior for projects that do not install an input package.
            ProcessInput(EventSystemInputMode.All, Time.TimeSinceStartup);
        }
    }

    // Clear the static reference on script hot-reload so it doesn't pin a disposed component (and its
    // collectible AssemblyLoadContext).
    [OnAssemblyUnload]
    public static void ResetStatics()
    {
        s_current = null;
        s_systems.Clear();
    }

    /// <summary>
    /// Re-registers enabled event systems in a scene that stayed live across assembly reload.
    /// Promotes the first enabled instance when <see cref="Current"/> is empty.
    /// </summary>
    internal static void Rebind(Scene scene)
    {
        EventSystem? first = null;
        foreach (EventSystem candidate in scene.FindObjectsOfType<EventSystem>())
        {
            if (candidate.IsDisposed || !candidate.EnabledInHierarchy) continue;
            RegisterLive(candidate);
            first ??= candidate;
        }

        if (s_current is null || s_current.IsDisposed || !s_current.EnabledInHierarchy)
            s_current = first;
    }

    private static void RegisterLive(EventSystem system)
    {
        for (int i = 0; i < s_systems.Count; i++)
            if (ReferenceEquals(s_systems[i], system)) return;
        s_systems.Add(system);
    }

    private static void UnregisterLive(EventSystem system)
    {
        for (int i = 0; i < s_systems.Count; i++)
        {
            if (!ReferenceEquals(s_systems[i], system)) continue;
            s_systems.RemoveAt(i);
            return;
        }
    }

    private static EventSystem? FindEnabledReplacement()
    {
        for (int i = 0; i < s_systems.Count; i++)
        {
            EventSystem candidate = s_systems[i];
            if (!candidate.IsDisposed && candidate.EnabledInHierarchy)
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Runs the built-in pointer/keyboard dispatcher for a selected stage. Package input modules use
    /// this as the host bridge; custom modules can provide their own device sampling and call only the
    /// stage they own. The method intentionally stays free of package types.
    /// </summary>
    public void ProcessInput(EventSystemInputMode mode, float currentTime)
    {
        if (mode == EventSystemInputMode.None) return;
        Tick(currentTime, mode);
    }

    private IEventSystemInputModule? ResolveInputModule()
    {
        IEventSystemInputModule? selected = null;
        foreach (MonoBehaviour component in GetComponents<MonoBehaviour>())
        {
            if (component is not IEventSystemInputModule candidate || !component.EnabledInHierarchy)
                continue;
            if (!candidate.IsModuleSupported || !candidate.ShouldActivateModule) continue;
            selected = candidate;
            break;
        }
        return selected;
    }

    private void SwitchInputModule(IEventSystemInputModule? next)
    {
        if (ReferenceEquals(next, _currentInputModule)) return;
        if (_currentInputModule != null)
        {
            try { _currentInputModule.DeactivateModule(this); }
            catch (Exception ex) { Debug.LogError($"[EventSystem] input module deactivation failed: {ex.Message}"); }
        }
        _currentInputModule = next;
        if (_currentInputModule != null)
        {
            try { _currentInputModule.ActivateModule(this); }
            catch (Exception ex) { Debug.LogError($"[EventSystem] input module activation failed: {ex.Message}"); }
        }
    }

    private void ClearState()
    {
        _hovered = null;
        _selected = null;
        _lastHovered = null;
        _left.Reset();
        _right.Reset();
        _middle.Reset();
        _touchPointers.Clear();
        _lastTouchHovered.Clear();
        _hasHeldGamepadMoveDirection = false;
    }

    // ============================================================
    // Selection
    // ============================================================

    /// <summary>
    /// Sets the focused element. Fires <see cref="IDeselectHandler"/> on the previous selection and
    /// <see cref="ISelectHandler"/> on the new one. Pass <c>null</c> to clear focus.
    /// </summary>
    public void SetSelected(GameObject? go)
    {
        if (ReferenceEquals(go, _selected)) return;

        if (_selected is { IsDisposed: false })
            ExecuteHierarchy<IDeselectHandler>(_selected, h => h.OnDeselect());

        _selected = go;

        if (go != null)
            ExecuteHierarchy<ISelectHandler>(go, h => h.OnSelect());
    }

    // ============================================================
    // Dispatch helpers (stateless)
    // ============================================================

    /// <summary>
    /// Fires <typeparamref name="TInterface"/> on the first component up the GameObject hierarchy (starting
    /// from <paramref name="root"/>) that implements it. Returns the consuming GameObject, or <c>null</c>.
    /// </summary>
    public static GameObject? ExecuteHierarchy<TInterface>(GameObject? root, Action<TInterface> action)
        where TInterface : class
    {
        if (root == null || action == null) return null;

        GameObject? node = root;
        while (node != null)
        {
            foreach (MonoBehaviour comp in node.GetComponents<MonoBehaviour>())
            {
                if (comp is TInterface handler && comp.EnabledInHierarchy)
                {
                    try { action(handler); }
                    catch (Exception ex) { Debug.LogError($"[EventSystem] {typeof(TInterface).Name} threw on {comp.Name}: {ex.Message}\n{ex.StackTrace}"); }
                    return node;
                }
            }
            node = node.Parent;
        }
        return null;
    }

    /// <summary>
    /// Bubbling variant: dispatches to every handler up the hierarchy until one sets
    /// <see cref="PointerEventData.Used"/>. Used for pointer events that may want to be observed by
    /// ancestors regardless of whether a leaf handled them.
    /// </summary>
    private static GameObject? Bubble<TInterface>(GameObject? root, PointerEventData e, Action<TInterface, PointerEventData> action)
        where TInterface : class
    {
        if (root == null || action == null) return null;
        e.Used = false;

        GameObject? node = root;
        GameObject? first = null;
        while (node != null)
        {
            foreach (MonoBehaviour comp in node.GetComponents<MonoBehaviour>())
            {
                if (comp is TInterface handler && comp.EnabledInHierarchy)
                {
                    first ??= node;
                    try { action(handler, e); }
                    catch (Exception ex) { Debug.LogError($"[EventSystem] {typeof(TInterface).Name} threw on {comp.Name}: {ex.Message}\n{ex.StackTrace}"); }
                    if (e.Used) return first;
                }
            }
            node = node.Parent;
        }
        return first;
    }

    /// <summary>Fires <typeparamref name="TInterface"/> on every enabled handler on a SINGLE node (no
    /// hierarchy walk). Used for the pointer enter/exit chain, which visits each node explicitly.</summary>
    private static void DispatchNode<TInterface>(GameObject node, PointerEventData e, Action<TInterface, PointerEventData> action)
        where TInterface : class
    {
        foreach (MonoBehaviour comp in node.GetComponents<MonoBehaviour>())
        {
            if (comp is not TInterface handler || !comp.EnabledInHierarchy) continue;
            try { action(handler, e); }
            catch (Exception ex) { Debug.LogError($"[EventSystem] {typeof(TInterface).Name} threw on {comp.Name}: {ex.Message}\n{ex.StackTrace}"); }
        }
    }

    /// <summary>True if <paramref name="node"/> is <paramref name="of"/> or one of its ancestors. Null
    /// <paramref name="of"/> is never contained.</summary>
    private static bool IsAncestorOrSelf(GameObject node, GameObject? of)
    {
        for (GameObject? c = of; c != null; c = c.Parent)
            if (ReferenceEquals(c, node)) return true;
        return false;
    }

    /// <summary>Walks up from <paramref name="from"/> and returns the GameObject that owns the first
    /// <typeparamref name="T"/> component encountered, or null.</summary>
    private static GameObject? FindAncestor<T>(GameObject from) where T : MonoBehaviour
    {
        GameObject? node = from;
        while (node != null)
        {
            if (node.GetComponent<T>() != null) return node;
            node = node.Parent;
        }
        return null;
    }

    private static void FillCommon(PointerEventData e, GameObject? hovered, GameCanvas? canvas, Float2 pos, Float2 delta, Float2 designPos)
    {
        e.PreviousPosition = e.Position;
        e.Position = pos;
        e.Delta = delta;
        e.DesignPosition = designPos;
        e.Hovered = hovered;
        e.HitCanvas = canvas;
        e.Used = false;
    }

    // ============================================================
    // Frame tick
    // ============================================================

    private void Tick(float currentTime, EventSystemInputMode mode)
    {
        Scene? scene = Scene.Current;

        bool processPointer = (mode & EventSystemInputMode.Pointer) != 0;
        bool processKeyboard = (mode & EventSystemInputMode.Keyboard) != 0;

        if (!processPointer)
        {
            ProcessKeyboardInput(processKeyboard);
            return;
        }

        // Headless players and Runtime tests have no native window to source a mouse
        // position from. Navigation, submit and cancel are still valid in that mode.
        if (Viewport == null && Window.InternalWindow == null)
        {
            ProcessKeyboardInput(processKeyboard);
            return;
        }

        // Source the pointer from the active host viewport when present; otherwise the OS window + mouse.
        Float2 winSize;
        Float2 pos;
        bool gated; // when true, suppress all hits this frame (panel not focused)
        if (Viewport is { } vp)
        {
            winSize = vp.ReferenceSize;
            pos = vp.PointerPosition;
            gated = !vp.ReceivesInput;
        }
        else
        {
            winSize = new(Window.InternalWindow.Size.X, Window.InternalWindow.Size.Y);
            Int2 mp = Input.MousePosition;
            pos = new(mp.X, mp.Y);
            gated = false;
        }

        // Publish the pointer exactly as this frame's UI pass sees it (editor game-view viewports
        // included), so gameplay input that needs design-space coordinates (virtual pads) can share
        // the same mapping via UIRaycaster.ScreenToDesign instead of re-deriving raw mouse math.
        LastPointerWindow = pos;
        LastWindowSize = winSize;

        // The game-view override must cover every pointer in this frame, not only the mouse. This
        // makes touch hit testing agree with the size the UI pass used for a render-texture viewport.
        Float2? previousOverride = GameCanvas.ScreenSizeOverride;
        if (Viewport != null) GameCanvas.ScreenSizeOverride = winSize;
        try
        {
            if (Input.Current is ITouchInputHandler touchInput && touchInput.TouchCount > 0)
            {
                // Android mirrors the primary finger into its mouse state. Once a touch handler is
                // active, consuming mouse too would double-dispatch the same press/click.
                ProcessTouchInput(scene, touchInput, winSize, gated, currentTime);
            }
            else
            {
                ProcessMouseInput(scene, pos, winSize, gated, currentTime);
            }
        }
        finally
        {
            if (Viewport != null) GameCanvas.ScreenSizeOverride = previousOverride;
        }

        ProcessKeyboardInput(processKeyboard);
    }

    private void ProcessMouseInput(Scene? scene, Float2 pos, Float2 winSize, bool gated, float currentTime)
    {
        Float2 delta = pos - _lastPointerPos;
        _lastPointerPos = pos;
        _pointerPosition = pos;

        TryPick(scene, pos, winSize, gated, out GameObject? hovered, out GameCanvas? hoveredCanvas, out Float2 designPos);
        _hovered = hovered;

        _left.PointerId = -1;
        UpdateHover(_left, _lastHovered, hovered, hoveredCanvas, pos, delta, designPos);
        _lastHovered = hovered;

        UpdatePointer(_left, hovered, hoveredCanvas, pos, delta, designPos, winSize, currentTime,
            Input.GetMouseButtonDown(0), Input.GetMouseButtonUp(0), Input.GetMouseButton(0), canceled: false,
            selectOnPress: true);
        _right.PointerId = -2;
        UpdatePointer(_right, hovered, hoveredCanvas, pos, delta, designPos, winSize, currentTime,
            Input.GetMouseButtonDown(1), Input.GetMouseButtonUp(1), Input.GetMouseButton(1), canceled: false,
            selectOnPress: false);
        _middle.PointerId = -3;
        UpdatePointer(_middle, hovered, hoveredCanvas, pos, delta, designPos, winSize, currentTime,
            Input.GetMouseButtonDown(2), Input.GetMouseButtonUp(2), Input.GetMouseButton(2), canceled: false,
            selectOnPress: false);

        float scroll = Input.MouseWheelDelta;
        if (Maths.Abs(scroll) > 0.0001f && hovered != null)
        {
            FillCommon(_left, hovered, hoveredCanvas, pos, delta, designPos);
            _left.ScrollDelta = scroll;
            Bubble<IScrollHandler>(hovered, _left, static (h, e) => h.OnScroll(e));
            _left.ScrollDelta = 0f;
        }
    }

    private void ProcessTouchInput(Scene? scene, ITouchInputHandler touchInput, Float2 winSize, bool gated,
        float currentTime)
    {
        int touchCount = touchInput.TouchCount;
        for (int index = 0; index < touchCount; index++)
        {
            TouchPoint touch = touchInput.GetTouch(index);
            if (!_touchPointers.TryGetValue(touch.PointerId, out PointerEventData? pointer))
            {
                // A module may become active while a finger is already down. It is safe to track its
                // hover state, but do not synthesize a late press that could produce a false click.
                if (touch.Phase != TouchPhase.Began) continue;
                // A newly observed press has no previous sample. Seed both positions so the
                // PointerDown/Enter handlers correctly receive a zero delta instead of a jump
                // from the screen origin.
                pointer = new PointerEventData
                {
                    Button = MouseButton.Left,
                    PointerId = touch.PointerId,
                    Position = touch.Position,
                    PreviousPosition = touch.Position,
                };
                _touchPointers.Add(touch.PointerId, pointer);
                _lastTouchHovered.Add(touch.PointerId, null);
            }

            Float2 pos = touch.Position;
            Float2 delta = pos - pointer.Position;
            TryPick(scene, pos, winSize, gated, out GameObject? hovered, out GameCanvas? hoveredCanvas, out Float2 designPos);
            if (index == 0)
            {
                _pointerPosition = pos;
                _hovered = hovered;
            }

            _lastTouchHovered.TryGetValue(touch.PointerId, out GameObject? lastHovered);
            UpdateHover(pointer, lastHovered, hovered, hoveredCanvas, pos, delta, designPos);
            _lastTouchHovered[touch.PointerId] = hovered;

            bool canceled = touch.Phase == TouchPhase.Canceled;
            bool released = canceled || touch.Phase == TouchPhase.Ended;
            UpdatePointer(pointer, hovered, hoveredCanvas, pos, delta, designPos, winSize, currentTime,
                touch.Phase == TouchPhase.Began, released, !released, canceled, selectOnPress: true);

            if (!released) continue;

            // An end/cancel has no next frame ownership. Send its exit transition now and then
            // discard all state keyed by this physical pointer id.
            UpdateHover(pointer, hovered, null, null, pos, Float2.Zero, Float2.Zero);
            _lastTouchHovered.Remove(touch.PointerId);
            _touchPointers.Remove(touch.PointerId);
            if (index == 0) _hovered = null;
        }
    }

    private static void TryPick(Scene? scene, Float2 pos, Float2 winSize, bool gated, out GameObject? hovered,
        out GameCanvas? canvas, out Float2 designPos)
    {
        if (!gated && UIRaycaster.TryPick(scene, pos, winSize, out UIRaycaster.Hit hit))
        {
            hovered = hit.GameObject;
            canvas = hit.Canvas;
            designPos = hit.DesignPosition;
            return;
        }

        hovered = null;
        canvas = null;
        designPos = Float2.Zero;
    }

    private static void UpdateHover(PointerEventData pointer, GameObject? lastHovered, GameObject? hovered,
        GameCanvas? canvas, Float2 pos, Float2 delta, Float2 designPos)
    {
        if (ReferenceEquals(hovered, lastHovered)) return;
        GameObject? oldHover = lastHovered is { IsDisposed: false } ? lastHovered : null;
        FillCommon(pointer, hovered, canvas, pos, delta, designPos);

        for (GameObject? node = oldHover; node != null && !IsAncestorOrSelf(node, hovered); node = node.Parent)
            DispatchNode<IPointerExitHandler>(node, pointer, static (handler, e) => handler.OnPointerExit(e));
        for (GameObject? node = hovered; node != null && !IsAncestorOrSelf(node, oldHover); node = node.Parent)
            DispatchNode<IPointerEnterHandler>(node, pointer, static (handler, e) => handler.OnPointerEnter(e));
    }

    private void ProcessKeyboardInput(bool enabled)
    {
        if (!enabled || _selected is not { IsDisposed: false }) return;
        if (Input.GetKeyDown(_submitKey) || IsGamepadButtonDown(_submitGamepadButton))
            ExecuteHierarchy<ISubmitHandler>(_selected, h => h.OnSubmit());
        if (Input.GetKeyDown(_cancelKey) || IsGamepadButtonDown(_cancelGamepadButton))
        {
            // A modal control (for example XGUI Dropdown) owns its cancel lifecycle and may restore
            // focus to an element outside the current selected item's hierarchy. Only clear focus
            // when no handler accepted the event; otherwise the EventSystem would immediately undo
            // that restoration after the handler returns.
            if (ExecuteHierarchy<ICancelHandler>(_selected, h => h.OnCancel()) == null)
                SetSelected(null);
            return;
        }

        if (Input.GetKeyDown(KeyCode.Left))
        {
            DispatchMove(MoveDirection.Left);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Right))
        {
            DispatchMove(MoveDirection.Right);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Up))
        {
            DispatchMove(MoveDirection.Up);
            return;
        }
        if (Input.GetKeyDown(KeyCode.Down))
        {
            DispatchMove(MoveDirection.Down);
            return;
        }

        if (TryGetGamepadDPadMove(out MoveDirection dpadDirection))
        {
            _hasHeldGamepadMoveDirection = false;
            DispatchMove(dpadDirection);
            return;
        }

        if (!TryGetGamepadStickMove(out MoveDirection stickDirection))
        {
            _hasHeldGamepadMoveDirection = false;
            return;
        }

        if (_hasHeldGamepadMoveDirection && _heldGamepadMoveDirection == stickDirection)
            return;

        _heldGamepadMoveDirection = stickDirection;
        _hasHeldGamepadMoveDirection = true;
        DispatchMove(stickDirection);
    }

    private bool IsGamepadButtonDown(GamepadButton button)
    {
        return button != GamepadButton.Unknown &&
               Input.IsGamepadConnected(_gamepadIndex) &&
               Input.GetGamepadButtonDown(button, _gamepadIndex);
    }

    private bool TryGetGamepadDPadMove(out MoveDirection direction)
    {
        if (IsGamepadButtonDown(GamepadButton.DPadLeft))
        {
            direction = MoveDirection.Left;
            return true;
        }
        if (IsGamepadButtonDown(GamepadButton.DPadRight))
        {
            direction = MoveDirection.Right;
            return true;
        }
        if (IsGamepadButtonDown(GamepadButton.DPadUp))
        {
            direction = MoveDirection.Up;
            return true;
        }
        if (IsGamepadButtonDown(GamepadButton.DPadDown))
        {
            direction = MoveDirection.Down;
            return true;
        }

        direction = default;
        return false;
    }

    private bool TryGetGamepadStickMove(out MoveDirection direction)
    {
        if (!Input.IsGamepadConnected(_gamepadIndex))
        {
            direction = default;
            return false;
        }

        Float2 stick = Input.GetGamepadLeftStick(_gamepadIndex);
        float absX = Maths.Abs(stick.X);
        float absY = Maths.Abs(stick.Y);
        if (Maths.Max(absX, absY) < _gamepadNavigationThreshold)
        {
            direction = default;
            return false;
        }

        if (absX >= absY)
            direction = stick.X < 0f ? MoveDirection.Left : MoveDirection.Right;
        else
            direction = stick.Y < 0f ? MoveDirection.Down : MoveDirection.Up;
        return true;
    }

    private void DispatchMove(MoveDirection dir)
    {
        if (_selected == null) return;
        ExecuteHierarchy<IMoveHandler>(_selected, h => h.OnMove(dir));
    }

    private void UpdatePointer(
        PointerEventData e,
        GameObject? hovered,
        GameCanvas? hoveredCanvas,
        Float2 pos,
        Float2 delta,
        Float2 designPos,
        Float2 winSize,
        float currentTime,
        bool down,
        bool up,
        bool held,
        bool canceled,
        bool selectOnPress)
    {
        FillCommon(e, hovered, hoveredCanvas, pos, delta, designPos);

        // When the pointer leaves every raycast target mid-interaction the hit-test yields no design
        // position (it falls back to zero). Recover it from the active drag/press target's canvas so
        // drags keep tracking the pointer instead of snapping the value to the canvas origin.
        if (hovered == null)
        {
            GameObject? tracked = e.Dragging ?? e.PressedOn;
            GameCanvas? canvas = tracked?.GetComponentInParent<GameCanvas>(includeSelf: true);
            if (canvas != null && UIRaycaster.TryProjectPointer(canvas, pos, winSize, out Float2 dp))
                e.DesignPosition = dp;
        }

        // ---- Press ----
        if (down)
        {
            e.PressedOn = hovered;
            e.PressPosition = pos;
            e.PressTime = currentTime;
            e.IsDragging = false;
            e.Dragging = null;

            if (hovered != null &&
                currentTime - e.LastClickTime <= _multiClickWindow &&
                ReferenceEquals(hovered, e.LastClickTarget))
                e.ClickCount++;
            else
                e.ClickCount = 1;

            if (hovered != null)
            {
                Bubble<IPointerDownHandler>(hovered, e, static (h, ev) => h.OnPointerDown(ev));

                // Auto-focus a Selectable on press so subsequent keyboard input goes to that widget.
                // Non-Selectable presses don't change focus.
                if (selectOnPress)
                    SetSelected(FindAncestor<Selectable>(hovered));
            }
        }

        // ---- Drag detection / continuation ----
        if (held && e.PressedOn != null)
        {
            if (!e.IsDragging)
            {
                Float2 fromPress = pos - e.PressPosition;
                float distSqr = fromPress.X * fromPress.X + fromPress.Y * fromPress.Y;
                if (distSqr >= _dragThreshold * _dragThreshold)
                {
                    e.IsDragging = true;
                    e.Dragging = e.PressedOn;
                    Bubble<IBeginDragHandler>(e.Dragging, e, static (h, ev) => h.OnBeginDrag(ev));
                }
            }
            else
            {
                // Send drag every frame while the button is held *and* the pointer moved.
                if (delta.X != 0f || delta.Y != 0f)
                    Bubble<IDragHandler>(e.Dragging, e, static (h, ev) => h.OnDrag(ev));
            }
        }

        // ---- Release ----
        if (up)
        {
            // PointerUp goes to the element that received PointerDown, wherever the pointer is now, so a
            // widget released off-target doesn't get stranded in its Pressed state.
            if (e.PressedOn != null)
                Bubble<IPointerUpHandler>(e.PressedOn, e, static (h, ev) => h.OnPointerUp(ev));

            if (e.IsDragging && e.Dragging != null)
            {
                Bubble<IEndDragHandler>(e.Dragging, e, static (h, ev) => h.OnEndDrag(ev));
                if (!canceled && hovered != null && !ReferenceEquals(hovered, e.Dragging))
                    Bubble<IDropHandler>(hovered, e, static (h, ev) => h.OnDrop(ev));
            }
            else if (!canceled && e.PressedOn != null && ReferenceEquals(e.PressedOn, hovered))
            {
                // Click: down + up on the same target without a drag.
                e.LastClickTime = currentTime;
                e.LastClickTarget = hovered;
                Bubble<IPointerClickHandler>(hovered, e, static (h, ev) => h.OnPointerClick(ev));
            }

            e.PressedOn = null;
            e.Dragging = null;
            e.IsDragging = false;
        }
    }
}
