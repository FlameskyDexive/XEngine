// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>What a tag contributes to the assembled hierarchy.</summary>
public enum XuiTagKind
{
    /// <summary>Rect-only container; no drawable of its own.</summary>
    Container,

    /// <summary>Draws something (Image / RawImage / Text).</summary>
    Graphic,

    /// <summary>Arranges its children (VStack / HStack / Grid).</summary>
    LayoutGroup,

    /// <summary>Interactive control (Btn / Toggle / Slider / InputField / ScrollList).</summary>
    Control,

    /// <summary>
    /// Adds behaviour to its parent instead of producing a node of its own. <c>&lt;Anim&gt;</c> is one:
    /// several may sit on the same element, and none of them draws anything.
    /// </summary>
    Behaviour,
}

/// <summary>One entry in the built-in tag catalog.</summary>
public sealed class XuiTagInfo
{
    internal XuiTagInfo(
        string tag,
        XuiTagKind kind,
        string bindingType,
        string idPrefix,
        bool acceptsText,
        bool acceptsChildren,
        string[] attributes,
        bool buildsTextChild = false)
    {
        Tag = tag;
        Kind = kind;
        BindingType = bindingType;
        IdPrefix = idPrefix;
        AcceptsText = acceptsText;
        AcceptsChildren = acceptsChildren;
        OwnAttributes = attributes;
        BuildsTextChild = buildsTextChild;
    }

    /// <summary>
    /// True when inline text becomes a separate <c>Text</c> child (a button's label), rather than
    /// living on the element itself. Only these tags get a generated <c>{id}Label</c> binding — one on
    /// a <c>&lt;Text&gt;</c> would point at a child that is never built.
    /// </summary>
    public bool BuildsTextChild { get; }

    /// <summary>Attributes this tag accepts on top of <see cref="XuiTagLibrary.CommonAttributes"/>.</summary>
    public IReadOnlyList<string> OwnAttributes { get; }

    public bool Accepts(string attribute)
    {
        for (int i = 0; i < OwnAttributes.Count; i++)
        {
            if (string.Equals(OwnAttributes[i], attribute, StringComparison.Ordinal)) return true;
        }
        return XuiTagLibrary.IsCommonAttribute(attribute);
    }

    public string Tag { get; }
    public XuiTagKind Kind { get; }

    /// <summary>
    /// Simple name of the <c>XEngine.Runtime.UI</c> component the generated view binding returns for
    /// this tag. The scaffolder emits this verbatim, so it must stay in step with the XGUI package.
    /// </summary>
    public string BindingType { get; }

    /// <summary>
    /// Required <c>id</c> prefix (`X` + component abbreviation), from the XPlay XUI naming system.
    /// Enforcing it is what makes "node name ≡ binding path ≡ field name" mechanically checkable
    /// instead of a convention people forget.
    /// </summary>
    public string IdPrefix { get; }

    public bool AcceptsText { get; }
    public bool AcceptsChildren { get; }

    public bool IsLayoutGroup => Kind == XuiTagKind.LayoutGroup;
}

/// <summary>
/// The built-in tag catalog. Every tag maps one-to-one onto components that already ship in
/// <c>com.xengine.xgui</c> — XUI adds a language, not new drawables.
/// </summary>
public static class XuiTagLibrary
{
    /// <summary>
    /// Attributes every tag accepts: identity, rect placement, and the translation opt-out.
    /// </summary>
    /// <remarks>
    /// <c>translate</c> is common rather than text-tag-only because it covers a whole subtree: a panel
    /// of player-entered names or a debug overlay is one decision, not one per label.
    /// </remarks>
    private static readonly string[] Common =
    {
        "id", "anchor", "size", "pos", "pivot", "scale", "hidden", "translate",
    };

    private static readonly string[] MaskOnly = { "mask" };
    private static readonly string[] ImageAttributes = { "color", "sprite", "type", "raycast", "mask" };
    private static readonly string[] RawImageAttributes = { "color", "raycast", "mask" };
    private static readonly string[] TextAttributes = { "color", "font", "font-size", "align", "rich" };
    private static readonly string[] StackAttributes =
        { "color", "sprite", "spacing", "padding", "align", "mask", "stretch-children", "expand" };
    private static readonly string[] GridAttributes =
        { "color", "sprite", "cell", "columns", "spacing", "padding", "align", "mask" };
    private static readonly string[] ButtonAttributes =
        { "color", "sprite", "text-color", "font", "font-size", "interactable" };
    private static readonly string[] ToggleAttributes =
        { "color", "sprite", "text-color", "font", "font-size", "interactable", "on" };
    private static readonly string[] SliderAttributes =
        { "color", "fill-color", "interactable", "min", "max", "value", "whole-numbers" };
    private static readonly string[] InputAttributes =
    {
        "color", "sprite", "placeholder", "text", "font-size", "character-limit",
        "password", "readonly", "interactable",
    };
    private static readonly string[] ScrollAttributes =
        { "color", "sprite", "spacing", "horizontal", "vertical", "mask" };
    private static readonly string[] SafeAreaAttributes = { "conform-x", "conform-y", "mask" };
    private static readonly string[] AnimAttributes =
        { "on", "move", "scale", "fade", "duration", "delay", "ease" };
    private static readonly string[] ShowAttributes = { "visible", "mask" };
    private static readonly string[] ProgressAttributes =
        { "color", "sprite", "fill-color", "value", "direction" };
    private static readonly string[] TabBarAttributes =
        { "color", "sprite", "spacing", "padding", "align", "selected", "mask", "expand" };
    private static readonly string[] TabAttributes =
        { "color", "sprite", "text-color", "font", "font-size", "interactable", "bind" };

    private static readonly XuiTagInfo[] AllTags =
    {
        new("Frame",      XuiTagKind.Container,   "RectTransform", "XRect",   acceptsText: false, acceptsChildren: true,  MaskOnly),
        new("Image",      XuiTagKind.Graphic,     "Image",         "XImg",    acceptsText: false, acceptsChildren: true,  ImageAttributes),
        new("RawImage",   XuiTagKind.Graphic,     "RawImage",      "XRimg",   acceptsText: false, acceptsChildren: true,  RawImageAttributes),
        new("Text",       XuiTagKind.Graphic,     "Text",          "XTxt",    acceptsText: true,  acceptsChildren: false, TextAttributes),
        new("VStack",     XuiTagKind.LayoutGroup, "XuiStack",      "XVs",     acceptsText: false, acceptsChildren: true,  StackAttributes),
        new("HStack",     XuiTagKind.LayoutGroup, "XuiStack",      "XHs",     acceptsText: false, acceptsChildren: true,  StackAttributes),
        new("Grid",       XuiTagKind.LayoutGroup, "XuiGrid",       "XGrid",   acceptsText: false, acceptsChildren: true,  GridAttributes),
        new("Btn",        XuiTagKind.Control,     "Button",        "XBtn",    acceptsText: true,  acceptsChildren: true,  ButtonAttributes, buildsTextChild: true),
        new("Toggle",     XuiTagKind.Control,     "Toggle",        "XTog",    acceptsText: true,  acceptsChildren: true,  ToggleAttributes, buildsTextChild: true),
        new("Slider",     XuiTagKind.Control,     "Slider",        "XSlider", acceptsText: false, acceptsChildren: false, SliderAttributes),
        new("InputField", XuiTagKind.Control,     "InputField",    "XInput",  acceptsText: false, acceptsChildren: false, InputAttributes),
        new("ScrollList", XuiTagKind.Control,     "ScrollRect",    "XSv",     acceptsText: false, acceptsChildren: true,  ScrollAttributes),
        new("SafeArea",   XuiTagKind.Container,   "SafeAreaFitter","XSafe",   acceptsText: false, acceptsChildren: true,  SafeAreaAttributes),
        new("Show",       XuiTagKind.Container,   "XuiShowGroup",  "XShow",   acceptsText: false, acceptsChildren: true,  ShowAttributes),
        new("Progress",   XuiTagKind.Graphic,     "XuiProgress",   "XProg",   acceptsText: false, acceptsChildren: false, ProgressAttributes),
        new("TabBar",     XuiTagKind.LayoutGroup, "XuiTabBar",     "XTabs",   acceptsText: false, acceptsChildren: true,  TabBarAttributes),
        new("Tab",        XuiTagKind.Control,     "XuiTab",        "XTab",    acceptsText: true,  acceptsChildren: true,  TabAttributes, buildsTextChild: true),
        new("Anim",       XuiTagKind.Behaviour,   "XuiAnimator",   "XAnim",   acceptsText: false, acceptsChildren: false, AnimAttributes),
    };

    /// <summary>Attributes accepted on every tag.</summary>
    public static IReadOnlyList<string> CommonAttributes => Common;

    public static bool IsCommonAttribute(string attribute)
    {
        for (int i = 0; i < Common.Length; i++)
        {
            if (string.Equals(Common[i], attribute, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static readonly Dictionary<string, XuiTagInfo> ByTag = BuildIndex();

    private static Dictionary<string, XuiTagInfo> BuildIndex()
    {
        var map = new Dictionary<string, XuiTagInfo>(AllTags.Length, StringComparer.Ordinal);
        for (int i = 0; i < AllTags.Length; i++)
            map.Add(AllTags[i].Tag, AllTags[i]);
        return map;
    }

    /// <summary>Every built-in tag, in catalog order.</summary>
    public static IReadOnlyList<XuiTagInfo> Tags => AllTags;

    public static bool IsBuiltin(string tag) => ByTag.ContainsKey(tag);

    public static XuiTagInfo? Find(string tag)
        => ByTag.TryGetValue(tag, out XuiTagInfo? info) ? info : null;

    /// <summary>
    /// The property name a generated view binding exposes for an id: <c>XBtnLogin</c> → <c>BtnLogin</c>.
    /// Business code then reads <c>View.BtnLogin</c>.
    /// </summary>
    public static string PropertyNameFor(string id)
        => id.Length > 1 && id[0] == 'X' ? id.Substring(1) : id;

    /// <summary>The backing field name for an id: <c>XBtnLogin</c> → <c>_btnLogin</c>.</summary>
    public static string FieldNameFor(string id)
    {
        string property = PropertyNameFor(id);
        if (property.Length == 0) return "_";
        return "_" + char.ToLowerInvariant(property[0]) + property.Substring(1);
    }

    /// <summary>
    /// Completes a short authored id into the canonical prefixed form: <c>Confirm</c> on a
    /// <c>&lt;Btn&gt;</c> becomes <c>XBtnConfirm</c>. Already-canonical ids pass through unchanged.
    /// </summary>
    public static string CanonicalId(string tag, string id)
    {
        XuiTagInfo? info = Find(tag);
        if (info == null || id.Length == 0) return id;
        if (id.StartsWith(info.IdPrefix, StringComparison.Ordinal)) return id;
        return info.IdPrefix + char.ToUpperInvariant(id[0]) + id.Substring(1);
    }
}
