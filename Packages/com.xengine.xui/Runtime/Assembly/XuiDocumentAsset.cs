// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>One resolved <c>sprite=</c> or <c>font=</c> key.</summary>
/// <remarks>
/// Serialized <b>fields</b>, not auto-properties: Echo round-trips [SerializeField] fields only.
/// The original auto-property version survived every earlier test because no document had resolved
/// art yet — the list's count serialized, its contents came back empty, and every sprite fell back
/// to the default. A screen full of white rectangles, three layers away from the actual mistake.
/// </remarks>
public sealed class XuiAssetBinding
{
    [SerializeField] private string _key = string.Empty;
    [SerializeField] private Guid _guid;

    public string Key
    {
        get => _key;
        set => _key = value ?? string.Empty;
    }

    public Guid Guid
    {
        get => _guid;
        set => _guid = value;
    }
}

/// <summary>
/// An imported <c>.xui</c> file.
/// </summary>
/// <remarks>
/// <para>
/// The asset stores the document <b>text</b>, not a serialized IR. The text is the thing a human and
/// an agent both read and diff, and re-parsing it costs microseconds against a screen build that
/// touches the GPU — so there is exactly one representation to keep correct, and a hot reload is just
/// a re-import.
/// </para>
/// <para>
/// What the importer does add is the art it resolved: keys become GUIDs while the asset database is
/// available, so the runtime never resolves paths and the build's dependency graph sees the edges.
/// </para>
/// </remarks>
public sealed class XuiDocumentAsset : EngineObject
{
    [SerializeField] private string _source = string.Empty;
    [SerializeField] private List<XuiAssetBinding> _sprites = new();
    [SerializeField] private List<XuiAssetBinding> _fonts = new();

    [SerializeIgnore] private XuiDocument? _parsed;
    [SerializeIgnore] private XuiAssetTable? _assets;
    [SerializeIgnore] private string? _parseError;

    /// <summary>The document text exactly as authored.</summary>
    public string Source
    {
        get => _source;
        set
        {
            _source = value ?? string.Empty;
            Invalidate();
        }
    }

    public List<XuiAssetBinding> Sprites
    {
        get => _sprites;
        set
        {
            _sprites = value ?? new List<XuiAssetBinding>();
            _assets = null;
        }
    }

    public List<XuiAssetBinding> Fonts
    {
        get => _fonts;
        set
        {
            _fonts = value ?? new List<XuiAssetBinding>();
            _assets = null;
        }
    }

    /// <summary>
    /// Non-null when the text does not parse. The asset still exists and still holds the source, so a
    /// document broken mid-edit shows its error instead of vanishing from the project.
    /// </summary>
    public string? ParseError
    {
        get
        {
            EnsureParsed();
            return _parseError;
        }
    }

    /// <summary>The parsed IR, or null when <see cref="ParseError"/> is set. Parsed once and cached.</summary>
    public XuiDocument? Document
    {
        get
        {
            EnsureParsed();
            return _parsed;
        }
    }

    public IXuiAssetResolver AssetResolver
    {
        get
        {
            if (_assets != null) return _assets;

            var table = new XuiAssetTable();
            for (int i = 0; i < _sprites.Count; i++)
                table.AddSprite(_sprites[i].Key, _sprites[i].Guid);
            for (int i = 0; i < _fonts.Count; i++)
                table.AddFont(_fonts[i].Key, _fonts[i].Guid);
            _assets = table;
            return table;
        }
    }

    /// <summary>Drops the cached parse. Call after rewriting <see cref="Source"/> in place.</summary>
    public void Invalidate()
    {
        _parsed = null;
        _parseError = null;
        _assets = null;
    }

    /// <summary>
    /// Builds one screen from this document, expanding templates declared in the same file. Returns
    /// null when the document does not parse or has no screen by that name.
    /// </summary>
    public XuiScreenInstance? BuildScreen(string screenName, XuiBuildOptions? options = null)
    {
        XuiDocument? document = Document;
        ScreenDef? screen = document?.FindScreen(screenName);
        if (document == null || screen == null) return null;

        options ??= new XuiBuildOptions();
        options.Assets = AssetResolver;
        if (ReferenceEquals(options.Palette, XuiPalette.Empty))
            options.Palette = XuiPalette.FromDocument(document);

        var templates = new XuiTemplateTable();
        templates.Add(document);

        var expanded = new ScreenDef(
            screen.Name,
            XuiTemplateExpander.Expand(screen.Root, templates, AssetPath),
            screen.Span)
        {
            Canvas = screen.Canvas,
            Layer = screen.Layer,
            ScaleMode = screen.ScaleMode,
            Reference = screen.Reference,
        };

        return XuiScreenBuilder.Build(expanded, options);
    }

    private void EnsureParsed()
    {
        if (_parsed != null || _parseError != null) return;
        if (string.IsNullOrEmpty(_source))
        {
            _parseError = "The document is empty.";
            return;
        }

        try
        {
            _parsed = XuiDocumentParser.Parse(_source, string.IsNullOrEmpty(AssetPath) ? Name : AssetPath);
        }
        catch (XuiParseException ex)
        {
            _parseError = ex.Message;
        }
    }
}
