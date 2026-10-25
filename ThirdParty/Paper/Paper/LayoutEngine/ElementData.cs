// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI.Events;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.PaperUI.LayoutEngine;

/// <summary>How leftover main-axis space is distributed across items on a wrapped line.</summary>
public enum WrapJustify
{
    /// <summary>Pack items at the start of each line (leftover space trails at the end).</summary>
    Start,
    /// <summary>Center items on each line.</summary>
    Center,
    /// <summary>Pack items at the end of each line.</summary>
    End,
    /// <summary>Distribute leftover space as equal gaps between items.</summary>
    SpaceBetween,
    /// <summary>Distribute leftover space as equal gaps around every item.</summary>
    SpaceAround,
    /// <summary>Grow every item on the line equally so the line consumes the full width.</summary>
    Fill,
}

/// <summary> Configuration data for a UI element, including its identifiers, interactivity flags, event callbacks, layout properties, and font settings. </summary>
public struct ElementData
{
    public int ID;
    public IPaperSubtreePainter? SubtreePainter;
    /// <summary>Optional exact revision for all custom paint dependencies on this element.
    /// Recreated each frame. Callers must change it for every callback-visible mutation.</summary>
    public ulong? CustomPaintVersion;

    // Events
    public bool IsFocusable;
    /// <summary>Opt-in focus loss on a left press outside this control (delayed text inputs).</summary>
    public bool BlurOnOutsideClick;
    /// <summary> When true, the element ignores all input events (mouse, keyboard, touch). </summary>
    public bool IsNotInteractable;
    /// <summary> When true, events on this element do not propagate to parent elements. </summary>
    public bool StopPropagation;

    /// <summary>Mouse cursor shape requested while this element is hovered. Defaults to
    /// <see cref="PaperCursor.Inherit"/> (take the nearest ancestor's, or the arrow at the root).</summary>
    public PaperCursor Cursor;

    /// <summary>Mouse cursor shape requested while this element is pressed/dragged. Defaults to
    /// <see cref="PaperCursor.Inherit"/>, which falls back to <see cref="Cursor"/> during a drag.</summary>
    public PaperCursor CursorDragging;

    // Event handlers
    public Action<ClickEvent> OnClick;
    public Action<ClickEvent> OnPress;
    public Action<ClickEvent> OnRelease;
    public Action<ClickEvent> OnDoubleClick;
    public Action<ClickEvent> OnRightClick;
    public Action<ClickEvent> OnHeld;

    public Action<DragEvent> OnDragStart;
    public Action<DragEvent> OnDragging;
    public Action<DragEvent> OnDragEnd;
    public Func<IInputEditSession>? DragEditSessionFactory;

    public Action<ScrollEvent> OnScroll;
    public Action<ElementEvent> OnHover;
    public Action<ElementEvent> OnEnter;
    public Action<ElementEvent> OnLeave;

    public Action<KeyEvent> OnKeyPressed;
    public Action<TextInputEvent> OnTextInput;
    public Action<FocusEvent> OnFocusChange;

    /// <summary> Called after this element's layout is computed, with the element handle and its final layout rectangle. </summary>
    public Action<ElementHandle, Rect> OnPostLayout;

    // Stateful (captured-value) bridges backing the On*<T>(captured, handler) fluent overloads.
    // See Events/StatefulHandlers.cs — pooled mutable closures; null unless such an overload was
    // used on this slot this frame. Drained back to their pools by ReturnBridges.
    internal IClickStateful? SfClick;
    internal IClickStateful? SfPress;
    internal IClickStateful? SfRelease;
    internal IClickStateful? SfDoubleClick;
    internal IClickStateful? SfRightClick;
    internal IClickStateful? SfHeld;
    internal IDragStateful? SfDragStart;
    internal IDragStateful? SfDragging;
    internal IDragStateful? SfDragEnd;
    internal IScrollStateful? SfScroll;
    internal IElementStateful? SfHover;
    internal IElementStateful? SfEnter;
    internal IElementStateful? SfLeave;
    internal IKeyStateful? SfKeyPressed;
    internal ITextStateful? SfTextInput;
    internal IFocusStateful? SfFocusChange;
    internal IPostLayoutStateful? SfPostLayout;

    /// <summary>Returns every rented stateful bridge of a dying slot to its pool and clears the
    /// fields. Called from CreateElement while the previous struct is still reachable.</summary>
    public static void ReturnBridges(ref ElementData data)
    {
        data.SfClick?.ReturnToPool(); data.SfClick = null;
        data.SfPress?.ReturnToPool(); data.SfPress = null;
        data.SfRelease?.ReturnToPool(); data.SfRelease = null;
        data.SfDoubleClick?.ReturnToPool(); data.SfDoubleClick = null;
        data.SfRightClick?.ReturnToPool(); data.SfRightClick = null;
        data.SfHeld?.ReturnToPool(); data.SfHeld = null;
        data.SfDragStart?.ReturnToPool(); data.SfDragStart = null;
        data.SfDragging?.ReturnToPool(); data.SfDragging = null;
        data.SfDragEnd?.ReturnToPool(); data.SfDragEnd = null;
        data.SfScroll?.ReturnToPool(); data.SfScroll = null;
        data.SfHover?.ReturnToPool(); data.SfHover = null;
        data.SfEnter?.ReturnToPool(); data.SfEnter = null;
        data.SfLeave?.ReturnToPool(); data.SfLeave = null;
        data.SfKeyPressed?.ReturnToPool(); data.SfKeyPressed = null;
        data.SfTextInput?.ReturnToPool(); data.SfTextInput = null;
        data.SfFocusChange?.ReturnToPool(); data.SfFocusChange = null;
        data.SfPostLayout?.ReturnToPool(); data.SfPostLayout = null;
    }


    // Hierarchy
    /// <summary> Index of the parent element in the owner's element array, or -1 if this element is the root. </summary>
    public int ParentIndex;
    /// <summary> Indices of child elements in the owning element array. </summary>
    public List<int> ChildIndices;

    /// <summary> Whether this element inherits its parent's interaction state (mouse hover, press, etc.) instead of tracking its own. </summary>
    public bool IsHookedToParent;

    /// <summary> Whether this element has one or more children that are hooked to it. An optimization flag for interaction hooking. </summary>
    public bool IsAHookedParent;

    /// <summary>  Tab navigation - element's position in tab order (-1 means not focusable via tab) </summary>
    public int TabIndex;

    /// <summary> Whether the element is rendered and participates in layout. Defaults to true. </summary>
    public bool Visible;

    // Layout properties
    public LayoutType LayoutType;
    public PositionType PositionType;

    // Text properties
    public bool IsMarkdown;
    public bool IsRichText;
    public string Paragraph;
    public FontFile Font;
    public FontFile FontBold;
    public FontFile FontItalic;
    public FontFile FontBoldItalic;
    public FontFile FontMono;
    public FontStyle FontStyle;
    /// <summary> How text wraps within the element's bounds. Defaults to NoWrap. </summary>
    public TextWrapMode WrapMode;
    /// <summary>When true (single-line only), text wider than the element is cut and suffixed with
    /// an ellipsis that is guaranteed to fit.</summary>
    public bool Truncate;
    public TextAlignment TextAlignment;

    /// <summary>Flex-wrap: parent-directed children flow onto new lines when they overrun the main axis.</summary>
    public bool ContentWrap;
    /// <summary>How leftover main-axis space on each wrapped line is distributed.</summary>
    public WrapJustify WrapJustify;

    // Cached text layout objects (RichText is persisted across frames via element storage so animation start time survives -- see Paper.Core.cs ProcessText / DrawText paths.)
    internal Quill.Canvas.QuillMarkdown? _quillMarkdown;
    internal Quill.Canvas.QuillRichText? _quillRichText;
    internal TextLayout _textLayout;

    // Rendering
    internal List<ElementRenderCommand> _renderCommands;
    internal List<ElementRenderCommand> _foregroundRenderCommands;
    internal ElementStyle _elementStyle;
    internal bool _scissorEnabled;
    internal bool _clampToScreen;

    // Culling bounds: the element's whole-subtree extent in its own local (layout) space, grown
    // to cover box shadow and every descendant. _cullHasLayerBreakout is set when any descendant
    // sits on a higher layer and therefore escapes this element's clip. Recomputed each frame
    // after layout; RenderElement uses them to skip fully-clipped subtrees.
    internal float _cullMinX, _cullMinY, _cullMaxX, _cullMaxY;
    internal bool _cullHasLayerBreakout;

    /// <summary>
    /// Layer assignment. Defaults to <see cref="Layer.Base"/> (0). Higher values render later
    /// and are hit-tested first. Use <see cref="Layer.Overlay"/> / <see cref="Layer.Topmost"/>
    /// for the well-known tiers, or any custom <see cref="int"/> for in-between tiers.
    /// </summary>
    public int Layer;

    // Per-frame memo for ProcessText. Layout runs an element several times per frame during
    // stretch resolution, and DrawText calls ProcessText again at render; each pass otherwise
    // re-derives identical settings and repeats the layout-cache lookups. The first compute of
    // the frame fills these; later calls reuse the size while the width still applies.
    // ElementData is recreated every frame, so _textMemoValid starts false automatically.
    internal Float2 _textMemoSize;
    internal float _textMemoWidth;
    internal bool _textMemoWidthIndependent;
    internal bool _textMemoValid;

    // Layout results
    /// <summary> Whether text layout has been computed for this element this frame. Set to true by ProcessText so subsequent layout and render passes skip redundant work. </summary>
    public bool ProcessedText;
    /// <summary> The resolved X position of the top-left corner of this element's layout rectangle. </summary>
    public float X;
    /// <summary> The resolved Y position of the top-left corner of this element's layout rectangle. </summary>
    public float Y;
    /// <summary> The width of this element as computed by the layout engine, in the parent's coordinate space. </summary>
    public float LayoutWidth;
    /// <summary> The Height of this element as computed by the layout engine, in the parent's coordinate space </summary>
    public float LayoutHeight;
    /// <summary> The resolved X position of the top-left corner of this element's layout rectangle, in the parent's coordinate space. </summary>
    public float RelativeX;
    /// <summary> The resolved Y position of the top-left corner of this element's layout rectangle, in the parent's coordinate space. </summary>
    public float RelativeY;

    /// <summary> Callback that measures the element's content size given optional width and height constraints. Returns the measured (width, height) or null if the measurement is unavailable. </summary>
    public Func<float?, float?, (float, float)?> ContentSizer;

    /// <summary> Gets the bounding rectangle of this element in sceen (or root) cordinate space, derived from X, Y, LayoutWidth and LayoutHeight. </summary>
    public readonly Rect LayoutRect => new Rect(X, Y, X + LayoutWidth, Y + LayoutHeight);

    /// <summary>
    /// Creates a new ElementData with the given ID and all fields set to their default values.
    /// Reuses <paramref name="childIndices"/> when the slot already has a list (immediate-mode
    /// frames rebuild the tree in place); otherwise rents from <see cref="ChildIndexListPool"/>.
    /// The custom render-command lists follow the same reuse pattern via
    /// <see cref="RenderCommandListPool"/>, which also recycles the pooled command instances.
    /// </summary>
    // internal: command-list parameters expose an internal type; sole caller is Paper's storage.
    internal static ElementData Create(int id, List<int>? childIndices = null,
        List<ElementRenderCommand>? renderCommands = null,
        List<ElementRenderCommand>? foregroundRenderCommands = null)
    {
        List<int> kids = ChildIndexListPool.Rent(childIndices);        return new ElementData
        {
            ID = id,
            IsFocusable = true,
            IsNotInteractable = false,
            StopPropagation = false,
            ParentIndex = -1,
            ChildIndices = kids,
            IsHookedToParent = false,
            IsAHookedParent = false,
            TabIndex = -1,
            Visible = true,
            LayoutType = LayoutType.Column,
            PositionType = PositionType.ParentDirected,
            IsMarkdown = false,
            IsRichText = false,
            Paragraph = null,
            Font = null,
            FontStyle = FontStyle.Regular,
            WrapMode = TextWrapMode.NoWrap,
            Truncate = false,
            TextAlignment = TextAlignment.Left,
            ContentWrap = false,
            WrapJustify = WrapJustify.Start,
            _quillMarkdown = null,
            _quillRichText = null,
            _textLayout = null,
            // Null stays null: most elements never register custom draws, so the lists are
            // created lazily by Paper.Draw/DrawForeground via RenderCommandListPool.NewList.
            // Slots that DO have lists get them drained (commands recycled) and reused in place.
            _renderCommands = renderCommands is null ? null : RenderCommandListPool.Reuse(renderCommands),
            _foregroundRenderCommands = foregroundRenderCommands is null
                ? null
                : RenderCommandListPool.Reuse(foregroundRenderCommands),
            // Assigned from the persistent per-id style store by CreateElement instead of a fresh
            // throwaway allocation each frame (the old new ElementStyle() here was discarded by
            // UpdateStyles, which repoints this at the _activeStyles entry anyway).
            _elementStyle = null,
            _scissorEnabled = false,
            _clampToScreen = false,
            // Default to Layer.Base (0). Fully qualified because the RHS shadows the LHS
            // field name in an object initializer when the type is a static class.
            Layer = PaperUI.Layer.Base,
            ProcessedText = false,
        };
    }
}

/// <summary>
/// Reuses <see cref="List{T}"/> instances for <see cref="ElementData.ChildIndices"/> so
/// immediate-mode <c>BeginFrame</c> rebuilds do not allocate a new child list per element.
/// </summary>
internal static class ChildIndexListPool
{
    private const int MaxPooled = 2048;
    private static readonly Stack<List<int>> s_pool = new();

    public static List<int> Rent(List<int>? existing)
    {
        if (existing != null)
        {
            existing.Clear();
            return existing;
        }

        lock (s_pool)
        {
            if (s_pool.Count > 0)
            {
                List<int> rented = s_pool.Pop();
                rented.Clear();
                return rented;
            }
        }

        return new List<int>();
    }

    public static void Return(List<int>? list)
    {
        if (list == null) return;
        list.Clear();
        lock (s_pool)
        {
            if (s_pool.Count < MaxPooled)
                s_pool.Push(list);
        }
    }
}

/// <summary>
/// Reuses the <see cref="List{T}"/> instances backing <see cref="ElementData._renderCommands"/> /
/// <see cref="ElementData._foregroundRenderCommands"/>, and recycles the <see cref="ElementRenderCommand"/>
/// instances themselves when a reused slot's stale commands are drained at frame rebuild. One
/// overnight editor dump held 135k dead command-list arrays from the previous new-per-draw path.
/// </summary>
internal static class RenderCommandListPool
{
    private const int MaxPooledLists = 2048;
    private const int MaxPooledCommands = 4096;
    private static readonly Stack<List<ElementRenderCommand>> s_lists = new();
    private static readonly Stack<ElementRenderCommand> s_commands = new();

    public static List<ElementRenderCommand>? Reuse(List<ElementRenderCommand> existing)
    {
        // In-place reuse: recycle the stale command objects first, then clear for this frame.
        lock (s_commands)
        {
            foreach (var cmd in existing)
            {
                cmd.RenderAction = null;
                cmd.PaintKind = null;
                cmd.PaintVersion = 0;
                cmd.PaintDetail = 0;
                cmd.PaintSource = null;
                cmd.PaintDependency = null;
                if (s_commands.Count < MaxPooledCommands)
                    s_commands.Push(cmd);
            }
        }
        existing.Clear();
        return existing;
    }

    /// <summary>Lazily creates (or pops) a list at first <c>Draw</c> registration.</summary>
    public static List<ElementRenderCommand> NewList()
    {
        lock (s_lists)
        {
            if (s_lists.Count > 0)
            {
                List<ElementRenderCommand> rented = s_lists.Pop();
                rented.Clear();
                return rented;
            }
        }
        return new List<ElementRenderCommand>();
    }

    public static ElementRenderCommand RentCommand()
    {
        lock (s_commands)
        {
            if (s_commands.Count > 0)
                return s_commands.Pop();
        }
        return new ElementRenderCommand();
    }
}
