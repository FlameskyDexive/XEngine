// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// Lays a parsed screen out as a live XGUI hierarchy.
/// <para>
/// Every tag maps onto components that already ship in <c>com.xengine.xgui</c>; the composite controls
/// (Slider, InputField, ScrollList) build the same sub-hierarchies and wiring as the package's own
/// <c>*.xgui-template.json</c> so a document-built control and a menu-created one behave identically.
/// </para>
/// <para>
/// The builder never throws on a bad attribute value. A screen that is 90% right should render 90%
/// right with the mistakes reported by lint, because an agent iterating on a layout learns far more
/// from a screenshot than from an exception.
/// </para>
/// </summary>
public static class XuiScreenBuilder
{
    private static readonly Color DefaultPanelColor = new(0.20f, 0.20f, 0.24f, 1f);
    private static readonly Color DefaultAccentColor = new(0.38f, 0.55f, 0.95f, 1f);
    private static readonly Color DefaultTextColor = new(0.90f, 0.90f, 0.92f, 1f);
    private static readonly Color DefaultPlaceholderColor = new(0.50f, 0.50f, 0.55f, 1f);
    private static readonly Color DefaultHandleColor = new(0.90f, 0.90f, 0.94f, 1f);

    public static XuiScreenInstance Build(ScreenDef screen, XuiBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        options ??= new XuiBuildOptions();

        var byId = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        GameObject root = CreateCanvas(screen, options);

        for (int i = 0; i < screen.Root.Children.Count; i++)
            BuildElement(screen.Root.Children[i], root, options, byId, parentTag: null);

        // EngineObject overloads == / != for destroyed-object semantics, so a plain null check asks the
        // wrong question. The pattern covers null and disposed in one go and binds a non-null result.
        WireTabBars(root, byId);

        // One ticker per screen drives every animator under it, so a screen that animates nothing costs
        // nothing and a torn-down screen cannot leave a ticker running.
        if (HasAnimator(root)) root.AddComponent<XuiAnimationTicker>();

        if (options.Scene is { IsDisposed: false } scene)
            scene.Add(root);

        return new XuiScreenInstance(screen.Name, root, screen.Layer, byId);
    }

    // ------------------------------------------------------------------
    //  Canvas
    // ------------------------------------------------------------------

    private static GameObject CreateCanvas(ScreenDef screen, XuiBuildOptions options)
    {
        var go = new GameObject(screen.Name);
        go.EnsureRectTransform();

        GameCanvas canvas = go.AddComponent<GameCanvas>();
        // GameCanvas has two modes, not three: camera-space is overlay plus a RenderCamera, which the
        // app assigns because the document has no way to name a scene object.
        canvas.RenderMode = screen.Canvas == XuiCanvasMode.World
            ? RenderMode.WorldSpace
            : RenderMode.ScreenSpaceOverlay;
        canvas.SortOrder = options.SortOrder;
        // Pixel mode exists for pixel art, where a fractional scale factor is the whole problem.
        canvas.PixelPerfect = screen.ScaleMode == XuiScaleMode.Pixel;

        CanvasScaler scaler = go.AddComponent<CanvasScaler>();
        if (screen.Reference != null)
        {
            scaler.UIScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.ReferenceResolution = new Float2(
                screen.Reference.Value.Width ?? 1920f,
                screen.Reference.Value.Height ?? 1080f);
        }
        else
        {
            scaler.UIScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        }

        if (options.Parent is { IsDisposed: false } layerRoot)
            go.SetParent(layerRoot);

        return go;
    }

    // ------------------------------------------------------------------
    //  Elements
    // ------------------------------------------------------------------

    private static void BuildElement(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo? parentTag)
    {
        XuiTagInfo? tag = XuiTagLibrary.Find(node.Tag);
        if (tag == null)
        {
            // An unknown tag is a lint error. Building the subtree as a plain container keeps the rest
            // of the screen intact so the screenshot still shows what is wrong and where.
            BuildContainer(node, parent, options, byId, tag: null);
            return;
        }

        switch (node.Tag)
        {
            case "Frame": BuildContainer(node, parent, options, byId, tag); break;
            case "Image": BuildImage(node, parent, options, byId, tag); break;
            case "RawImage": BuildRawImage(node, parent, options, byId, tag); break;
            case "Text": BuildText(node, parent, options, byId, tag); break;
            case "VStack": BuildStack(node, parent, options, byId, tag, parentTag, vertical: true); break;
            case "HStack": BuildStack(node, parent, options, byId, tag, parentTag, vertical: false); break;
            case "Grid": BuildGrid(node, parent, options, byId, tag, parentTag); break;
            case "Btn": BuildButton(node, parent, options, byId, tag); break;
            case "Toggle": BuildToggle(node, parent, options, byId, tag); break;
            case "Slider": BuildSlider(node, parent, options, byId, tag); break;
            case "InputField": BuildInputField(node, parent, options, byId, tag); break;
            case "ScrollList": BuildScrollList(node, parent, options, byId, tag); break;
            case "SafeArea": BuildSafeArea(node, parent, options, byId, tag); break;
            case "Show": BuildShow(node, parent, options, byId, tag); break;
            case "Progress": BuildProgress(node, parent, options, byId, tag); break;
            case "TabBar": BuildTabBar(node, parent, options, byId, tag, parentTag); break;
            case "Tab": BuildTab(node, parent, options, byId, tag); break;
            case "Anim": ApplyAnim(node, parent); break;
            default: BuildContainer(node, parent, options, byId, tag); break;
        }
    }

    private static GameObject CreateNode(
        ElementNode node,
        GameObject parent,
        Dictionary<string, GameObject> byId,
        XuiTagInfo? tag)
    {
        string? id = node.Id;
        var go = new GameObject(id ?? node.Tag);
        go.SetParent(parent);
        RectTransform rect = go.EnsureRectTransform();
        XuiRectApplier.Apply(rect, node);

        ApplyAuthoredSizeOverride(go, node);

        string? translate = node.Attribute("translate");
        if (translate != null && XuiValueParser.TryParseBool(translate, out bool translatable) && !translatable)
            go.AddComponent<XuiNoTranslate>();

        string? hidden = node.Attribute("hidden");
        if (hidden != null && XuiValueParser.TryParseBool(hidden, out bool isHidden) && isHidden)
            go.Enabled = false;

        if (id != null)
        {
            // A duplicate id is rejected by the parser inside one body and by lint after template
            // expansion; keeping the first here means the index still matches the generated binding.
            byId.TryAdd(id, go);
        }

        _ = tag;
        return go;
    }

    /// <summary>
    /// Publishes an authored <c>size</c> as a preferred size, so a parent layout container honours it
    /// instead of whatever the element measures itself as. See <see cref="XuiSizeOverride"/> for why
    /// this is not optional.
    /// </summary>
    private static void ApplyAuthoredSizeOverride(GameObject go, ElementNode node)
    {
        string? sizeText = node.Attribute("size");
        if (sizeText == null || !XuiValueParser.TryParseSize(sizeText, out XuiSize size)) return;
        if (size.Width == null && size.Height == null) return;

        XuiSizeOverride element = go.AddComponent<XuiSizeOverride>();
        element.Width = size.Width ?? -1f;
        element.Height = size.Height ?? -1f;
    }

    private static void BuildChildren(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo? parentTag = null)
    {
        for (int i = 0; i < node.Children.Count; i++)
            BuildElement(node.Children[i], parent, options, byId, parentTag);
    }

    /// <summary>
    /// Sizes a container to its content on any axis the document left as <c>_</c> when nothing above
    /// it decides that axis.
    /// </summary>
    /// <remarks>
    /// <c>_</c> means "leave this axis to the layout". Inside another container that is unambiguous:
    /// the parent decides. At the top of a screen there is no parent to decide, and the axis would
    /// otherwise keep the RectTransform's arbitrary 100px default — so a panel written as
    /// <c>size="480x_"</c> would come out 480x100 regardless of what is in it. Sizing to content is
    /// the only reading of <c>_</c> that matches what the author wrote.
    /// </remarks>
    private static void ApplyContentFit(GameObject go, ElementNode node, XuiTagInfo? parentTag)
    {
        if (parentTag is { IsLayoutGroup: true }) return;

        bool fitWidth;
        bool fitHeight;
        string? sizeText = node.Attribute("size");
        if (sizeText == null || !XuiValueParser.TryParseSize(sizeText, out XuiSize size))
        {
            // No authored size at all: fit both, rather than sit at the 100x100 default.
            fitWidth = true;
            fitHeight = true;
        }
        else
        {
            fitWidth = size.Width == null;
            fitHeight = size.Height == null;
        }

        if (!fitWidth && !fitHeight) return;

        XuiContentFitter fitter = go.AddComponent<XuiContentFitter>();
        fitter.FitWidth = fitWidth;
        fitter.FitHeight = fitHeight;
    }

    private static void BuildContainer(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo? tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        ApplyMask(node, go);
        BuildChildren(node, go, options, byId);
    }

    private static void BuildImage(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        Image image = go.AddComponent<Image>();
        ApplyGraphic(node, image, options, Color.White);
        ApplySprite(node, image, options);
        ApplyMask(node, go);
        BuildChildren(node, go, options, byId);
    }

    private static void BuildRawImage(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        RawImage image = go.AddComponent<RawImage>();
        ApplyGraphic(node, image, options, Color.White);
        ApplyMask(node, go);
        BuildChildren(node, go, options, byId);
    }

    private static void BuildText(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        Text text = go.AddComponent<Text>();
        ApplyText(node, text, options, node.Text ?? string.Empty, DefaultTextColor);
        Localize(go, node, node.Text, XuiTextSlot.Label, options);
    }

    private static void BuildStack(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag,
        XuiTagInfo? parentTag,
        bool vertical)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        ApplyOptionalBackground(node, go, options);

        XuiStack group = go.AddComponent<XuiStack>();
        group.Vertical = vertical;

        string? spacing = node.Attribute("spacing");
        if (spacing != null && XuiValueParser.TryParseFloat(spacing, out float spacingValue))
            group.Spacing = spacingValue;

        string? stretch = node.Attribute("stretch-children");
        if (stretch != null && XuiValueParser.TryParseBool(stretch, out bool stretchCross))
            group.StretchCross = stretchCross;

        string? expand = node.Attribute("expand");
        if (expand != null && XuiValueParser.TryParseBool(expand, out bool expandAlong))
            group.ExpandAlong = expandAlong;

        if (TryReadPadding(node, out int top, out int right, out int bottom, out int left))
        {
            group.PaddingTop = top;
            group.PaddingRight = right;
            group.PaddingBottom = bottom;
            group.PaddingLeft = left;
        }
        if (TryReadChildAlignment(node, out TextAlignment alignment))
            group.ChildAlignment = alignment;

        ApplyMask(node, go);
        BuildChildren(node, go, options, byId, tag);
        ApplyContentFit(go, node, parentTag);
    }

    private static void BuildGrid(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag,
        XuiTagInfo? parentTag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        ApplyOptionalBackground(node, go, options);

        XuiGrid group = go.AddComponent<XuiGrid>();

        string? cell = node.Attribute("cell");
        if (cell != null && XuiValueParser.TryParseSize(cell, out XuiSize cellSize))
            group.CellSize = new Float2(cellSize.Width ?? 100f, cellSize.Height ?? 100f);

        string? spacing = node.Attribute("spacing");
        if (spacing != null && XuiValueParser.TryParseFloat2(spacing, out XuiFloat2 spacing2))
            group.Spacing = new Float2(spacing2.X, spacing2.Y);
        else if (spacing != null && XuiValueParser.TryParseFloat(spacing, out float uniform))
            group.Spacing = new Float2(uniform, uniform);

        string? columns = node.Attribute("columns");
        if (columns != null && XuiValueParser.TryParseInt(columns, out int columnCount))
            group.Columns = columnCount;

        if (TryReadPadding(node, out int top, out int right, out int bottom, out int left))
        {
            group.PaddingTop = top;
            group.PaddingRight = right;
            group.PaddingBottom = bottom;
            group.PaddingLeft = left;
        }
        if (TryReadChildAlignment(node, out TextAlignment alignment))
            group.ChildAlignment = alignment;

        ApplyMask(node, go);
        BuildChildren(node, go, options, byId, tag);
        ApplyContentFit(go, node, parentTag);
    }

    private static void BuildButton(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        Image background = go.AddComponent<Image>();
        Color authored = ApplyGraphic(node, background, options, DefaultPanelColor);
        ApplySprite(node, background, options);

        Button button = go.AddComponent<Button>();
        button.TargetGraphicComponent = background;
        ApplySelectableTint(button, background, authored);
        ApplyInteractable(node, button);

        // Inline text becomes a stretched label child, matching the package's Button template. Its id
        // is derived so a generated binding can reach the label without the author naming it.
        if (!string.IsNullOrEmpty(node.Text))
            CreateLabel(node, go, options, byId, node.Text!);

        BuildChildren(node, go, options, byId);
    }

    private static void BuildToggle(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        Image background = go.AddComponent<Image>();
        Color authored = ApplyGraphic(node, background, options, DefaultPanelColor);
        ApplySprite(node, background, options);

        Toggle toggle = go.AddComponent<Toggle>();
        toggle.TargetGraphicComponent = background;
        ApplySelectableTint(toggle, background, authored);
        ApplyInteractable(node, toggle);

        string? isOn = node.Attribute("on");
        if (isOn != null && XuiValueParser.TryParseBool(isOn, out bool on))
            toggle.IsOn = on;

        if (!string.IsNullOrEmpty(node.Text))
            CreateLabel(node, go, options, byId, node.Text!);

        BuildChildren(node, go, options, byId);
    }

    private static void BuildSlider(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        Image background = go.AddComponent<Image>();
        Color authored = ApplyGraphic(node, background, options, DefaultPanelColor);

        GameObject fillGo = CreateChild(go, "Fill");
        RectTransform fillRect = fillGo.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(fillRect, XuiAnchor.Stretch);
        Image fill = fillGo.AddComponent<Image>();
        fill.Color = ResolveColor(node, "fill-color", options, DefaultAccentColor);
        fill.RaycastTarget = false;

        GameObject handleGo = CreateChild(go, "Handle");
        RectTransform handleRect = handleGo.EnsureRectTransform();
        handleRect.SizeDelta = new Float2(20f, 0f);
        Image handle = handleGo.AddComponent<Image>();
        handle.Color = DefaultHandleColor;
        handle.RaycastTarget = false;

        Slider slider = go.AddComponent<Slider>();
        slider.TargetGraphicComponent = background;
        slider.FillRect = fillRect;
        slider.HandleRect = handleRect;
        ApplySelectableTint(slider, background, authored);
        ApplyInteractable(node, slider);

        string? min = node.Attribute("min");
        if (min != null && XuiValueParser.TryParseFloat(min, out float minValue)) slider.MinValue = minValue;
        string? max = node.Attribute("max");
        if (max != null && XuiValueParser.TryParseFloat(max, out float maxValue)) slider.MaxValue = maxValue;
        string? whole = node.Attribute("whole-numbers");
        if (whole != null && XuiValueParser.TryParseBool(whole, out bool wholeNumbers)) slider.WholeNumbers = wholeNumbers;
        string? value = node.Attribute("value");
        if (value != null && XuiValueParser.TryParseFloat(value, out float sliderValue)) slider.Value = sliderValue;
    }

    private static void BuildInputField(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        Image background = go.AddComponent<Image>();
        Color authored = ApplyGraphic(node, background, options, new Color(0.12f, 0.12f, 0.15f, 1f));
        ApplySprite(node, background, options);

        GameObject areaGo = CreateChild(go, "Text Area");
        RectTransform areaRect = areaGo.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(areaRect, XuiAnchor.Stretch);
        areaRect.SizeDelta = new Float2(-16f, -8f);
        areaGo.AddComponent<RectMask2D>();

        GameObject placeholderGo = CreateChild(areaGo, "Placeholder");
        XuiRectApplier.ApplyAnchor(placeholderGo.EnsureRectTransform(), XuiAnchor.Stretch);
        Text placeholder = placeholderGo.AddComponent<Text>();
        placeholder.TextValue = node.Attribute("placeholder") ?? string.Empty;
        placeholder.Alignment = TextAlignment.CenterLeft;
        placeholder.TextColor = DefaultPlaceholderColor;

        GameObject textGo = CreateChild(areaGo, "Text");
        XuiRectApplier.ApplyAnchor(textGo.EnsureRectTransform(), XuiAnchor.Stretch);
        Text text = textGo.AddComponent<Text>();
        text.Alignment = TextAlignment.CenterLeft;
        text.TextColor = DefaultTextColor;
        ApplyFontSize(node, text);

        GameObject selectionGo = CreateChild(areaGo, "Selection");
        RectTransform selectionRect = selectionGo.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(selectionRect, XuiAnchor.LeftStretch);
        selectionRect.SizeDelta = new Float2(0f, -4f);
        Image selection = selectionGo.AddComponent<Image>();
        selection.RaycastTarget = false;

        GameObject caretGo = CreateChild(areaGo, "Caret");
        RectTransform caretRect = caretGo.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(caretRect, XuiAnchor.LeftStretch);
        caretRect.SizeDelta = new Float2(1.5f, -4f);
        Image caret = caretGo.AddComponent<Image>();
        caret.RaycastTarget = false;

        InputField input = go.AddComponent<InputField>();
        input.TargetGraphicComponent = background;
        input.TextArea = areaRect;
        input.Placeholder = placeholder;
        input.TextComponent = text;
        input.Selection = selectionRect;
        input.Caret = caretRect;
        ApplySelectableTint(input, background, authored);
        ApplyInteractable(node, input);

        string? limit = node.Attribute("character-limit");
        if (limit != null && XuiValueParser.TryParseInt(limit, out int characterLimit))
            input.CharacterLimit = characterLimit;
        string? password = node.Attribute("password");
        if (password != null && XuiValueParser.TryParseBool(password, out bool isPassword))
            input.IsPassword = isPassword;
        string? readOnly = node.Attribute("readonly");
        if (readOnly != null && XuiValueParser.TryParseBool(readOnly, out bool isReadOnly))
            input.ReadOnly = isReadOnly;
        string? initial = node.Attribute("text");
        if (initial != null)
            input.Text = initial;

        // Both live on the InputField's own object rather than on the child that displays them: the
        // slot says which one to write, and a controller looking for the field finds one object.
        Localize(go, node, node.Attribute("placeholder"), XuiTextSlot.Placeholder, options);
        Localize(go, node, initial, XuiTextSlot.Value, options);
    }

    private static void BuildScrollList(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        ApplyOptionalBackground(node, go, options);

        GameObject viewportGo = CreateChild(go, "Viewport");
        RectTransform viewportRect = viewportGo.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(viewportRect, XuiAnchor.Stretch);
        viewportGo.AddComponent<RectMask2D>();

        GameObject contentGo = CreateChild(viewportGo, "Content");
        RectTransform contentRect = contentGo.EnsureRectTransform();
        contentRect.AnchorMin = new Float2(0f, 1f);
        contentRect.AnchorMax = new Float2(1f, 1f);
        contentRect.Pivot = new Float2(0.5f, 1f);
        contentRect.SizeDelta = new Float2(0f, 0f);
        contentRect.AnchoredPosition = Float2.Zero;

        // A scroll list arranges its rows itself; without this the content rect would be an empty
        // container and every row would stack at the origin.
        XuiStack contentLayout = contentGo.AddComponent<XuiStack>();
        contentLayout.Vertical = true;
        string? spacing = node.Attribute("spacing");
        if (spacing != null && XuiValueParser.TryParseFloat(spacing, out float spacingValue))
            contentLayout.Spacing = spacingValue;
        contentGo.AddComponent<XuiContentFitter>().FitHeight = true;

        ScrollRect scroll = go.AddComponent<ScrollRect>();
        scroll.Viewport = viewportRect;
        scroll.Content = contentRect;

        string? horizontal = node.Attribute("horizontal");
        scroll.Horizontal = horizontal != null && XuiValueParser.TryParseBool(horizontal, out bool h) && h;
        string? vertical = node.Attribute("vertical");
        scroll.Vertical = vertical == null || !XuiValueParser.TryParseBool(vertical, out bool v) || v;

        // Authored children are rows, so they belong under Content, not under the ScrollRect itself.
        BuildChildren(node, contentGo, options, byId);
    }

    /// <summary>
    /// Attaches a transition to the element the <c>&lt;Anim&gt;</c> sits inside.
    /// </summary>
    /// <remarks>
    /// It produces no node of its own: several transitions on one element are several tracks on one
    /// animator, not a stack of empty children that would each take part in layout.
    /// </remarks>
    private static void ApplyAnim(ElementNode node, GameObject owner)
    {
        XuiAnimator animator = owner.GetComponent<XuiAnimator>() ?? owner.AddComponent<XuiAnimator>();

        var track = new XuiAnimTrack { Trigger = node.Attribute("on") ?? "show" };

        string? move = node.Attribute("move");
        if (move != null && XuiValueParser.TryParseTweenOffset(move, out XuiFloat2 moveFrom, out XuiFloat2 moveTo))
        {
            track.MoveFrom = new Float2(moveFrom.X, moveFrom.Y);
            track.MoveTo = new Float2(moveTo.X, moveTo.Y);
            track.AnimatesMove = true;
        }

        string? scale = node.Attribute("scale");
        if (scale != null && XuiValueParser.TryParseTweenScale(scale, out XuiFloat2 scaleFrom, out XuiFloat2 scaleTo))
        {
            track.ScaleFrom = new Float2(scaleFrom.X, scaleFrom.Y);
            track.ScaleTo = new Float2(scaleTo.X, scaleTo.Y);
            track.AnimatesScale = true;
        }

        string? fade = node.Attribute("fade");
        if (fade != null && XuiValueParser.TryParseTweenAlpha(fade, out float fadeFrom, out float fadeTo))
        {
            track.FadeFrom = Maths.Clamp(fadeFrom, 0f, 1f);
            track.FadeTo = Maths.Clamp(fadeTo, 0f, 1f);
            track.AnimatesFade = true;
        }

        string? duration = node.Attribute("duration");
        if (duration != null && XuiValueParser.TryParseFloat(duration, out float seconds))
            track.Duration = Maths.Max(0f, seconds);

        string? delay = node.Attribute("delay");
        if (delay != null && XuiValueParser.TryParseFloat(delay, out float delaySeconds))
            track.Delay = Maths.Max(0f, delaySeconds);

        string? ease = node.Attribute("ease");
        if (ease != null && XuiValueParser.TryParseEase(ease, out XuiEase parsedEase))
            track.Ease = parsedEase;

        animator.AddTrack(track);
    }

    private static void BuildSafeArea(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        // A safe area always fills its parent; the fitter then insets it. Letting a document anchor it
        // some other way would silently defeat the point of the tag.
        XuiRectApplier.ApplyAnchor(go.EnsureRectTransform(), XuiAnchor.Stretch);

        SafeAreaFitter fitter = go.AddComponent<SafeAreaFitter>();
        string? conformX = node.Attribute("conform-x");
        if (conformX != null && XuiValueParser.TryParseBool(conformX, out bool x)) fitter.ConformX = x;
        string? conformY = node.Attribute("conform-y");
        if (conformY != null && XuiValueParser.TryParseBool(conformY, out bool y)) fitter.ConformY = y;

        ApplyMask(node, go);
        BuildChildren(node, go, options, byId);
    }

    private static void BuildShow(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        XuiShowGroup group = go.AddComponent<XuiShowGroup>();

        ApplyMask(node, go);
        BuildChildren(node, go, options, byId);

        // Applied after the children exist, so hiding a group does not stop it being built — a
        // controller that shows it later gets a complete subtree rather than an empty one.
        string? visible = node.Attribute("visible");
        group.Visible = visible == null || !XuiValueParser.TryParseBool(visible, out bool shown) || shown;
    }

    private static void BuildProgress(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        Image track = go.AddComponent<Image>();
        ApplyGraphic(node, track, options, DefaultPanelColor);
        ApplySprite(node, track, options);

        GameObject fillGo = CreateChild(go, "Fill");
        RectTransform fillRect = fillGo.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(fillRect, XuiAnchor.Stretch);
        Image fill = fillGo.AddComponent<Image>();
        fill.Color = ResolveColor(node, "fill-color", options, DefaultAccentColor);
        fill.RaycastTarget = false;
        fill.Sprite = new AssetRef<Sprite>(XguiDefaultResources.ResolveDefaultSpriteGuid());

        XuiProgress progress = go.AddComponent<XuiProgress>();
        progress.FillRect = fillRect;

        string? direction = node.Attribute("direction");
        if (direction != null && XuiValueParser.TryParseProgressDirection(direction, out XuiProgressFill fillDirection))
            progress.Direction = (XuiProgressDirection)fillDirection;

        string? value = node.Attribute("value");
        if (value != null && XuiValueParser.TryParseFloat(value, out float parsed)) progress.Value = parsed;
    }

    private static void BuildTabBar(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag,
        XuiTagInfo? parentTag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);
        ApplyOptionalBackground(node, go, options);

        // A tab bar is a row, so it arranges like an HStack and gains a bar on top.
        XuiStack row = go.AddComponent<XuiStack>();
        row.Vertical = false;

        string? spacing = node.Attribute("spacing");
        if (spacing != null && XuiValueParser.TryParseFloat(spacing, out float spacingValue))
            row.Spacing = spacingValue;

        string? expand = node.Attribute("expand");
        row.ExpandAlong = expand == null || !XuiValueParser.TryParseBool(expand, out bool expandAlong) || expandAlong;

        if (TryReadPadding(node, out int top, out int right, out int bottom, out int left))
        {
            row.PaddingTop = top;
            row.PaddingRight = right;
            row.PaddingBottom = bottom;
            row.PaddingLeft = left;
        }
        if (TryReadChildAlignment(node, out TextAlignment alignment)) row.ChildAlignment = alignment;

        XuiTabBar bar = go.AddComponent<XuiTabBar>();

        ApplyMask(node, go);
        BuildChildren(node, go, options, byId, tag);
        ApplyContentFit(go, node, parentTag);

        string? selected = node.Attribute("selected");
        if (selected != null && XuiValueParser.TryParseInt(selected, out int index))
            bar.Select(index, notify: false);
    }

    private static void BuildTab(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        XuiTagInfo tag)
    {
        GameObject go = CreateNode(node, parent, byId, tag);

        Image background = go.AddComponent<Image>();
        Color authored = ApplyGraphic(node, background, options, DefaultPanelColor);
        ApplySprite(node, background, options);

        Button button = go.AddComponent<Button>();
        button.TargetGraphicComponent = background;
        ApplySelectableTint(button, background, authored);
        ApplyInteractable(node, button);

        XuiTab tab = go.AddComponent<XuiTab>();
        tab.BoundId = node.Attribute("bind") ?? string.Empty;

        XuiTabBar? bar = parent.GetComponent<XuiTabBar>();
        if (bar is { IsDisposed: false })
        {
            bar.AddTab(tab);
            int index = bar.Tabs.Count - 1;
            button.OnClick += () => bar.Select(index, notify: true);
        }

        if (!string.IsNullOrEmpty(node.Text)) CreateLabel(node, go, options, byId, node.Text!);
        BuildChildren(node, go, options, byId);
    }

    private static bool HasAnimator(GameObject node)
    {
        if (node.GetComponent<XuiAnimator>() is { IsDisposed: false }) return true;
        IReadOnlyList<GameObject> children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (HasAnimator(children[i])) return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves every tab's <c>bind</c> once the screen is complete, then applies the initial selection.
    /// </summary>
    /// <remarks>
    /// This cannot happen while a bar is being built: a tab may bind a panel the document declares
    /// further down, which does not exist yet. Finding the bars by walking the finished tree — rather
    /// than collecting them in a field as they are built — keeps the builder free of state that would
    /// outlive one call and quietly leak between screens.
    /// </remarks>
    private static void WireTabBars(GameObject node, Dictionary<string, GameObject> byId)
    {
        if (node.GetComponent<XuiTabBar>() is { IsDisposed: false } bar)
        {
            for (int t = 0; t < bar.Tabs.Count; t++)
            {
                XuiTab tab = bar.Tabs[t];
                if (tab.BoundId.Length == 0) continue;
                if (byId.TryGetValue(tab.BoundId, out GameObject? target)) tab.Target = target;
            }
            bar.Refresh();
        }

        IReadOnlyList<GameObject> children = node.Children;
        for (int i = 0; i < children.Count; i++) WireTabBars(children[i], byId);
    }

    // ------------------------------------------------------------------
    //  Shared appliers
    // ------------------------------------------------------------------

    private static GameObject CreateChild(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.SetParent(parent);
        go.EnsureRectTransform();
        return go;
    }

    private static void CreateLabel(
        ElementNode node,
        GameObject parent,
        XuiBuildOptions options,
        Dictionary<string, GameObject> byId,
        string content)
    {
        string? ownerId = node.Id;
        var go = new GameObject(ownerId == null ? "Label" : ownerId + "Label");
        go.SetParent(parent);
        RectTransform rect = go.EnsureRectTransform();
        XuiRectApplier.ApplyAnchor(rect, XuiAnchor.Stretch);
        rect.SizeDelta = new Float2(-16f, -8f);

        Text text = go.AddComponent<Text>();
        text.TextValue = content;
        text.Alignment = TextAlignment.CenterMiddle;
        text.TextColor = ResolveColor(node, "text-color", options, DefaultTextColor);
        ApplyFontSize(node, text);
        ApplyFont(node, text, options);
        Localize(go, node, content, XuiTextSlot.Label, options);

        if (ownerId != null)
            byId.TryAdd(ownerId + "Label", go);
    }

    /// <summary>Applies the authored colour and returns it, because a Selectable added afterwards
    /// repaints the graphic and reading it back then would return the control's tint, not the
    /// document's colour.</summary>
    private static Color ApplyGraphic(ElementNode node, Graphic graphic, XuiBuildOptions options, Color fallback)
    {
        Color color = ResolveColor(node, "color", options, fallback);
        graphic.Color = color;

        string? raycast = node.Attribute("raycast");
        if (raycast != null && XuiValueParser.TryParseBool(raycast, out bool raycastTarget))
            graphic.RaycastTarget = raycastTarget;

        return color;
    }

    /// <summary>
    /// Translates a string as the screen is built, and records where it came from.
    /// </summary>
    /// <remarks>
    /// The record is what makes a locale switch cheap: the string is re-translated in place instead of
    /// the screen being rebuilt, so scroll position, focus, and anything a controller wrote after the
    /// build survive. Nothing is attached when there is nothing to translate — an untranslated screen
    /// carries no extra components — and <c>translate="false"</c> anywhere up the tree opts out.
    /// </remarks>
    private static void Localize(
        GameObject target, ElementNode node, string? source, XuiTextSlot slot, XuiBuildOptions options)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        if (!options.Localize) return;
        if (!IsTranslatable(target, node)) return;

        string trimmed = slot == XuiTextSlot.Label ? source!.Trim() : source!;
        var localized = target.AddComponent<XuiLocalizedText>();
        localized.Source = trimmed;
        localized.Slot = slot;
        localized.Apply();
    }

    /// <summary>
    /// <c>translate</c> is inherited, so a container opts out everything inside it.
    /// </summary>
    /// <remarks>
    /// The inherited state is read from the hierarchy rather than threaded through every build method:
    /// the opt-out is a marker component the container already carries, which also keeps it true after
    /// a hot reload and visible to anyone inspecting the live object.
    /// </remarks>
    private static bool IsTranslatable(GameObject target, ElementNode node)
    {
        string? own = node.Attribute("translate");
        if (own != null && XuiValueParser.TryParseBool(own, out bool value)) return value;

        for (GameObject? current = target; current is { IsDisposed: false }; current = current.Parent)
        {
            if (current.GetComponent<XuiNoTranslate>() is { IsDisposed: false }) return false;
        }
        return true;
    }

    private static void ApplyText(
        ElementNode node,
        Text text,
        XuiBuildOptions options,
        string content,
        Color fallback)
    {
        text.TextValue = content;
        text.TextColor = ResolveColor(node, "color", options, fallback);

        string? align = node.Attribute("align");
        if (align != null && XuiValueParser.TryParseTextAlign(align, out XuiTextAlign parsed))
            text.Alignment = ToEngineAlignment(parsed);

        string? rich = node.Attribute("rich");
        if (rich != null && XuiValueParser.TryParseBool(rich, out bool richText))
            text.RichTextEnabled = richText;

        ApplyFontSize(node, text);
        ApplyFont(node, text, options);
    }

    private static void ApplyFontSize(ElementNode node, TextComponent text)
    {
        string? fontSize = node.Attribute("font-size");
        if (fontSize != null && XuiValueParser.TryParseInt(fontSize, out int size))
            text.Size = size;
    }

    private static void ApplyFont(ElementNode node, TextComponent text, XuiBuildOptions options)
    {
        string? font = node.Attribute("font");
        if (font != null && options.Assets.TryResolveFont(font, out AssetRef<FontAsset> resolved))
            text.Font = resolved;
    }

    private static void ApplySprite(ElementNode node, Image image, XuiBuildOptions options)
    {
        string? sprite = node.Attribute("sprite");
        bool assigned = false;
        if (sprite != null && sprite.Length > 0
            && options.Assets.TryResolveSprite(sprite, out AssetRef<Sprite> resolved))
        {
            image.Sprite = resolved;
            assigned = true;
        }

        // An Image with no sprite draws nothing at all — its material has no texture to sample, so a
        // panel or button authored with only a colour comes out invisible. XGUI ships a default sprite
        // for exactly this and its own creation menu assigns it; a document-built Image has to as well,
        // or every screen renders as floating text over the skybox.
        // sprite="none" is the explicit opt-out, for a rect that really should draw nothing.
        if (!assigned && !string.Equals(sprite, "none", StringComparison.Ordinal))
            image.Sprite = new AssetRef<Sprite>(XguiDefaultResources.ResolveDefaultSpriteGuid());

        string? type = node.Attribute("type");
        if (type == null || !XuiValueParser.TryParseImageFit(type, out XuiImageFit fit)) return;

        switch (fit)
        {
            case XuiImageFit.Simple: image.Type = ImageType.Simple; break;
            case XuiImageFit.Sliced: image.Type = ImageType.Sliced; break;
            case XuiImageFit.Tiled: image.Type = ImageType.Tiled; break;
            case XuiImageFit.Filled: image.Type = ImageType.Filled; break;
            case XuiImageFit.Contain:
                image.Type = ImageType.Simple;
                image.PreserveAspect = true;
                break;
            default: break;
        }
    }

    /// <summary>A layout container only gets an Image when the author actually asked for a look.</summary>
    private static void ApplyOptionalBackground(ElementNode node, GameObject go, XuiBuildOptions options)
    {
        if (!node.HasAttribute("color") && !node.HasAttribute("sprite")) return;

        Image image = go.AddComponent<Image>();
        ApplyGraphic(node, image, options, DefaultPanelColor);
        ApplySprite(node, image, options);
    }

    private static void ApplyMask(ElementNode node, GameObject go)
    {
        string? mask = node.Attribute("mask");
        if (string.Equals(mask, "rect", StringComparison.Ordinal))
            go.AddComponent<RectMask2D>();
    }

    /// <summary>
    /// Reads <c>padding</c> without naming the shared <c>LayoutGroup</c> base type. That base name is
    /// declared by both <c>XEngine.Runtime</c> and <c>XEngine.XGUI.Runtime</c>, and a package that
    /// references both cannot spell it (CS0433) — so the values come out here and each caller writes
    /// them through its own concrete group.
    /// </summary>
    private static bool TryReadPadding(ElementNode node, out int top, out int right, out int bottom, out int left)
    {
        top = right = bottom = left = 0;
        string? padding = node.Attribute("padding");
        if (padding == null) return false;
        if (!XuiValueParser.TryParseInsets(padding, out float t, out float r, out float b, out float l))
            return false;

        top = (int)t;
        right = (int)r;
        bottom = (int)b;
        left = (int)l;
        return true;
    }

    private static bool TryReadChildAlignment(ElementNode node, out TextAlignment alignment)
    {
        alignment = TextAlignment.TopLeft;
        string? align = node.Attribute("align");
        if (align == null || !XuiValueParser.TryParseTextAlign(align, out XuiTextAlign parsed)) return false;
        alignment = ToEngineAlignment(parsed);
        return true;
    }

    private static void ApplyInteractable(ElementNode node, Selectable selectable)
    {
        string? interactable = node.Attribute("interactable");
        if (interactable != null && XuiValueParser.TryParseBool(interactable, out bool value))
            selectable.Interactable = value;
    }

    /// <summary>
    /// Derives a control's interaction tints from the colour the document gave it.
    /// </summary>
    /// <remarks>
    /// <see cref="Selectable"/> uses a colour-tint transition whose normal state defaults to white, and
    /// it writes that onto its target graphic as soon as it enables. Setting only
    /// <see cref="Graphic.Color"/> therefore does nothing visible: every button, toggle, slider, and
    /// input field comes out white regardless of what the document said. The authored colour has to
    /// become the <em>normal</em> state, with the other states derived from it.
    /// </remarks>
    /// <summary>
    /// Derives a control's interaction tints from the colour the document gave it, and repaints it.
    /// </summary>
    /// <remarks>
    /// <see cref="Selectable"/> tints its target graphic, and its normal state defaults to white — so
    /// it repaints over the authored colour the moment it is added. Two things are needed to undo
    /// that. The states have to be derived from the authored colour, so hover and press stay in the
    /// same family. And the graphic has to be repainted <b>after</b> the control exists: assigning
    /// <c>NormalColor</c> alone changes nothing, because Selectable only repaints when the selection
    /// state changes and it was already Normal.
    /// </remarks>
    private static void ApplySelectableTint(Selectable selectable, Graphic target, in Color baseColor)
    {
        selectable.NormalColor = baseColor;
        selectable.HighlightedColor = Scale(baseColor, 1.15f);
        selectable.SelectedColor = Scale(baseColor, 1.08f);
        selectable.PressedColor = Scale(baseColor, 0.82f);
        selectable.DisabledColor = new Color(baseColor.R, baseColor.G, baseColor.B, baseColor.A * 0.4f);

        // The last word on what the control looks like at rest. Every later repaint comes from the
        // states above, which are all derived from this colour.
        target.Color = baseColor;
    }

    private static Color Scale(in Color color, float factor) => new(
        Maths.Clamp(color.R * factor, 0f, 1f),
        Maths.Clamp(color.G * factor, 0f, 1f),
        Maths.Clamp(color.B * factor, 0f, 1f),
        color.A);

    private static Color ResolveColor(ElementNode node, string attribute, XuiBuildOptions options, Color fallback)
    {
        string? text = node.Attribute(attribute);
        return text == null ? fallback : options.Palette.Resolve(text, fallback);
    }

    /// <summary>
    /// The document's <c>align</c> is horizontal; XGUI's <see cref="TextAlignment"/> is a flags enum
    /// covering both axes. Vertical centring is the sane default for UI text, and <c>justify</c> has no
    /// XGUI equivalent, so it degrades to left rather than silently centring.
    /// </summary>
    private static TextAlignment ToEngineAlignment(XuiTextAlign align) => align switch
    {
        XuiTextAlign.Center => TextAlignment.CenterMiddle,
        XuiTextAlign.Right => TextAlignment.CenterRight,
        _ => TextAlignment.CenterLeft,
    };

    internal static string Describe(ElementNode node)
        => node.Id ?? node.Tag + "@" + node.Span.Line.ToString(CultureInfo.InvariantCulture);
}
