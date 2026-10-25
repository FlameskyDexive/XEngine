// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XEngine.XUI.Core;

/// <summary>
/// The parameters a deep link carried, handed to a controller as its <c>OnShow</c> context.
/// </summary>
/// <remarks>
/// Values are strings because a link is text. The typed accessors exist so a controller reads
/// <c>query.GetInt("level")</c> rather than parsing by hand at every call site — and so a malformed
/// link produces a default instead of an exception halfway through showing a screen.
/// </remarks>
public sealed class XuiRouteQuery
{
    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _values;

    public XuiRouteQuery(string screenName, IReadOnlyDictionary<string, string>? values = null)
    {
        ScreenName = screenName;
        _values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values == null) return;
        foreach (KeyValuePair<string, string> pair in values) _values[pair.Key] = pair.Value;
    }

    /// <summary>The screen the link addressed.</summary>
    public string ScreenName { get; }

    public IReadOnlyDictionary<string, string> Values => _values;

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out string? value) ? value : null;

    public string GetString(string key, string fallback = "")
        => _values.TryGetValue(key, out string? value) ? value : fallback;

    public int GetInt(string key, int fallback = 0)
        => _values.TryGetValue(key, out string? value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    public float GetFloat(string key, float fallback = 0f)
        => _values.TryGetValue(key, out string? value)
            && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : fallback;

    public bool GetBool(string key, bool fallback = false)
        => _values.TryGetValue(key, out string? value)
            ? string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            : fallback;

    /// <summary>Rebuilds the link this query came from. Used by history and by logging.</summary>
    public string ToLink(string scheme = XuiLink.DefaultScheme)
    {
        var builder = new StringBuilder(scheme).Append("://").Append(ScreenName);
        bool first = true;
        foreach (KeyValuePair<string, string> pair in _values)
        {
            builder.Append(first ? '?' : '&').Append(Uri.EscapeDataString(pair.Key))
                .Append('=').Append(Uri.EscapeDataString(pair.Value));
            first = false;
        }
        return builder.ToString();
    }

    public override string ToString() => ToLink();

    internal static XuiRouteQuery ForScreen(string screenName) => new(screenName, Empty);
}
