using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace XHub.Services;

/// <summary>
/// Result of normalizing a user-supplied project location (planning doc §3.1):
/// <see cref="RootPath"/> is the canonical project folder, <see cref="Name"/> the display name
/// when one could be read, and <see cref="XEngineFiles"/> every marker file found at the root
/// (more than one = ambiguous identity, the user must pick).
/// </summary>
public sealed class ProjectLocation
{
    public string RootPath { get; init; } = "";
    public string? Name { get; init; }
    public IReadOnlyList<string> XEngineFiles { get; init; } = [];
    public bool DirectoryExists { get; init; }
    public bool HasAssets { get; init; }
}

/// <summary>
/// Pure path utilities for importing, deduplicating and repairing project records.
/// All comparisons use the OS-appropriate case sensitivity (Windows paths are case-insensitive).
/// </summary>
public static class ProjectPath
{
    /// <summary>Case sensitivity used for path identity on the current OS.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Normalizes user input into a project location. Accepts a project folder or a
    /// <c>.xengine</c> file (which resolves to its parent directory). Strips quotes, expands
    /// environment variables, collapses trailing separators, and makes relative paths absolute.
    /// Never throws for bad input — the returned <see cref="ProjectLocation"/> simply reports
    /// what exists.
    /// </summary>
    public static ProjectLocation Normalize(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return new ProjectLocation();

        string input = rawInput.Trim().Trim('"').Trim();
        if (input.Length == 0)
            return new ProjectLocation();

        // Expand %VAR% / ${VAR} so pasted paths from logs and shells just work.
        input = Environment.ExpandEnvironmentVariables(input);

        string root;
        if (input.EndsWith(".xengine", StringComparison.OrdinalIgnoreCase) && File.Exists(input))
        {
            root = Path.GetDirectoryName(Path.GetFullPath(input)) ?? input;
        }
        else
        {
            if (!Path.IsPathRooted(input))
                input = Path.Combine(Directory.GetCurrentDirectory(), input);
            try { root = Path.GetFullPath(input); }
            catch { return new ProjectLocation(); }
        }

        while (root.Length > 3 && (root.EndsWith('\\') || root.EndsWith('/')))
            root = root[..^1];

        bool dirExists = Directory.Exists(root);
        bool hasAssets = dirExists && Directory.Exists(Path.Combine(root, "Assets"));

        List<string> xengineFiles = [];
        string? name = null;
        if (dirExists)
        {
            try
            {
                foreach (string file in Directory.GetFiles(root, "*.xengine"))
                    xengineFiles.Add(file);
            }
            catch { /* unreadable directory: treat as no marker files */ }
            if (xengineFiles.Count > 0)
                name = ReadName(xengineFiles[0]);
        }

        return new ProjectLocation
        {
            RootPath = root,
            Name = name ?? (dirExists ? System.IO.Path.GetFileName(root) : null),
            XEngineFiles = xengineFiles,
            DirectoryExists = dirExists,
            HasAssets = hasAssets,
        };
    }

    /// <summary>Reads the <c>name</c> field from a .xengine marker file, if present.</summary>
    public static string? ReadName(string xengineFile)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(xengineFile));
            return doc.RootElement.TryGetProperty("name", out JsonElement name)
                && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads <c>ProjectSettings/ProjectVersion.txt</c> (m_EditorVersion: …), or null.</summary>
    public static string? ReadProjectVersion(string projectRoot)
    {
        try
        {
            string file = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(file)) return null;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                {
                    string value = line["m_EditorVersion:".Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
        }
        catch { /* unreadable stamp reads as unknown */ }
        return null;
    }

    /// <summary>True when both strings denote the same project root.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(a, b, PathComparison);
}
