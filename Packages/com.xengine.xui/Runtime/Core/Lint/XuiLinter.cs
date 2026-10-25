// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// Semantic checks the parser deliberately does not make.
/// </summary>
/// <remarks>
/// <para>
/// The split is by information, not by taste. The parser knows only that the file is well-formed, and
/// stops at the first thing that prevents an IR existing at all; the linter knows the resolved template
/// set and the theme tokens, and reports <b>every</b> finding at once — which is what turns a fix into
/// one edit instead of a guess-compile loop. Id uniqueness lives here for exactly that reason.
/// </para>
/// <para>
/// Rules run against the <em>expanded</em> tree, so a template used twice is judged on what it
/// actually produced rather than on what it looked like before substitution.
/// </para>
/// </remarks>
public sealed class XuiLinter
{
    /// <summary>Rule ids. Referenced by tools, docs, and review comments.</summary>
    public static class Codes
    {
        public const string UnknownTag = "XUI-TAG-UNKNOWN";
        public const string IdPrefix = "XUI-ID-PREFIX";
        public const string DuplicateId = "XUI-ID-DUPLICATE";
        public const string MissingId = "XUI-ID-MISSING";
        public const string UnknownAttribute = "XUI-ATTR-UNKNOWN";
        public const string BadAttributeValue = "XUI-ATTR-VALUE";
        public const string LayoutChildRect = "XUI-LAYOUT-CHILD-RECT";
        public const string UnknownColorToken = "XUI-COLOR-TOKEN";
        public const string UnknownTheme = "XUI-THEME-BASE";
        public const string ChildrenNotAllowed = "XUI-CHILDREN-NOT-ALLOWED";
        public const string TextNotAllowed = "XUI-TEXT-NOT-ALLOWED";
        public const string EmptyScreen = "XUI-SCREEN-EMPTY";
        public const string UnusedTemplate = "XUI-TEMPLATE-UNUSED";
        public const string UnknownBind = "XUI-TAB-BIND";
        public const string TabOutsideBar = "XUI-TAB-ORPHAN";
        public const string AnimAtScreenRoot = "XUI-ANIM-ORPHAN";
        public const string AnimDoesNothing = "XUI-ANIM-EMPTY";
    }

    private readonly List<XuiLintIssue> _issues = new();
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedTemplates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XuiSourceSpan> _ids = new(StringComparer.Ordinal);
    private readonly List<(string Bound, XuiSourceSpan Span, string? ElementId)> _pendingBinds = new();

    /// <summary>
    /// Lints a document. <paramref name="templates"/> resolves imported templates; pass null when only
    /// this document's own templates are visible, and unresolved tags are reported as unknown.
    /// </summary>
    public static IReadOnlyList<XuiLintIssue> Lint(XuiDocument document, IXuiTemplateSource? templates = null)
    {
        var linter = new XuiLinter();
        linter.Run(document, templates);
        return linter._issues;
    }

    public static bool HasErrors(IReadOnlyList<XuiLintIssue> issues)
    {
        for (int i = 0; i < issues.Count; i++)
        {
            if (issues[i].Severity == XuiLintSeverity.Error) return true;
        }
        return false;
    }

    private void Run(XuiDocument document, IXuiTemplateSource? templates)
    {
        CollectTokens(document);
        CheckThemeBases(document);

        var table = new XuiTemplateTable();
        table.Add(document);
        IXuiTemplateSource source = templates ?? table;

        for (int i = 0; i < document.Screens.Count; i++)
        {
            ScreenDef screen = document.Screens[i];
            _ids.Clear();

            ElementNode expanded;
            try
            {
                expanded = XuiTemplateExpander.Expand(screen.Root, source, document.SourcePath);
            }
            catch (XuiParseException ex)
            {
                // Expansion failures are structural, not stylistic. Report and move to the next screen
                // so one broken template does not hide every finding in the rest of the file.
                _issues.Add(new XuiLintIssue(Codes.UnknownTag, XuiLintSeverity.Error, ex.RawMessage, ex.Span));
                continue;
            }

            if (expanded.Children.Count == 0)
            {
                _issues.Add(new XuiLintIssue(
                    Codes.EmptyScreen,
                    XuiLintSeverity.Warning,
                    "Screen \"" + screen.Name + "\" has no elements, so opening it shows nothing.",
                    screen.Span));
            }

            _pendingBinds.Clear();
            for (int c = 0; c < expanded.Children.Count; c++)
                WalkElement(expanded.Children[c], parentTag: null);

            // Resolved after the walk: a tab may bind a panel the document declares further down.
            for (int b = 0; b < _pendingBinds.Count; b++)
            {
                (string bound, XuiSourceSpan span, string? elementId) = _pendingBinds[b];
                if (_ids.ContainsKey(bound)) continue;
                _issues.Add(new XuiLintIssue(
                    Codes.UnknownBind,
                    XuiLintSeverity.Error,
                    "bind=\"" + bound + "\" names no element in screen \"" + screen.Name
                    + "\", so this tab would reveal nothing.",
                    span,
                    elementId));
            }
        }

        // Unexpanded template bodies still get an attribute and value pass, so a template nobody uses
        // yet is not a place mistakes accumulate unseen.
        for (int i = 0; i < document.Templates.Count; i++)
            WalkTemplateBody(document.Templates[i].Root);

        ReportUnusedTemplates(document, source);
    }

    // ------------------------------------------------------------------
    //  Themes
    // ------------------------------------------------------------------

    private void CollectTokens(XuiDocument document)
    {
        for (int i = 0; i < document.Themes.Count; i++)
        {
            ThemeBlock theme = document.Themes[i];
            for (int c = 0; c < theme.Colors.Count; c++)
                _tokens.Add(theme.Colors[c].Name);
        }
    }

    private void CheckThemeBases(XuiDocument document)
    {
        for (int i = 0; i < document.Themes.Count; i++)
        {
            ThemeBlock theme = document.Themes[i];
            if (theme.Base == null) continue;
            if (document.Themes.Exists(t => string.Equals(t.Name, theme.Base, StringComparison.Ordinal))) continue;

            _issues.Add(new XuiLintIssue(
                Codes.UnknownTheme,
                XuiLintSeverity.Error,
                "Theme \"" + theme.Name + "\" inherits from \"" + theme.Base + "\", which this document does not declare.",
                theme.Span));
        }
    }

    // ------------------------------------------------------------------
    //  Elements
    // ------------------------------------------------------------------

    private void WalkElement(ElementNode node, XuiTagInfo? parentTag)
    {
        XuiTagInfo? tag = XuiTagLibrary.Find(node.Tag);
        if (tag == null)
        {
            _issues.Add(new XuiLintIssue(
                Codes.UnknownTag,
                XuiLintSeverity.Error,
                "<" + node.Tag + "> is neither a built-in tag nor a template this document declares or imports.",
                node.Span,
                node.Id));
            return;
        }

        CheckId(node, tag);
        CheckAttributes(node, tag);
        CheckContent(node, tag);
        CheckRectUnderLayout(node, parentTag);
        CheckTab(node, tag, parentTag);
        CheckAnim(node, tag, parentTag);

        for (int i = 0; i < node.Children.Count; i++)
            WalkElement(node.Children[i], tag);
    }

    /// <summary>Attribute and value checks only — ids and duplicates are meaningless before expansion.</summary>
    private void WalkTemplateBody(ElementNode node)
    {
        XuiTagInfo? tag = XuiTagLibrary.Find(node.Tag);
        if (tag != null)
        {
            CheckAttributes(node, tag);
            CheckContent(node, tag);
        }

        for (int i = 0; i < node.Children.Count; i++)
            WalkTemplateBody(node.Children[i]);
    }

    private void CheckId(ElementNode node, XuiTagInfo tag)
    {
        string? id = node.Id;
        if (id == null)
        {
            // Controls exist to be driven from code. One with no id cannot be reached, so it is either
            // a mistake or decoration that should have been a plain Image.
            if (tag.Kind == XuiTagKind.Control)
            {
                _issues.Add(new XuiLintIssue(
                    Codes.MissingId,
                    XuiLintSeverity.Warning,
                    "<" + node.Tag + "> has no id, so no code can reach it.",
                    node.Span));
            }
            return;
        }

        if (!id.StartsWith(tag.IdPrefix, StringComparison.Ordinal))
        {
            _issues.Add(new XuiLintIssue(
                Codes.IdPrefix,
                XuiLintSeverity.Error,
                "id \"" + id + "\" on <" + node.Tag + "> must start with \"" + tag.IdPrefix
                + "\". The prefix is what makes the generated binding's type derivable from the document.",
                node.Span,
                id));
        }

        if (_ids.TryGetValue(id, out XuiSourceSpan first))
        {
            _issues.Add(new XuiLintIssue(
                Codes.DuplicateId,
                XuiLintSeverity.Error,
                "id \"" + id + "\" is already used at line " + first.Line.ToString(CultureInfo.InvariantCulture)
                + " (ids are compared after template expansion). Ids must be unique so the generated "
                + "binding is unambiguous; parameterise a template's ids so each usage names its own.",
                node.Span,
                id));
            return;
        }

        _ids.Add(id, node.Span);
    }

    private void CheckAttributes(ElementNode node, XuiTagInfo tag)
    {
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XuiAttribute attribute = node.Attributes[i];
            if (!tag.Accepts(attribute.Name))
            {
                _issues.Add(new XuiLintIssue(
                    Codes.UnknownAttribute,
                    XuiLintSeverity.Error,
                    "<" + node.Tag + "> does not accept \"" + attribute.Name + "\". It would be silently dropped.",
                    attribute.Span.HasValue ? attribute.Span : node.Span,
                    node.Id));
                continue;
            }

            CheckAttributeValue(node, attribute, tag);
        }
    }

    private void CheckAttributeValue(ElementNode node, XuiAttribute attribute, XuiTagInfo tag)
    {
        // A value still holding a placeholder belongs to a template body; it is checked after expansion.
        if (attribute.Value.IndexOf('{') >= 0) return;

        XuiSourceSpan span = attribute.Span.HasValue ? attribute.Span : node.Span;

        // A transition reads several attribute names differently from an element: "on" is a trigger
        // name rather than a checked state, and "scale" is a from:to pair rather than a fixed value.
        if (tag.Kind == XuiTagKind.Behaviour && CheckTransitionValue(node, attribute, span)) return;

        switch (attribute.Name)
        {
            case "anchor":
                Require(XuiValueParser.TryParseAnchor(attribute.Value, out _), attribute, node, span,
                    "one of center / top / bottom / left / right / top-left / top-right / bottom-left / "
                    + "bottom-right / stretch / top-stretch / bottom-stretch / left-stretch / right-stretch");
                break;
            case "size":
            case "cell":
                Require(XuiValueParser.TryParseSize(attribute.Value, out _), attribute, node, span,
                    "WxH, where either axis may be _ (for example 480x360, 400x_, _x44)");
                break;
            case "pos":
            case "pivot":
            case "scale":
                Require(XuiValueParser.TryParseFloat2(attribute.Value, out _), attribute, node, span, "x,y");
                break;
            case "padding":
                Require(
                    XuiValueParser.TryParseInsets(attribute.Value, out _, out _, out _, out _),
                    attribute, node, span, "a single number, or T,R,B,L");
                break;
            case "spacing":
                Require(
                    XuiValueParser.TryParseFloat(attribute.Value, out _)
                    || XuiValueParser.TryParseFloat2(attribute.Value, out _),
                    attribute, node, span, "a number (or x,y on Grid)");
                break;
            case "hidden":
            case "translate":
            case "visible":
            case "conform-x":
            case "conform-y":
            case "raycast":
            case "rich":
            case "interactable":
            case "on":
            case "password":
            case "readonly":
            case "horizontal":
            case "vertical":
            case "whole-numbers":
            case "stretch-children":
            case "expand":
                Require(XuiValueParser.TryParseBool(attribute.Value, out _), attribute, node, span,
                    "true or false");
                break;
            case "font-size":
            case "columns":
            case "character-limit":
            case "selected":
                Require(XuiValueParser.TryParseInt(attribute.Value, out _), attribute, node, span, "an integer");
                break;
            case "duration":
            case "delay":
            case "min":
            case "max":
            case "value":
                Require(XuiValueParser.TryParseFloat(attribute.Value, out _), attribute, node, span, "a number");
                break;
            case "align":
                Require(XuiValueParser.TryParseTextAlign(attribute.Value, out _), attribute, node, span,
                    "left, center, or right");
                break;
            case "type":
                Require(XuiValueParser.TryParseImageFit(attribute.Value, out _), attribute, node, span,
                    "simple, sliced, tiled, filled, or contain");
                break;
            case "ease":
                Require(XuiValueParser.TryParseEase(attribute.Value, out _), attribute, node, span,
                    "linear, out-quad, in-quad, in-out-quad, or out-back");
                break;
            case "direction":
                Require(XuiValueParser.TryParseProgressDirection(attribute.Value, out _), attribute, node, span,
                    "left-to-right, right-to-left, bottom-to-top, or top-to-bottom");
                break;
            case "mask":
                Require(string.Equals(attribute.Value, "rect", StringComparison.Ordinal),
                    attribute, node, span, "rect");
                break;
            case "color":
            case "text-color":
            case "fill-color":
                CheckColor(node, attribute, span);
                break;
            default:
                break;
        }
    }

    private void CheckColor(ElementNode node, XuiAttribute attribute, XuiSourceSpan span)
    {
        XuiColor color = XuiColorParser.Parse(attribute.Value);
        if (!color.IsToken) return;

        // A hex typo reads as a token name, so say both things it could be.
        if (_tokens.Contains(color.Token!)) return;

        _issues.Add(new XuiLintIssue(
            Codes.UnknownColorToken,
            XuiLintSeverity.Error,
            "\"" + attribute.Value + "\" on <" + node.Tag + "> is not a hex literal, a CSS colour name, "
            + "or a token any <Theme> in this document declares.",
            span,
            node.Id));
    }

    private void Require(
        bool ok,
        XuiAttribute attribute,
        ElementNode node,
        XuiSourceSpan span,
        string expected)
    {
        if (ok) return;
        _issues.Add(new XuiLintIssue(
            Codes.BadAttributeValue,
            XuiLintSeverity.Error,
            attribute.Name + "=\"" + attribute.Value + "\" on <" + node.Tag + "> is not valid; expected " + expected + ".",
            span,
            node.Id));
    }

    private void CheckContent(ElementNode node, XuiTagInfo tag)
    {
        if (node.Text != null && !tag.AcceptsText)
        {
            _issues.Add(new XuiLintIssue(
                Codes.TextNotAllowed,
                XuiLintSeverity.Error,
                "<" + node.Tag + "> cannot carry inline text; put it in a <Text> child.",
                node.Span,
                node.Id));
        }

        if (node.Children.Count > 0 && !tag.AcceptsChildren)
        {
            _issues.Add(new XuiLintIssue(
                Codes.ChildrenNotAllowed,
                XuiLintSeverity.Error,
                "<" + node.Tag + "> builds its own sub-hierarchy and cannot take children; they would be discarded.",
                node.Span,
                node.Id));
        }
    }

    /// <summary>
    /// The attributes whose grammar a transition owns. Returns false for the shared ones
    /// (<c>duration</c> / <c>delay</c> / <c>ease</c>) so they fall through to the common rules.
    /// </summary>
    private bool CheckTransitionValue(ElementNode node, XuiAttribute attribute, XuiSourceSpan span)
    {
        switch (attribute.Name)
        {
            case "on":
                Require(attribute.Value.Length > 0, attribute, node, span,
                    "a trigger name, such as show or hide");
                return true;
            case "move":
                Require(XuiValueParser.TryParseTweenOffset(attribute.Value, out _, out _), attribute, node,
                    span, "from:to offsets, as in 0,-24:0,0 — either side may be empty to mean rest");
                return true;
            case "scale":
                Require(XuiValueParser.TryParseTweenScale(attribute.Value, out _, out _), attribute, node,
                    span, "from:to scales, as in 0.9:1 — either side may be empty to mean rest");
                return true;
            case "fade":
                Require(XuiValueParser.TryParseTweenAlpha(attribute.Value, out _, out _), attribute, node,
                    span, "from:to alpha in 0..1, as in 0:1 — either side may be empty to mean opaque");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// An <c>&lt;Anim&gt;</c> animates the element it sits inside, so it needs one — and it has to
    /// actually change something, or it is a transition that plays and does nothing.
    /// </summary>
    private void CheckAnim(ElementNode node, XuiTagInfo tag, XuiTagInfo? parentTag)
    {
        if (tag.Kind != XuiTagKind.Behaviour) return;

        if (parentTag == null)
        {
            _issues.Add(new XuiLintIssue(
                Codes.AnimAtScreenRoot,
                XuiLintSeverity.Error,
                "<Anim> animates the element it is inside; at the top of a screen there is nothing for it "
                + "to animate. Put it inside the element it should move.",
                node.Span,
                node.Id));
            return;
        }

        bool changesSomething = node.HasAttribute("move")
            || node.HasAttribute("scale")
            || node.HasAttribute("fade");
        if (changesSomething) return;

        _issues.Add(new XuiLintIssue(
            Codes.AnimDoesNothing,
            XuiLintSeverity.Warning,
            "<Anim> sets neither move, scale, nor fade, so it plays and changes nothing.",
            node.Span,
            node.Id));
    }

    /// <summary>
    /// A <c>&lt;Tab&gt;</c> only means something inside a <c>&lt;TabBar&gt;</c>, and its <c>bind</c> only
    /// means something if some element actually carries that id.
    /// </summary>
    private void CheckTab(ElementNode node, XuiTagInfo tag, XuiTagInfo? parentTag)
    {
        if (!string.Equals(tag.Tag, "Tab", StringComparison.Ordinal)) return;

        if (parentTag == null || !string.Equals(parentTag.Tag, "TabBar", StringComparison.Ordinal))
        {
            _issues.Add(new XuiLintIssue(
                Codes.TabOutsideBar,
                XuiLintSeverity.Error,
                "<Tab> only works as a direct child of <TabBar>; nothing else selects it.",
                node.Span,
                node.Id));
        }

        string? bound = node.Attribute("bind");
        if (bound == null) return;
        if (bound.IndexOf('{') >= 0) return;

        _pendingBinds.Add((bound, node.Span, node.Id));
    }

    /// <summary>
    /// A layout container owns its children's rects: it writes their position and size every rebuild.
    /// An <c>anchor</c> or <c>pos</c> on a direct child is therefore dead — and worse, it reads as
    /// intent, so the next person spends time wondering why moving the number changes nothing.
    /// </summary>
    private void CheckRectUnderLayout(ElementNode node, XuiTagInfo? parentTag)
    {
        if (parentTag is not { IsLayoutGroup: true }) return;

        ReportDeadRectAttribute(node, "anchor", parentTag);
        ReportDeadRectAttribute(node, "pos", parentTag);
    }

    private void ReportDeadRectAttribute(ElementNode node, string name, XuiTagInfo parentTag)
    {
        XuiAttribute? attribute = node.FindAttribute(name);
        if (attribute == null) return;

        _issues.Add(new XuiLintIssue(
            Codes.LayoutChildRect,
            XuiLintSeverity.Warning,
            name + " has no effect on a direct child of <" + parentTag.Tag
            + ">: the container writes its children's rects. Use size, spacing, padding, or align instead.",
            attribute.Span.HasValue ? attribute.Span : node.Span,
            node.Id));
    }

    // ------------------------------------------------------------------
    //  Templates
    // ------------------------------------------------------------------

    private void ReportUnusedTemplates(XuiDocument document, IXuiTemplateSource source)
    {
        for (int i = 0; i < document.Screens.Count; i++)
        {
            try
            {
                MarkUsedTemplates(document.Screens[i].Root, source);
            }
            catch (XuiParseException)
            {
                // Already reported by the expansion pass.
            }
        }

        for (int i = 0; i < document.Templates.Count; i++)
        {
            TemplateDef template = document.Templates[i];
            if (_usedTemplates.Contains(template.Name)) continue;

            _issues.Add(new XuiLintIssue(
                Codes.UnusedTemplate,
                XuiLintSeverity.Warning,
                "Template \"" + template.Name + "\" is never used in this document. "
                + "Move it to a shared document if other screens import it.",
                template.Span));
        }
    }

    private void MarkUsedTemplates(ElementNode node, IXuiTemplateSource source)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            ElementNode child = node.Children[i];
            if (source.Resolve(child.Tag) != null)
            {
                _usedTemplates.Add(child.Tag);
                // A template body may use others; mark those too.
                TemplateDef? template = source.Resolve(child.Tag);
                if (template != null) MarkUsedTemplates(template.Root, source);
            }
            MarkUsedTemplates(child, source);
        }
    }
}
