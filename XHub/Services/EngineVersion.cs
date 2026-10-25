using System;
using System.Collections.Generic;
using System.Text;

namespace XHub.Services;

/// <summary>
/// Normalized engine version (planning doc §4.2). Accepts every existing editor expression —
/// the csproj <c>1.0.0-a1</c> semver form and the Unity-style <c>V1.0.0a1</c> informational
/// form (optionally with <c>+build</c> metadata) — and exposes a canonical string plus semantic
/// comparison. String sorting is never used for ordering.
/// </summary>
public sealed record EngineVersion : IComparable<EngineVersion?>
{
    public int Major { get; private init; }
    public int Minor { get; private init; }
    public int Patch { get; private init; }

    /// <summary>Normalized prerelease token ("a1", "b2", "rc1", "alpha.1" → mapped form), or "" for stable.</summary>
    public string Prerelease { get; private init; } = "";

    /// <summary>The version text exactly as supplied (display form, e.g. "V1.0.0a1").</summary>
    public string Display { get; private init; } = "";

    public bool IsPreview => Prerelease.Length > 0;

    public string Channel => IsPreview ? "preview" : "stable";

    public string Canonical =>
        Prerelease.Length > 0 ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    public override string ToString() => Display;

    public static bool TryParse(string? text, out EngineVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string input = text.Trim();
        if (input.Length >= 2 && (input[0] is 'v' or 'V') && char.IsAsciiDigit(input[1]))
            input = input[1..]; // "V1.0.0a1" → "1.0.0a1"

        int plus = input.IndexOf('+');
        if (plus >= 0) input = input[..plus]; // build metadata never affects ordering

        string corePart;
        string prerelease = "";
        int dash = input.IndexOf('-');
        if (dash >= 0)
        {
            corePart = input[..dash];
            prerelease = input[(dash + 1)..].Trim();
            if (prerelease.Length == 0) return false;
        }
        else
        {
            corePart = input;
        }

        string[] segments = corePart.Split('.');
        if (segments.Length is < 1 or > 3) return false;

        int major = 0, minor = 0, patch = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            bool isLast = i == segments.Length - 1;
            if (!TryParseNumericSegment(segments[i].Trim(), isLast, out int number, out string? unitySuffix))
                return false;

            switch (i)
            {
                case 0: major = number; break;
                case 1: minor = number; break;
                case 2: patch = number; break;
            }
            if (unitySuffix != null)
                prerelease = unitySuffix; // Unity style: "1.0.0a1" / "1.0a1" — patch stays 0 when unwritten
        }

        if (!TryNormalizePrerelease(prerelease, out string normalized))
            return false;

        version = new EngineVersion
        {
            Major = major,
            Minor = minor,
            Patch = patch,
            Prerelease = normalized,
            Display = text.Trim(),
        };
        return true;
    }

    /// <summary>Parses "12", "0a1" → (0, "a1"), "12b2" → (12, "b2"); rejects non-numeric junk.</summary>
    private static bool TryParseNumericSegment(string segment, bool allowSuffix, out int number, out string? suffix)
    {
        number = 0;
        suffix = null;
        if (segment.Length == 0) return false;

        int i = 0;
        while (i < segment.Length && char.IsAsciiDigit(segment[i])) i++;
        if (i == 0) return false;
        if (!int.TryParse(segment[..i], out number)) return false;

        if (i == segment.Length) return true;
        if (!allowSuffix) return false;
        string rest = segment[i..];
        // Unity prerelease: letters immediately followed by an optional number ("a1", "b12", "rc1").
        int j = 0;
        while (j < rest.Length && char.IsAsciiLetter(rest[j])) j++;
        if (j == 0) return false;
        while (j < rest.Length && char.IsAsciiDigit(rest[j])) j++;
        if (j != rest.Length) return false;
        suffix = rest;
        return true;
    }

    /// <summary>Maps "alpha"→"a", "beta"→"b"; lowercases; rejects empty tokens.</summary>
    private static bool TryNormalizePrerelease(string prerelease, out string normalized)
    {
        if (prerelease.Length == 0)
        {
            normalized = "";
            return true; // stable release
        }

        var builder = new StringBuilder();
        string[] tokens = prerelease.Split('.');
        var mapped = new List<string>(tokens.Length);
        foreach (string rawToken in tokens)
        {
            string token = rawToken.Trim().ToLowerInvariant();
            if (token.Length == 0) { normalized = ""; return false; }
            mapped.Add(token switch
            {
                "alpha" => "a",
                "beta" => "b",
                _ => token,
            });
        }
        foreach (string token in mapped)
        {
            if (builder.Length > 0) builder.Append('.');
            builder.Append(token);
        }
        normalized = builder.ToString();
        return true;
    }

    public int CompareTo(EngineVersion? other)
    {
        if (other is null) return 1;
        int byCore = Major.CompareTo(other.Major);
        if (byCore != 0) return byCore;
        byCore = Minor.CompareTo(other.Minor);
        if (byCore != 0) return byCore;
        byCore = Patch.CompareTo(other.Patch);
        if (byCore != 0) return byCore;

        // Semver precedence: a prerelease sorts BELOW the release of the same core.
        if (Prerelease.Length == 0 && other.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1;
        if (other.Prerelease.Length == 0) return -1;

        string[] mine = Prerelease.Split('.');
        string[] theirs = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Max(mine.Length, theirs.Length); i++)
        {
            string a = i < mine.Length ? mine[i] : "";
            string b = i < theirs.Length ? theirs[i] : "";
            int byToken = ComparePrereleaseToken(a, b);
            if (byToken != 0) return byToken;
        }
        return 0;
    }

    private static int ComparePrereleaseToken(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 0;
        bool aNumeric = IsNumeric(a);
        bool bNumeric = IsNumeric(b);
        if (aNumeric && bNumeric)
            return int.Parse(a).CompareTo(int.Parse(b));
        if (aNumeric) return -1; // numeric identifiers sort below alphanumeric
        if (bNumeric) return 1;
        (char LetterA, int NumA) = SplitLetterToken(a);
        (char LetterB, int NumB) = SplitLetterToken(b);
        int byLetter = PrereleaseRank(LetterA).CompareTo(PrereleaseRank(LetterB));
        if (byLetter != 0) return byLetter;
        if (LetterA != LetterB) return LetterA.CompareTo(LetterB);
        return NumA.CompareTo(NumB);
    }

    private static bool IsNumeric(string token)
    {
        foreach (char c in token)
            if (!char.IsAsciiDigit(c)) return false;
        return token.Length > 0;
    }

    private static (char Letter, int Number) SplitLetterToken(string token)
    {
        int i = 0;
        while (i < token.Length && char.IsAsciiLetter(token[i])) i++;
        int number = i < token.Length && int.TryParse(token[i..], out int n) ? n : 0;
        return (token.Length > 0 ? token[0] : '\0', number);
    }

    /// <summary>Channel ordering for letter prereleases: a(lpha) &lt; b(eta) &lt; rc; unknown letters follow.</summary>
    private static int PrereleaseRank(char letter) => letter switch
    {
        'a' => 1,
        'b' => 2,
        'c' or 'r' => 3, // "rc1" / legacy "c"
        _ => 4,
    };

    /// <summary>True when <paramref name="project"/> was stamped by a NEWER editor than <paramref name="candidate"/> (a downgrade open).</summary>
    public static bool IsDowngrade(EngineVersion project, EngineVersion candidate) =>
        candidate.CompareTo(project) < 0;
}
