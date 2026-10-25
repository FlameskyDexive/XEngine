// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XEngine.XUI.Core;

/// <summary>Resolves a tag name to a template. Imported templates are keyed <c>alias.Name</c>.</summary>
public interface IXuiTemplateSource
{
    TemplateDef? Resolve(string tagName);
}

/// <summary>A template set backed by one document plus any documents it imported.</summary>
public sealed class XuiTemplateTable : IXuiTemplateSource
{
    private readonly Dictionary<string, TemplateDef> _templates = new(StringComparer.Ordinal);

    /// <summary>Registers a document's templates, optionally under an import alias.</summary>
    public void Add(XuiDocument document, string? alias = null)
    {
        for (int i = 0; i < document.Templates.Count; i++)
        {
            TemplateDef template = document.Templates[i];
            string key = alias == null ? template.Name : alias + "." + template.Name;
            _templates[key] = template;
        }
    }

    public TemplateDef? Resolve(string tagName)
        => _templates.TryGetValue(tagName, out TemplateDef? template) ? template : null;

    public IReadOnlyCollection<string> Names => _templates.Keys;
}

/// <summary>
/// Expands template usages into a plain element tree.
/// <para>
/// Parameters substitute as <c>{name}</c> anywhere in an attribute value or inline text — including
/// in <c>id</c>. That is deliberate: it is how a template gets used twice on one screen without the
/// expander inventing renamed ids behind the author's back. Two usages that both leave the inner ids
/// literal collide, and lint says so with both line numbers.
/// </para>
/// <para>
/// A <c>&lt;Slot/&gt;</c> in the template body is replaced by the usage site's children.
/// </para>
/// </summary>
public static class XuiTemplateExpander
{
    /// <summary>Guards against a template that (transitively) uses itself.</summary>
    public const int MaxDepth = 16;

    public static ElementNode Expand(ElementNode root, IXuiTemplateSource templates, string? sourcePath = null)
    {
        ElementNode copy = CloneShallow(root);
        ExpandChildren(root, copy, templates, sourcePath, depth: 0);
        return copy;
    }

    private static void ExpandChildren(
        ElementNode source,
        ElementNode target,
        IXuiTemplateSource templates,
        string? sourcePath,
        int depth)
    {
        for (int i = 0; i < source.Children.Count; i++)
        {
            ElementNode child = source.Children[i];
            TemplateDef? template = templates.Resolve(child.Tag);
            if (template == null)
            {
                ElementNode copy = CloneShallow(child);
                ExpandChildren(child, copy, templates, sourcePath, depth);
                target.Children.Add(copy);
                continue;
            }

            if (depth >= MaxDepth)
            {
                throw new XuiParseException(
                    "Template expansion exceeded " + MaxDepth.ToString(CultureInfo.InvariantCulture)
                    + " levels at <" + child.Tag + ">. A template most likely uses itself.",
                    child.Span, sourcePath);
            }

            target.Children.Add(ExpandUsage(child, template, templates, sourcePath, depth + 1));
        }
    }

    private static ElementNode ExpandUsage(
        ElementNode usage,
        TemplateDef template,
        IXuiTemplateSource templates,
        string? sourcePath,
        int depth)
    {
        Dictionary<string, string> arguments = BindArguments(usage, template, sourcePath);

        // The template body is expanded first (so a template may use other templates), then the
        // parameters are substituted into the result.
        ElementNode body = CloneShallow(template.Root);
        ExpandChildren(template.Root, body, templates, sourcePath, depth);

        ElementNode substituted = Substitute(body, arguments, usage.Span, sourcePath);
        InjectSlot(substituted, usage, templates, sourcePath, depth);
        return substituted;
    }

    private static Dictionary<string, string> BindArguments(
        ElementNode usage,
        TemplateDef template,
        string? sourcePath)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < template.Params.Count; i++)
        {
            ParamDef parameter = template.Params[i];
            string? supplied = usage.Attribute(parameter.Name);
            string? value = supplied ?? parameter.Default;
            if (value == null)
            {
                throw new XuiParseException(
                    "<" + usage.Tag + "> is missing the required template parameter \"" + parameter.Name
                    + "\", which has no default.",
                    usage.Span, sourcePath);
            }
            arguments.Add(parameter.Name, value);
        }

        // An attribute that is not a declared parameter is almost always a typo, and silently dropping
        // it produces a screen that looks right in the source and wrong on screen.
        for (int i = 0; i < usage.Attributes.Count; i++)
        {
            string name = usage.Attributes[i].Name;
            if (arguments.ContainsKey(name)) continue;
            throw new XuiParseException(
                "<" + usage.Tag + "> passes \"" + name + "\", which template \"" + template.Name
                + "\" does not declare as a <Param>.",
                usage.Attributes[i].Span.HasValue ? usage.Attributes[i].Span : usage.Span,
                sourcePath);
        }

        return arguments;
    }

    private static void InjectSlot(
        ElementNode node,
        ElementNode usage,
        IXuiTemplateSource templates,
        string? sourcePath,
        int depth)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            ElementNode child = node.Children[i];
            if (!string.Equals(child.Tag, "Slot", StringComparison.Ordinal))
            {
                InjectSlot(child, usage, templates, sourcePath, depth);
                continue;
            }

            node.Children.RemoveAt(i);
            var expandedUsageChildren = new ElementNode(usage.Tag, usage.Span);
            ExpandChildren(usage, expandedUsageChildren, templates, sourcePath, depth);
            node.Children.InsertRange(i, expandedUsageChildren.Children);
            return;
        }
    }

    // ------------------------------------------------------------------
    //  Cloning + substitution
    // ------------------------------------------------------------------

    private static ElementNode CloneShallow(ElementNode node)
    {
        var copy = new ElementNode(node.Tag, node.Span) { Text = node.Text };
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XuiAttribute attribute = node.Attributes[i];
            copy.Attributes.Add(new XuiAttribute(attribute.Name, attribute.Value, attribute.Span));
        }
        return copy;
    }

    private static ElementNode Substitute(
        ElementNode node,
        Dictionary<string, string> arguments,
        XuiSourceSpan usageSpan,
        string? sourcePath)
    {
        var copy = new ElementNode(node.Tag, node.Span)
        {
            Text = node.Text == null ? null : Interpolate(node.Text, arguments, usageSpan, sourcePath),
        };

        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XuiAttribute attribute = node.Attributes[i];
            string value = Interpolate(attribute.Value, arguments, usageSpan, sourcePath);

            // The template body was allowed to write id="{rootId}Label"; whether the supplied argument
            // makes that a legal identifier is only knowable now. Report it at the usage site, which is
            // where the wrong value was actually typed.
            if (string.Equals(attribute.Name, "id", StringComparison.Ordinal)
                && !XuiDocumentParser.IsIdentifier(value))
            {
                throw new XuiParseException(
                    "Template expansion produced the id \"" + value + "\", which is not a plain identifier. "
                    + "Check the arguments passed here.",
                    usageSpan, sourcePath);
            }

            copy.Attributes.Add(new XuiAttribute(attribute.Name, value, attribute.Span));
        }

        for (int i = 0; i < node.Children.Count; i++)
            copy.Children.Add(Substitute(node.Children[i], arguments, usageSpan, sourcePath));

        return copy;
    }

    /// <summary>
    /// Replaces <c>{name}</c> with the bound argument. <c>{{</c> and <c>}}</c> are literal braces, so a
    /// document can still contain them.
    /// </summary>
    internal static string Interpolate(
        string text,
        Dictionary<string, string> arguments,
        XuiSourceSpan usageSpan,
        string? sourcePath)
    {
        if (text.IndexOf('{') < 0) return text;

        var builder = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                builder.Append('{');
                i += 2;
                continue;
            }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                builder.Append('}');
                i += 2;
                continue;
            }
            if (c != '{')
            {
                builder.Append(c);
                i++;
                continue;
            }

            int close = text.IndexOf('}', i + 1);
            if (close < 0)
            {
                throw new XuiParseException(
                    "Unclosed \"{\" in \"" + text + "\". Write \"{{\" for a literal brace.",
                    usageSpan, sourcePath);
            }

            string name = text.Substring(i + 1, close - i - 1);
            if (!arguments.TryGetValue(name, out string? value))
            {
                throw new XuiParseException(
                    "\"{" + name + "}\" does not match any <Param> of this template.",
                    usageSpan, sourcePath);
            }
            builder.Append(value);
            i = close + 1;
        }

        return builder.ToString();
    }
}
