using System.Diagnostics;
using System.Text.RegularExpressions;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 — Regex content search across files. The offline complement to <see cref="CodeIndexService"/>
/// (which finds symbol DECLARATIONS via Roslyn): this finds any text anywhere in a file's content —
/// usages, comments, string literals, config values. Pure <see cref="Regex"/> + line scan, no Roslyn
/// dependency, so it works on any text file (.cs, .shader, .mat, .scene, .json, ...).
///
/// <para>Glob handling: a simple translator from a shell-style glob (<c>Assets/Scripts/**/*.cs</c>) to
/// a regex on the project-relative path. Supports <c>*</c> (any non-separator), <c>**</c> (any path),
/// and <c>?</c> (single char). When the pattern looks like an extension (<c>.cs</c>), it's treated as
/// a suffix filter.</para>
/// </summary>
internal static class FileSearcher
{
    private const int MaxMatches = 500;
    private const int MaxExcerptLength = 300;
    private const int MaxFilesScannedReport = 100_000; // safety cap to avoid pathological enumerations

    /// <summary>Search file contents for <paramref name="pattern"/> (regex). Scope is either a single
    /// file (<paramref name="path"/>) or a glob under the project's Assets root
    /// (<paramref name="glob"/>, e.g. <c>**/*.cs</c> or <c>Scripts/**/*.cs</c>).</summary>
    public static FileSearchResult Search(
        string assetsRoot,
        string pattern,
        string? path = null,
        string? glob = null,
        int limit = 100)
    {
        if (string.IsNullOrEmpty(assetsRoot) || !Directory.Exists(assetsRoot))
        {
            return new FileSearchResult(pattern, path ?? glob ?? "", 0, Array.Empty<FileSearchMatch>(), false);
        }

        var regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        int cap = Math.Clamp(limit, 1, MaxMatches);
        var matches = new List<FileSearchMatch>(cap);
        int filesScanned = 0;
        bool truncated = false;

        IEnumerable<string> files = ResolveFiles(assetsRoot, path, glob);
        foreach (string file in files)
        {
            if (filesScanned >= MaxFilesScannedReport) break;
            filesScanned++;

            string relative = Path.GetRelativePath(assetsRoot, file).Replace('\\', '/');
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue; // binary / locked / unreadable — skip
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                string excerpt = lines[i].Trim();
                if (excerpt.Length > MaxExcerptLength) excerpt = excerpt[..MaxExcerptLength] + "…";
                if (matches.Count < cap)
                {
                    matches.Add(new FileSearchMatch(relative, i + 1, excerpt));
                }
                else
                {
                    truncated = true;
                    goto done; // hit the page cap; stop early
                }
            }
        }

    done:
        string scope = !string.IsNullOrEmpty(path) ? path! : (glob ?? "**/*");
        return new FileSearchResult(pattern, scope, filesScanned, matches, truncated);
    }

    /// <summary>Resolve the file set to scan: a single file (path), or a glob enumeration under
    /// assetsRoot. Returns absolute paths.</summary>
    private static IEnumerable<string> ResolveFiles(string assetsRoot, string? path, string? glob)
    {
        // Single-file mode.
        if (!string.IsNullOrEmpty(path))
        {
            string abs = Path.IsPathRooted(path)
                ? path
                : Path.Combine(assetsRoot, path);
            return File.Exists(abs) ? new[] { abs } : Array.Empty<string>();
        }

        // Glob mode. Default to "all text files" when neither path nor glob is given.
        string g = string.IsNullOrEmpty(glob) ? "**/*" : glob!;

        // A bare extension like ".cs" → treat as a suffix filter (most common case).
        if (g.StartsWith('.') && !g.Contains('*'))
        {
            return EnumerateSafe(assetsRoot, "*" + g);
        }

        // Optimization: if the glob is a simple "**/*.<ext>", use the enumeration filter directly
        // (faster than regex-matching every path).
        string? enumFilter = TrySimpleEnumFilter(g);
        if (enumFilter is not null)
        {
            return EnumerateSafe(assetsRoot, enumFilter);
        }

        // Otherwise translate the glob to a path regex and filter the full enumeration.
        var pathRegex = GlobToRegex(g);
        return EnumerateSafe(assetsRoot, "*.*")
            .Where(f => pathRegex.IsMatch(Path.GetRelativePath(assetsRoot, f).Replace('\\', '/')));
    }

    private static IEnumerable<string> EnumerateSafe(string root, string filter)
    {
        string[] all;
        try
        {
            all = Directory.EnumerateFiles(root, filter, SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            yield break;
        }
        // Skip Library/.xengine (engine caches, not source) — mirror AssetReader.EnumerateAssets.
        foreach (string p in all)
        {
            string rel = Path.GetRelativePath(root, p).Replace('\\', '/');
            if (rel.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith(".xengine/", StringComparison.OrdinalIgnoreCase)) continue;
            yield return p;
        }
    }

    /// <summary>If the glob is a simple "<c>**/*.&lt;ext&gt;</c>" or "<c>*.&lt;ext&gt;</c>", return the
    /// enumeration filter ("<c>*.&lt;ext&gt;</c>") so we use the fast Directory.EnumerateFiles path.</summary>
    private static string? TrySimpleEnumFilter(string glob)
    {
        // Normalize: strip leading "**/" or "./"
        string g = glob.Replace('\\', '/');
        if (g.StartsWith("**/")) g = g[3..];
        else if (g.StartsWith("./")) g = g[2..];

        // Match patterns like "*.cs", "*.shader", "*.mat" — "*.ext" with no further wildcards.
        int dot = g.LastIndexOf('.');
        if (dot <= 0 || !g.StartsWith("*.") || g.IndexOf('*', 2) >= 0) return null;
        return g;
    }

    /// <summary>Translate a shell glob to a regex that matches a forward-slash project-relative path.</summary>
    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('^');
        int i = 0;
        string g = glob.Replace('\\', '/');
        while (i < g.Length)
        {
            // ** matches any path segment(s) including separators
            if (i + 1 < g.Length && g[i] == '*' && g[i + 1] == '*')
            {
                sb.Append(".*");
                i += 2;
                // consume an optional following slash so "**/x" works
                if (i < g.Length && g[i] == '/') i++;
            }
            else if (g[i] == '*')
            {
                sb.Append("[^/]*"); // single segment wildcard
                i++;
            }
            else if (g[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if ("+()|^$.{}[]".IndexOf(g[i]) >= 0)
            {
                sb.Append('\\').Append(g[i]); // regex-special → escape
                i++;
            }
            else
            {
                sb.Append(g[i]);
                i++;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
