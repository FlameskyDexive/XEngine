// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace XEngine.Runtime.Backends.WebGL2;

public enum WebGL2GlslHost
{
    DesktopCore,
    Browser,
}

public enum WebGL2GlslStage
{
    Vertex,
    Fragment,
}

/// <summary>
/// Rewrites Slang GLSL ES artifacts for the active WebGL2 host. Desktop Core 4.1 cannot
/// consume <c>#version 300 es</c> or precision qualifiers; WebGL2 cannot consume
/// <c>layout(binding=…)</c>.
/// </summary>
public static class WebGL2Glsl
{
    private static readonly Regex s_versionLine = new(
        @"^[ \t]*#[ \t]*version[ \t]+[^\r\n]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex s_precisionLine = new(
        @"^[ \t]*precision[ \t]+(?:highp|mediump|lowp)[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*;[ \t]*\r?\n?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex s_precisionQualifier = new(
        @"\b(?:highp|mediump|lowp)[ \t]+",
        RegexOptions.CultureInvariant);

    public static string Prepare(string source, WebGL2GlslStage stage, WebGL2GlslHost host)
    {
        ArgumentNullException.ThrowIfNull(source);

        string withoutBindings = StripBindingLayouts(source);
        return host == WebGL2GlslHost.Browser
            ? PrepareBrowser(withoutBindings, stage)
            : PrepareDesktop(withoutBindings);
    }

    internal static string StripBindingLayouts(string source)
    {
        var output = new StringBuilder(source.Length);
        int i = 0;
        while (i < source.Length)
        {
            if (TryMatchLayout(source, i, out int afterKeyword))
            {
                int paren = SkipWhitespace(source, afterKeyword);
                if (paren < source.Length && source[paren] == '(')
                {
                    int close = FindMatchingParen(source, paren);
                    if (close > paren)
                    {
                        string inner = source.Substring(paren + 1, close - paren - 1);
                        string stripped = StripSetAndBindingClauses(inner);
                        if (!string.IsNullOrWhiteSpace(stripped))
                        {
                            output.Append("layout(");
                            output.Append(stripped.Trim());
                            output.Append(')');
                        }

                        i = close + 1;
                        continue;
                    }
                }
            }

            output.Append(source[i]);
            i++;
        }

        return output.ToString();
    }

    private static string PrepareDesktop(string source)
    {
        string withoutPrecisionLines = s_precisionLine.Replace(source, string.Empty);
        string withoutQualifiers = s_precisionQualifier.Replace(withoutPrecisionLines, string.Empty);
        return ReplaceOrPrependVersion(withoutQualifiers, "#version 330");
    }

    private static string PrepareBrowser(string source, WebGL2GlslStage stage)
    {
        string versioned = ReplaceOrPrependVersion(source, "#version 300 es");
        if (stage != WebGL2GlslStage.Fragment)
            return versioned;

        if (versioned.Contains("precision highp float", StringComparison.Ordinal))
            return versioned;

        Match version = s_versionLine.Match(versioned);
        if (!version.Success)
            return "precision highp float;\n" + versioned;

        int insertAt = version.Index + version.Length;
        string suffix = versioned[insertAt..];
        string newline = suffix.StartsWith("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : suffix.StartsWith('\n') ? "\n" : "\n";
        string separator = suffix.StartsWith('\r') || suffix.StartsWith('\n') ? string.Empty : newline;
        return versioned[..insertAt] + newline + "precision highp float;" + separator + suffix;
    }

    private static string ReplaceOrPrependVersion(string source, string versionLine)
    {
        Match match = s_versionLine.Match(source);
        if (!match.Success)
            return versionLine + "\n" + source;

        return source[..match.Index] + versionLine + source[(match.Index + match.Length)..];
    }

    private static bool TryMatchLayout(string source, int index, out int afterKeyword)
    {
        afterKeyword = index;
        const string keyword = "layout";
        if (index + keyword.Length > source.Length)
            return false;
        if (index > 0 && IsIdentChar(source[index - 1]))
            return false;
        if (!source.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.Ordinal))
            return false;
        int next = index + keyword.Length;
        if (next < source.Length && IsIdentChar(source[next]))
            return false;
        afterKeyword = next;
        return true;
    }

    private static string StripSetAndBindingClauses(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner))
            return string.Empty;

        string[] parts = inner.Split(',');
        var kept = new StringBuilder(inner.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0)
                continue;
            if (IsSetOrBindingClause(part))
                continue;
            if (kept.Length > 0)
                kept.Append(", ");
            kept.Append(part);
        }

        return kept.ToString();
    }

    private static bool IsSetOrBindingClause(string part)
    {
        if (part.StartsWith("set", StringComparison.Ordinal) ||
            part.StartsWith("binding", StringComparison.Ordinal))
        {
            int equals = part.IndexOf('=');
            if (equals <= 0)
                return false;
            string name = part[..equals].Trim();
            return name is "set" or "binding";
        }

        return false;
    }

    private static int FindMatchingParen(string source, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '(')
                depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
            index++;
        return index;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
