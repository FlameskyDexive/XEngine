// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>A <c>&lt;Import src="..." as="ns"/&gt;</c> declaration: pull another document's templates in.</summary>
public sealed class ImportRef
{
    public ImportRef(string src, string? alias, XuiSourceSpan span = default)
    {
        Src = src;
        Alias = alias;
        Span = span;
    }

    /// <summary>Document path relative to the UI source root, without the <c>.xui</c> extension.</summary>
    public string Src { get; }

    /// <summary>Optional namespace prefix; imported templates become <c>alias:Name</c>.</summary>
    public string? Alias { get; }

    public XuiSourceSpan Span { get; }
}

/// <summary>One named color token inside a <see cref="ThemeBlock"/>.</summary>
public sealed class ColorEntry
{
    public ColorEntry(string name, string value, XuiSourceSpan span = default)
    {
        Name = name;
        Value = value;
        Span = span;
    }

    /// <summary>kebab-case token name, e.g. <c>panel-bg</c>.</summary>
    public string Name { get; }

    /// <summary>Hex literal or CSS color name. Resolved at apply time, not at parse time.</summary>
    public string Value { get; }

    public XuiSourceSpan Span { get; }
}

/// <summary>A named palette. <c>Base</c> inherits every token from another theme before overriding.</summary>
public sealed class ThemeBlock
{
    public ThemeBlock(string name, string? baseTheme, XuiSourceSpan span = default)
    {
        Name = name;
        Base = baseTheme;
        Span = span;
    }

    public string Name { get; }
    public string? Base { get; }
    public XuiSourceSpan Span { get; }
    public List<ColorEntry> Colors { get; } = new();
}

/// <summary>A template parameter, substituted into the body as <c>{name}</c>.</summary>
public sealed class ParamDef
{
    public ParamDef(string name, string? defaultValue, XuiSourceSpan span = default)
    {
        Name = name;
        Default = defaultValue;
        Span = span;
    }

    public string Name { get; }
    public string? Default { get; }
    public XuiSourceSpan Span { get; }
}

/// <summary>
/// A reusable subtree, expanded where its name is used as a tag. The body must have exactly one root
/// element so expansion is a straight node substitution with no wrapper.
/// </summary>
public sealed class TemplateDef
{
    public TemplateDef(string name, ElementNode root, XuiSourceSpan span = default)
    {
        Name = name;
        Root = root;
        Span = span;
    }

    public string Name { get; }
    public ElementNode Root { get; }
    public XuiSourceSpan Span { get; }
    public List<ParamDef> Params { get; } = new();
}

/// <summary>
/// One complete screen — one Canvas, opened by name. <see cref="Root"/> is the <c>&lt;Screen&gt;</c>
/// element itself: the typed properties below are the parsed view of its structural attributes, while
/// the node keeps every attribute as authored so a patch can round-trip the file without reshaping it.
/// </summary>
public sealed class ScreenDef
{
    public ScreenDef(string name, ElementNode root, XuiSourceSpan span = default)
    {
        Name = name;
        Root = root;
        Span = span;
    }

    public string Name { get; }
    public ElementNode Root { get; }
    public XuiSourceSpan Span { get; }

    public XuiCanvasMode Canvas { get; set; } = XuiCanvasMode.Overlay;
    public XuiLayerKind Layer { get; set; } = XuiLayerKind.Normal;
    public XuiScaleMode ScaleMode { get; set; } = XuiScaleMode.Auto;

    /// <summary>Design resolution from <c>reference="1920x1080"</c>. Null keeps the canvas unscaled.</summary>
    public XuiSize? Reference { get; set; }
}

/// <summary>
/// A parsed <c>.xui</c> file. Import/Theme/Screen/Template are the only things allowed at the top
/// level; everything else in the language lives inside a screen or template body.
/// </summary>
public sealed class XuiDocument
{
    public XuiDocument(int version, string? sourcePath)
    {
        Version = version;
        SourcePath = sourcePath;
    }

    /// <summary>The <c>version</c> attribute on the root element. Validated against
    /// <see cref="XUIRuntime.DocumentVersion"/> at parse time.</summary>
    public int Version { get; }

    /// <summary>Project-relative path, when the document came from a file. Diagnostics only.</summary>
    public string? SourcePath { get; }

    public List<ImportRef> Imports { get; } = new();
    public List<ThemeBlock> Themes { get; } = new();
    public List<ScreenDef> Screens { get; } = new();
    public List<TemplateDef> Templates { get; } = new();

    public ScreenDef? FindScreen(string name)
    {
        for (int i = 0; i < Screens.Count; i++)
        {
            if (string.Equals(Screens[i].Name, name, StringComparison.Ordinal))
                return Screens[i];
        }
        return null;
    }

    public TemplateDef? FindTemplate(string name)
    {
        for (int i = 0; i < Templates.Count; i++)
        {
            if (string.Equals(Templates[i].Name, name, StringComparison.Ordinal))
                return Templates[i];
        }
        return null;
    }
}
