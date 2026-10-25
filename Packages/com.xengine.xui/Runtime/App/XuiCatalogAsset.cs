// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// An imported <c>.po</c> translation catalog.
/// </summary>
/// <remarks>
/// Like <see cref="XuiDocumentAsset"/>, this keeps the <b>text</b> rather than a serialized lookup.
/// The <c>.po</c> is what a translator edits and what version control diffs, so having one
/// representation to keep correct is worth re-parsing a few kilobytes on load — and a re-import is
/// then all a hot reload needs to be.
/// </remarks>
public sealed class XuiCatalogAsset : EngineObject
{
    [SerializeField] private string _source = string.Empty;
    [SerializeField] private string _locale = string.Empty;

    [SerializeIgnore] private XuiCatalog? _catalog;
    [SerializeIgnore] private string? _parseError;

    /// <summary>The file's text exactly as written.</summary>
    public string Source
    {
        get => _source;
        set
        {
            _source = value ?? string.Empty;
            _catalog = null;
            _parseError = null;
        }
    }

    /// <summary>
    /// The locale tag, taken from the file name (<c>zh-Hans.po</c>) or the <c>Language</c> header.
    /// </summary>
    public string Locale
    {
        get => _locale;
        set
        {
            _locale = value ?? string.Empty;
            _catalog = null;
        }
    }

    /// <summary>The parsed lookup, or null when the file does not parse.</summary>
    public XuiCatalog? Catalog
    {
        get
        {
            if (_catalog != null || _parseError != null) return _catalog;
            try
            {
                PoCatalog po = PoCatalog.Parse(_source, _locale + ".po");
                _catalog = XuiCatalog.FromPo(po, ResolveLocale(po));
            }
            catch (XuiParseException ex)
            {
                _parseError = ex.Message;
            }
            return _catalog;
        }
    }

    /// <summary>Why the file did not parse, or null.</summary>
    public string? ParseError
    {
        get
        {
            _ = Catalog;
            return _parseError;
        }
    }

    /// <summary>
    /// The file name wins over the <c>Language</c> header: the name is what a person sees in the
    /// project, and a header copied from another file is the more likely of the two to be wrong.
    /// </summary>
    private string ResolveLocale(PoCatalog po)
    {
        if (_locale.Length > 0) return _locale;

        for (int i = 0; i < po.Header.Count; i++)
        {
            if (string.Equals(po.Header[i].Key, "Language", StringComparison.OrdinalIgnoreCase))
                return po.Header[i].Value;
        }
        return string.Empty;
    }
}
