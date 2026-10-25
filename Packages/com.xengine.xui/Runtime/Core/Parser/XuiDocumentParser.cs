// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace XEngine.XUI.Core;

/// <summary>
/// Turns <c>.xui</c> text into the IR. Structural only: it enforces the document shape (root, the four
/// top-level elements, id syntax, mixed content) and the typed screen attributes, but it does <b>not</b>
/// decide whether a tag exists, whether an id is unique, or whether an attribute makes sense on it.
/// Those need the resolved template set and belong to lint, which reports every problem at once instead
/// of dying on the first — the difference between one edit and a guess-compile loop.
/// </summary>
public static class XuiDocumentParser
{
    private const string RootElementName = "XUI";

    public static XuiDocument ParseFile(string path, string? displayPath = null)
        => Parse(File.ReadAllText(path), displayPath ?? path);

    public static XuiDocument Parse(string xml, string? sourcePath = null)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new XuiParseException(
                "Malformed XML: " + ex.Message,
                new XuiSourceSpan(ex.LineNumber, ex.LinePosition),
                sourcePath);
        }

        XElement? root = doc.Root;
        if (root == null)
            throw new XuiParseException("The document is empty.", XuiSourceSpan.None, sourcePath);

        if (root.Name.LocalName != RootElementName)
        {
            throw new XuiParseException(
                "The root element must be <" + RootElementName + ">, not <" + root.Name.LocalName + ">.",
                SpanOf(root), sourcePath);
        }

        int version = ParseVersion(root, sourcePath);
        var document = new XuiDocument(version, sourcePath);

        foreach (XElement child in root.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Import":
                    document.Imports.Add(ParseImport(child, sourcePath));
                    break;
                case "Theme":
                    AddTheme(document, ParseTheme(child, sourcePath), sourcePath);
                    break;
                case "Screen":
                    AddScreen(document, ParseScreen(child, sourcePath), sourcePath);
                    break;
                case "Template":
                    AddTemplate(document, ParseTemplate(child, sourcePath), sourcePath);
                    break;
                default:
                    throw new XuiParseException(
                        "<" + child.Name.LocalName + "> is not allowed at the top level. "
                        + "Only <Import>, <Theme>, <Screen>, and <Template> are.",
                        SpanOf(child), sourcePath);
            }
        }

        if (document.Screens.Count == 0 && document.Templates.Count == 0)
        {
            throw new XuiParseException(
                "The document declares neither a <Screen> nor a <Template>, so nothing can be opened or reused.",
                SpanOf(root), sourcePath);
        }

        return document;
    }

    // ------------------------------------------------------------------
    //  Top-level elements
    // ------------------------------------------------------------------

    private static int ParseVersion(XElement root, string? sourcePath)
    {
        XAttribute? attribute = root.Attribute("version");
        if (attribute == null)
        {
            throw new XuiParseException(
                "<" + RootElementName + "> is missing the required version attribute (version=\""
                + XUIRuntime.DocumentVersion.ToString(CultureInfo.InvariantCulture) + "\").",
                SpanOf(root), sourcePath);
        }

        if (!XuiValueParser.TryParseInt(attribute.Value, out int version))
        {
            throw new XuiParseException(
                "version must be an integer, got \"" + attribute.Value + "\".",
                SpanOf(attribute), sourcePath);
        }

        if (version != XUIRuntime.DocumentVersion)
        {
            throw new XuiParseException(
                "Document version " + version.ToString(CultureInfo.InvariantCulture)
                + " is not supported by this XUI build (expected "
                + XUIRuntime.DocumentVersion.ToString(CultureInfo.InvariantCulture) + ").",
                SpanOf(attribute), sourcePath);
        }

        return version;
    }

    private static ImportRef ParseImport(XElement element, string? sourcePath)
    {
        string src = RequiredAttribute(element, "src", sourcePath);
        string? alias = element.Attribute("as")?.Value;
        if (alias != null && !IsIdentifier(alias))
        {
            throw new XuiParseException(
                "Import alias \"" + alias + "\" must be a plain identifier; it becomes the template tag prefix (alias.TemplateName).",
                SpanOf(element.Attribute("as")!), sourcePath);
        }
        if (element.HasElements)
        {
            throw new XuiParseException("<Import> cannot have children.", SpanOf(element), sourcePath);
        }
        return new ImportRef(src, alias, SpanOf(element));
    }

    private static ThemeBlock ParseTheme(XElement element, string? sourcePath)
    {
        string name = RequiredAttribute(element, "name", sourcePath);
        var theme = new ThemeBlock(name, element.Attribute("base")?.Value, SpanOf(element));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement child in element.Elements())
        {
            if (child.Name.LocalName != "Color")
            {
                throw new XuiParseException(
                    "<Theme> may only contain <Color>, found <" + child.Name.LocalName + ">.",
                    SpanOf(child), sourcePath);
            }

            string tokenName = RequiredAttribute(child, "name", sourcePath);
            string value = RequiredAttribute(child, "value", sourcePath);
            if (!seen.Add(tokenName))
            {
                throw new XuiParseException(
                    "Theme \"" + name + "\" defines the color token \"" + tokenName + "\" twice.",
                    SpanOf(child), sourcePath);
            }
            if (!XuiColorParser.IsLiteral(value))
            {
                throw new XuiParseException(
                    "Color token \"" + tokenName + "\" must be a hex literal or a CSS color name, got \"" + value
                    + "\". A token cannot reference another token.",
                    SpanOf(child), sourcePath);
            }
            theme.Colors.Add(new ColorEntry(tokenName, value, SpanOf(child)));
        }

        return theme;
    }

    private static ScreenDef ParseScreen(XElement element, string? sourcePath)
    {
        string name = RequiredAttribute(element, "name", sourcePath);
        if (!IsIdentifier(name))
        {
            throw new XuiParseException(
                "Screen name \"" + name + "\" must be a plain identifier; it is also the controller type name.",
                SpanOf(element), sourcePath);
        }

        ElementNode root = ParseElement(element, sourcePath, allowText: false, allowPlaceholders: false);
        var screen = new ScreenDef(name, root, SpanOf(element));

        XAttribute? canvas = element.Attribute("canvas");
        if (canvas != null)
        {
            if (!XuiValueParser.TryParseCanvasMode(canvas.Value, out XuiCanvasMode mode))
            {
                throw new XuiParseException(
                    "canvas must be overlay, camera, or world; got \"" + canvas.Value + "\".",
                    SpanOf(canvas), sourcePath);
            }
            screen.Canvas = mode;
        }

        XAttribute? layer = element.Attribute("layer");
        if (layer != null)
        {
            if (!XuiValueParser.TryParseLayer(layer.Value, out XuiLayerKind kind))
            {
                throw new XuiParseException(
                    "layer must be Normal, Fixed, PopUp, or Other; got \"" + layer.Value + "\".",
                    SpanOf(layer), sourcePath);
            }
            screen.Layer = kind;
        }

        XAttribute? scaleMode = element.Attribute("scale-mode");
        if (scaleMode != null)
        {
            if (!XuiValueParser.TryParseScaleMode(scaleMode.Value, out XuiScaleMode mode))
            {
                throw new XuiParseException(
                    "scale-mode must be auto or pixel; got \"" + scaleMode.Value + "\".",
                    SpanOf(scaleMode), sourcePath);
            }
            screen.ScaleMode = mode;
        }

        XAttribute? reference = element.Attribute("reference");
        if (reference != null)
        {
            if (!XuiValueParser.TryParseSize(reference.Value, out XuiSize size)
                || size.Width == null || size.Height == null)
            {
                throw new XuiParseException(
                    "reference must be a design resolution like 1920x1080; got \"" + reference.Value
                    + "\". Both axes are required.",
                    SpanOf(reference), sourcePath);
            }
            screen.Reference = size;
        }

        return screen;
    }

    private static TemplateDef ParseTemplate(XElement element, string? sourcePath)
    {
        string name = RequiredAttribute(element, "name", sourcePath);
        if (!IsTemplateName(name))
        {
            throw new XuiParseException(
                "Template name \"" + name + "\" must be a plain identifier; it is used directly as a tag.",
                SpanOf(element), sourcePath);
        }

        var parameters = new List<ParamDef>();
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        XElement? body = null;

        foreach (XElement child in element.Elements())
        {
            if (child.Name.LocalName == "Param")
            {
                if (body != null)
                {
                    throw new XuiParseException(
                        "<Param> declarations must come before the template body.",
                        SpanOf(child), sourcePath);
                }
                string parameterName = RequiredAttribute(child, "name", sourcePath);
                if (!IsIdentifier(parameterName))
                {
                    throw new XuiParseException(
                        "Template parameter \"" + parameterName + "\" must be a plain identifier.",
                        SpanOf(child), sourcePath);
                }
                if (!parameterNames.Add(parameterName))
                {
                    throw new XuiParseException(
                        "Template \"" + name + "\" declares the parameter \"" + parameterName + "\" twice.",
                        SpanOf(child), sourcePath);
                }
                parameters.Add(new ParamDef(parameterName, child.Attribute("default")?.Value, SpanOf(child)));
                continue;
            }

            if (body != null)
            {
                throw new XuiParseException(
                    "Template \"" + name + "\" has more than one root element. Wrap the content in a single <Frame>.",
                    SpanOf(child), sourcePath);
            }
            body = child;
        }

        if (body == null)
        {
            throw new XuiParseException(
                "Template \"" + name + "\" has an empty body; it must declare exactly one root element.",
                SpanOf(element), sourcePath);
        }

        var template = new TemplateDef(
            name,
            ParseElement(body, sourcePath, allowText: true, allowPlaceholders: true),
            SpanOf(element));
        for (int i = 0; i < parameters.Count; i++)
            template.Params.Add(parameters[i]);
        return template;
    }

    // ------------------------------------------------------------------
    //  Element tree
    // ------------------------------------------------------------------

    private static ElementNode ParseElement(
        XElement element,
        string? sourcePath,
        bool allowText,
        bool allowPlaceholders)
    {
        var node = new ElementNode(element.Name.LocalName, SpanOf(element));

        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;
            node.Attributes.Add(new XuiAttribute(attribute.Name.LocalName, attribute.Value, SpanOf(attribute)));
        }

        string? id = node.Id;
        if (id != null)
        {
            if (!(allowPlaceholders ? IsIdentifierOrPlaceholder(id) : IsIdentifier(id)))
            {
                throw new XuiParseException(
                    "id \"" + id + "\" must be a plain identifier; it becomes a generated C# property name."
                    + (allowPlaceholders ? " Inside a template it may also interpolate parameters, as in {rootId}Label." : ""),
                    node.Span, sourcePath);
            }
            // Uniqueness is deliberately NOT checked here. It is a lint rule (XUI-ID-DUPLICATE), so a
            // file with a duplicate id still reports everything else wrong with it in the same pass.
        }

        // A behaviour child (<Anim>) produces no node of its own, so it does not make this element a
        // container: <Btn><Anim .../>Go</Btn> is the natural way to give a button an entrance, and it
        // would be absurd to reject it as "text mixed with children".
        bool hasElementChildren = false;
        foreach (XElement child in element.Elements())
        {
            ElementNode childNode = ParseElement(child, sourcePath, allowText: true, allowPlaceholders);
            node.Children.Add(childNode);
            if (XuiTagLibrary.Find(childNode.Tag) is not { Kind: XuiTagKind.Behaviour })
                hasElementChildren = true;
        }

        if (!hasElementChildren)
        {
            string text = ReadDirectText(element);
            if (text.Length > 0)
            {
                if (!allowText)
                {
                    throw new XuiParseException(
                        "<" + node.Tag + "> cannot carry inline text.",
                        node.Span, sourcePath);
                }
                node.Text = text;
            }
        }
        else if (HasSignificantText(element))
        {
            throw new XuiParseException(
                "<" + node.Tag + "> mixes inline text with child elements. Put the text in its own <Text> child.",
                node.Span, sourcePath);
        }

        return node;
    }

    private static string ReadDirectText(XElement element)
    {
        string? single = null;
        System.Text.StringBuilder? builder = null;

        foreach (XNode child in element.Nodes())
        {
            if (child is not XText text) continue;
            if (single == null && builder == null)
            {
                single = text.Value;
                continue;
            }
            builder ??= new System.Text.StringBuilder(single);
            builder.Append(text.Value);
        }

        string joined = builder != null ? builder.ToString() : single ?? string.Empty;
        return joined.Trim();
    }

    private static bool HasSignificantText(XElement element)
        => ReadDirectText(element).Length > 0;

    // ------------------------------------------------------------------
    //  Uniqueness + helpers
    // ------------------------------------------------------------------

    private static void AddTheme(XuiDocument document, ThemeBlock theme, string? sourcePath)
    {
        for (int i = 0; i < document.Themes.Count; i++)
        {
            if (string.Equals(document.Themes[i].Name, theme.Name, StringComparison.Ordinal))
            {
                throw new XuiParseException(
                    "Theme \"" + theme.Name + "\" is declared twice.", theme.Span, sourcePath);
            }
        }
        document.Themes.Add(theme);
    }

    private static void AddScreen(XuiDocument document, ScreenDef screen, string? sourcePath)
    {
        if (document.FindScreen(screen.Name) != null)
        {
            throw new XuiParseException(
                "Screen \"" + screen.Name + "\" is declared twice.", screen.Span, sourcePath);
        }
        document.Screens.Add(screen);
    }

    private static void AddTemplate(XuiDocument document, TemplateDef template, string? sourcePath)
    {
        if (document.FindTemplate(template.Name) != null)
        {
            throw new XuiParseException(
                "Template \"" + template.Name + "\" is declared twice.", template.Span, sourcePath);
        }
        document.Templates.Add(template);
    }

    private static string RequiredAttribute(XElement element, string name, string? sourcePath)
    {
        XAttribute? attribute = element.Attribute(name);
        if (attribute == null || attribute.Value.Length == 0)
        {
            throw new XuiParseException(
                "<" + element.Name.LocalName + "> is missing the required " + name + " attribute.",
                SpanOf(element), sourcePath);
        }
        return attribute.Value;
    }

    /// <summary>Identifier grammar for ids, screen names, aliases, and template parameters.</summary>
    public static bool IsIdentifier(string text)
    {
        if (text.Length == 0) return false;
        if (!char.IsLetter(text[0]) && text[0] != '_') return false;
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// The relaxed id grammar that applies inside a template body, where an id may interpolate
    /// parameters (<c>{rootId}Label</c>). Only the brace structure is checked here; the substituted
    /// result is re-validated at the usage site, so a bad argument is reported where it was passed.
    /// </summary>
    public static bool IsIdentifierOrPlaceholder(string text)
    {
        if (text.Length == 0) return false;
        if (text.IndexOf('{') < 0) return IsIdentifier(text);

        int i = 0;
        bool sawSegment = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{')
            {
                int close = text.IndexOf('}', i + 1);
                if (close < 0) return false;
                if (!IsIdentifier(text.Substring(i + 1, close - i - 1))) return false;
                sawSegment = true;
                i = close + 1;
                continue;
            }
            if (c == '}') return false;
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
            sawSegment = true;
            i++;
        }
        return sawSegment;
    }

    /// <summary>
    /// Template names double as tags. An imported template is referenced as <c>alias.Name</c> — a dot
    /// rather than a colon, because a colon in an XML tag is a namespace prefix and would need an
    /// xmlns declaration the author never wrote.
    /// </summary>
    public static bool IsTemplateName(string text) => IsIdentifier(text);

    private static XuiSourceSpan SpanOf(XObject node)
    {
        if (node is IXmlLineInfo info && info.HasLineInfo())
            return new XuiSourceSpan(info.LineNumber, info.LinePosition);
        return XuiSourceSpan.None;
    }
}
