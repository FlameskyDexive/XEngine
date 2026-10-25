// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Editor.Projects.Packages;

/// <summary>
/// A parsed semantic version (major.minor.patch + optional pre-release/build metadata). Implements
/// the SemVer 2.0.0 precedence rules enough for package resolution: exact equality, &gt;=/&gt;/^/~ ranges,
/// and pre-release handling (a pre-release of a given x.y.z is lower than the release of the same).
/// </summary>
public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>Pre-release identifiers (e.g. ["alpha","1"]) or empty for a release. Null = release.</summary>
    public string[]? PreRelease { get; }
    /// <summary>Build metadata (ignored for precedence, per SemVer 2.0.0 §10).</summary>
    public string[]? Build { get; }

    public bool IsPreRelease => PreRelease is { Length: > 0 };

    public SemanticVersion(int major, int minor, int patch, string[]? preRelease = null, string[]? build = null)
    {
        Major = major; Minor = minor; Patch = patch; PreRelease = preRelease; Build = build;
    }

    public static SemanticVersion Parse(string input)
    {
        if (TryParse(input, out var v)) return v;
        throw new FormatException($"'{input}' is not a valid semantic version.");
    }

    public static bool TryParse(string input, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        // Strip leading v (npm convention) and any leading comparator/operator — callers pass clean versions,
        // but be lenient: "v1.2.3", "^1.2.3", ">=1.2.3" all parse to 1.2.3.
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        while (s.Length > 0 && (s[0] == '^' || s[0] == '~' || s[0] == '=' || s[0] == '>' || s[0] == '<'))
            s = s[1..];
        s = s.TrimStart();

        // Split off build metadata (+) then pre-release (-).
        string[]? build = null, pre = null;
        int plus = s.IndexOf('+');
        if (plus >= 0)
        {
            var b = s[(plus + 1)..];
            build = string.IsNullOrEmpty(b) ? null : b.Split('.');
            s = s[..plus];
        }
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var p = s[(dash + 1)..];
            pre = string.IsNullOrEmpty(p) ? null : p.Split('.');
            s = s[..dash];
        }

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        if (!int.TryParse(parts[0], out var major) || major < 0) return false;
        int minor = 0, patch = 0;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out minor) || minor < 0)) return false;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out patch) || patch < 0)) return false;

        version = new SemanticVersion(major, minor, patch, pre, build);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        // Major.Minor.Patch numeric precedence.
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // Pre-release has lower precedence than release (a release has none).
        bool a = IsPreRelease, b = other.IsPreRelease;
        if (!a && b) return 1;
        if (a && !b) return -1;
        if (!a && !b) return 0;

        // Both pre-release: compare identifiers per SemVer 2.0.0 §11.
        var pa = PreRelease!;
        var pb = other.PreRelease!;
        int len = Math.Min(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int c = CompareIdentifiers(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    private static int CompareIdentifiers(string a, string b)
    {
        bool aNum = int.TryParse(a, out var an);
        bool bNum = int.TryParse(b, out var bn);
        if (aNum && bNum) return an.CompareTo(bn);
        if (aNum) return -1;    // numeric < non-numeric
        if (bNum) return 1;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(SemanticVersion other)
        => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, IsPreRelease);

    public static bool operator ==(SemanticVersion a, SemanticVersion b) => a.Equals(b);
    public static bool operator !=(SemanticVersion a, SemanticVersion b) => !a.Equals(b);
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString()
    {
        var s = $"{Major}.{Minor}.{Patch}";
        if (IsPreRelease) s += "-" + string.Join(".", PreRelease!);
        if (Build is { Length: > 0 }) s += "+" + string.Join(".", Build);
        return s;
    }
}
