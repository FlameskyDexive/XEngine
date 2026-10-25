// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// A version range parsed from a manifest/package.json specifier. Supports the forms used by
/// PackageManager resolution:
/// <list type="bullet">
/// <item><c>1.2.3</c> — caret range (<c>&gt;=1.2.3 &lt;2.0.0</c>, or <c>&lt;1.3.0</c> when major is 0),
/// matching npm/UPM "compatible version" semantics for a dependency lower bound.</item>
/// <item><c>~1.2.3</c> — tilde range (<c>&gt;=1.2.3 &lt;1.3.0</c>).</item>
/// <item><c>^1.2.3</c> — explicit caret range (same as bare <c>1.2.3</c>).</item>
/// <item><c>1.2.x</c> / <c>1.x</c> / <c>*</c> — wildcard ranges.</item>
/// <item><c>&gt;=1.2.0</c>, <c>&gt;1.2.0</c>, <c>&lt;2.0.0</c>, <c>&lt;=1.9.0</c>, <c>=1.2.3</c> — comparators.</item>
/// </list>
/// Pin forms (<c>file:</c>, <c>embed:</c>, <c>git@</c>) are handled by the source resolver, not here.
/// </summary>
public readonly struct VersionRange
{
    private enum Kind { Caret, Tilde, Wildcard, ComparatorAny, Any }

    private readonly Kind _kind;
    private readonly SemanticVersion _anchor;
    private readonly string _raw;

    private VersionRange(Kind kind, SemanticVersion anchor, string raw)
    {
        _kind = kind; _anchor = anchor; _raw = raw;
    }

    /// <summary>A range that matches any version (used for embed roots and unspecifiable fallbacks).</summary>
    public static VersionRange Any { get; } = new(Kind.Any, default, "*");

    public string Raw => _raw;

    /// <summary>Parse a specifier into a range. Returns null for non-version specifiers
    /// (file:/embed:/git:), which the source resolver handles separately.</summary>
    public static VersionRange? TryParse(string specifier)
    {
        var s = (specifier ?? "").Trim();
        if (s.Length == 0) return null;
        // Non-version source specifiers — leave to the resolver.
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("embed:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            return null;

        // "*" matches anything.
        if (s == "*" || s == "x" || s == "X") return new VersionRange(Kind.Wildcard, default, s);

        // Wildcard ranges: 1.2.x, 1.x, 1.2.* — treat as tilde-like at the most-specific boundary.
        if (s.Contains('x') || s.Contains('X') || s.Contains('*'))
        {
            // Normalize wildcards to a concrete anchor at the highest non-wildcard boundary, then
            // treat as tilde of that precision. e.g. "1.2.x" -> ~1.2.0; "1.x" -> ~1.0.0; "1.*" -> ~1.0.0
            var cleaned = s.Replace('*', 'x').Replace('X', 'x');
            var parts = cleaned.Split('.');
            // Replace x-parts with 0, remember the precision.
            int specificity = 0;
            for (int i = 0; i < parts.Length && i < 3; i++)
            {
                if (parts[i] == "x") { parts[i] = "0"; break; }
                specificity = i + 1;
            }
            if (!SemanticVersion.TryParse(string.Join('.', parts[0], parts.Length > 1 ? parts[1] : "0", parts.Length > 2 ? parts[2] : "0"), out var v))
                return null;
            // Lower specificity (just major) acts like caret; two-or-more like tilde.
            return specificity <= 1
                ? new VersionRange(Kind.Caret, v, s)
                : new VersionRange(Kind.Tilde, v, s);
        }

        // Tilde / caret.
        if (s.StartsWith('~')) return SemanticVersion.TryParse(s[1..], out var tv) ? new VersionRange(Kind.Tilde, tv, s) : null;
        if (s.StartsWith('^')) return SemanticVersion.TryParse(s[1..], out var cv) ? new VersionRange(Kind.Caret, cv, s) : null;

        // Comparators: >=, >, <=, <, =
        if (s.StartsWith(">=") || s.StartsWith("<=") || s.StartsWith("=="))
        {
            var op = s[..2];
            return SemanticVersion.TryParse(s[2..], out var v)
                ? new VersionRange(ComparatorOp(op), v, s) : null;
        }
        if (s.StartsWith('>') || s.StartsWith('<') || s.StartsWith('='))
        {
            var op = s[..1] == "=" ? "==" : s[..1];
            return SemanticVersion.TryParse(s[1..], out var v)
                ? new VersionRange(ComparatorOp(op), v, s) : null;
        }

        // Bare version -> caret (npm/UPM default).
        return SemanticVersion.TryParse(s, out var bare) ? new VersionRange(Kind.Caret, bare, s) : null;

        static Kind ComparatorOp(string op) => op switch
        {
            ">=" => Kind.ComparatorAny, // encoded with anchor; we special-case below by raw prefix
            "<=" => Kind.ComparatorAny,
            "==" => Kind.ComparatorAny,
            ">" => Kind.ComparatorAny,
            "<" => Kind.ComparatorAny,
            _ => Kind.Caret,
        };
    }

    /// <summary>True when <paramref name="version"/> satisfies this range.</summary>
    public bool Satisfies(SemanticVersion version)
    {
        if (_kind == Kind.Any) return true;
        // Comparator forms are encoded by their raw prefix; decode here.
        if (_kind == Kind.ComparatorAny)
        {
            if (_raw.StartsWith(">=")) return version >= _anchor;
            if (_raw.StartsWith("<=")) return version <= _anchor;
            if (_raw.StartsWith('>')) return version > _anchor;
            if (_raw.StartsWith('<')) return version < _anchor;
            if (_raw.StartsWith('=') || _raw.StartsWith("==")) return version == _anchor;
            return false;
        }
        if (_kind == Kind.Wildcard) return true;
        if (_kind == Kind.Tilde)
        {
            // >=anchor, <(next minor). Same major+minor required (caret-on-minor).
            if (version < _anchor) return false;
            return version.Major == _anchor.Major && version.Minor == _anchor.Minor;
        }
        // Caret: >=anchor, <(next major); but for 0.x.y, cap at next minor (npm rule).
        if (version < _anchor) return false;
        if (_anchor.Major >= 1) return version.Major == _anchor.Major;
        // 0.x: allow same major (0) and same minor.
        return version.Major == _anchor.Major && version.Minor == _anchor.Minor;
    }

    public override string ToString() => _raw;
}
