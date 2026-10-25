using System;
using System.IO;

namespace XEngine.Editor.Projects;

/// <summary>
/// Per-project engine version stamp, Unity-parity: <c>ProjectSettings/ProjectVersion.txt</c>
/// containing <c>m_EditorVersion: &lt;version&gt;</c>. Written at project creation and after an
/// upgrade-confirmed open; read before opening so a version mismatch can raise the upgrade prompt.
/// </summary>
public static class ProjectEngineVersion
{
    public const string FileName = "ProjectVersion.txt";
    private const string Marker = "m_EditorVersion:";

    public static string PathFor(string projectRoot)
        => Path.Combine(projectRoot, "ProjectSettings", FileName);

    /// <summary>Reads the stamped version, or null when the file is missing/unparsable.</summary>
    public static string? Read(string projectRoot)
    {
        try
        {
            string path = PathFor(projectRoot);
            if (!File.Exists(path)) return null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith(Marker, StringComparison.Ordinal))
                {
                    string value = line[Marker.Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Canonical record format: semantic version "1.0.0-a1" (XHub plan §4.2 binding decision).
    /// Converts the display form "V1.0.0a1" and passes already-canonical input through; build
    /// metadata ("+…") is stripped. New stamps are always written in this form — legacy
    /// "V1.0.0a1" stamps are compared through this converter so old projects still match.
    /// </summary>
    public static string Canonicalize(string version)
    {
        string v = version.Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        if (v.Length >= 2 && (v[0] is 'V' or 'v') && char.IsAsciiDigit(v[1]))
            v = v[1..];
        if (v.Contains('-'))
            return v; // already semantic ("1.0.0-a1")

        // Unity style: trailing letters+digits on a numeric core ("1.0.0a1") → "1.0.0-a1".
        int i = v.Length;
        while (i > 0 && char.IsAsciiDigit(v[i - 1])) i--;
        int digitsEnd = i;
        while (i > 0 && char.IsAsciiLetter(v[i - 1])) i--;
        if (digitsEnd > i && i > 0 && char.IsAsciiDigit(v[i - 1]))
            return $"{v[..i]}-{v[i..]}";
        return v;
    }

    public static void Write(string projectRoot, string version)
    {
        string path = PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{Marker} {Canonicalize(version)}{Environment.NewLine}");
    }

    /// <summary>True when the stamp equals the running engine version. Both sides are compared
    /// in canonical form, so legacy "V1.0.0a1" stamps keep matching the same release.</summary>
    public static bool Matches(string projectRoot, string engineVersion)
    {
        string? stamped = Read(projectRoot);
        if (stamped == null) return false;
        return string.Equals(
            Canonicalize(stamped),
            Canonicalize(engineVersion),
            StringComparison.OrdinalIgnoreCase);
    }
}
