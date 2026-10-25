// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.XUI.Core;

/// <summary>
/// The deep-link grammar: <c>scheme://Screen?a=1&amp;b=2</c>.
/// </summary>
/// <remarks>
/// A link is text, so parsing it lives in Core alongside the other text formats — which also means the
/// offline tools and the runtime router agree by construction about what a valid link is, and the
/// grammar can be tested without an engine.
/// </remarks>
public static class XuiLink
{
    public const string DefaultScheme = "xui";

    /// <summary>
    /// Parses a link. A bare screen name is accepted as well as a full link, because that is what a
    /// hand-written call passes and rejecting it would only invite string concatenation at call sites.
    /// </summary>
    public static bool TryParse(string? link, out XuiRouteQuery? query, string scheme = DefaultScheme)
    {
        query = null;
        if (string.IsNullOrWhiteSpace(link)) return false;

        string rest = link;
        int schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            if (!string.Equals(link.Substring(0, schemeEnd), scheme, StringComparison.OrdinalIgnoreCase))
                return false;
            rest = link.Substring(schemeEnd + 3);
        }
        else if (link.StartsWith("://", StringComparison.Ordinal))
        {
            return false;
        }

        int queryStart = rest.IndexOf('?');
        string screenName = (queryStart < 0 ? rest : rest.Substring(0, queryStart)).TrimEnd('/');
        if (!XuiDocumentParser.IsIdentifier(screenName)) return false;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryStart >= 0) ParsePairs(rest.Substring(queryStart + 1), values);

        query = new XuiRouteQuery(screenName, values);
        return true;
    }

    private static void ParsePairs(string text, Dictionary<string, string> into)
    {
        string[] pairs = text.Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < pairs.Length; i++)
        {
            string pair = pairs[i];
            int equals = pair.IndexOf('=');
            if (equals < 0)
            {
                // A bare flag reads as present/true, which is how links get written in practice.
                string flag = Unescape(pair);
                if (flag.Length > 0) into[flag] = "true";
                continue;
            }

            string key = Unescape(pair.Substring(0, equals));
            if (key.Length == 0) continue;
            into[key] = Unescape(pair.Substring(equals + 1));
        }
    }

    /// <summary>
    /// Percent-decodes, tolerating a malformed escape. A link often arrives from outside the app, and
    /// throwing on one bad character would take down navigation over a value nobody reads.
    /// </summary>
    private static string Unescape(string text)
    {
        if (text.IndexOf('%') < 0) return text;
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
