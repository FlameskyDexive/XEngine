using XEngine.Ai.Protocol;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B1) — Offline asset metadata reader. Surfaces the GUID/importer/version from each
/// asset's <c>.meta</c> sidecar (EchoText, written by the editor's <c>MetaFile</c>). Engine-agnostic:
/// reads the sidecar directly, no <c>metadata.db</c> / Editor dependency. Lets an AI discover assets
/// and their stable GUIDs (the same GUIDs scene files reference).
/// </summary>
internal static class AssetReader
{
    /// <summary>List assets under Assets/, optionally filtered by name or extension. Reads .meta sidecars.
    /// ★ P0-3: capped at <c>limit</c> with <c>HasMore</c> when more assets match. Total reports the
    /// full match count so the AI knows how much was omitted.</summary>
    public static AssetList Search(string? assetsRoot, string? query = null, string? extension = null, int limit = 200)
    {
        if (string.IsNullOrEmpty(assetsRoot) || !Directory.Exists(assetsRoot))
        {
            return new AssetList(assetsRoot ?? "", 0, Array.Empty<AssetInfo>(), false);
        }

        // Collect all matches first (asset counts are small; the expensive part is .meta reads which
        // we skip on truncated tail). Then sort + truncate to compute HasMore accurately.
        var all = new List<AssetInfo>();
        foreach (string path in EnumerateAssets(assetsRoot))
        {
            string relative = Path.GetRelativePath(assetsRoot, path).Replace('\\', '/');
            if (extension is not null && !relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query is not null && relative.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            all.Add(ReadInfo(path, relative));
        }

        all.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
        int total = all.Count;
        int cap = Math.Clamp(limit, 1, 500);
        var page = total > cap ? all.Take(cap).ToList() : all;
        bool hasMore = page.Count < total;
        return new AssetList(assetsRoot!, total, page, hasMore);
    }

    /// <summary>Read a single asset's .meta detail (GUID/importer/version/settings keys).</summary>
    public static AssetDetail Inspect(string absolutePath, string relativePath)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Asset file not found: {absolutePath}");
        }

        string metaPath = absolutePath + ".meta";
        string guid = "";
        string importer = "";
        int importerVersion = 0;
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);

        if (File.Exists(metaPath))
        {
            EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
            if (meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String) guid = g.StringValue;
            if (meta.TryGet("importer", out EchoObject? im) && im!.TagType == EchoType.String) importer = im.StringValue;
            if (meta.TryGet("importerVersion", out EchoObject? v) && v!.TagType == EchoType.Int) importerVersion = v.IntValue;
            if (meta.TryGet("settings", out EchoObject? s) && s!.TagType == EchoType.Compound)
            {
                foreach (string key in s.GetNames())
                {
                    settings[key] = Describe(s.Get(key));
                }
            }
        }

        var info = new FileInfo(absolutePath);
        return new AssetDetail(relativePath, guid, importer, importerVersion, settings, info.Length, info.LastWriteTimeUtc);
    }

    private static AssetInfo ReadInfo(string absolutePath, string relativePath)
    {
        string metaPath = absolutePath + ".meta";
        string? guid = null;
        string? importer = null;
        int? version = null;
        bool hasMeta = File.Exists(metaPath);

        if (hasMeta)
        {
            EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
            if (meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String) guid = g.StringValue;
            if (meta.TryGet("importer", out EchoObject? im) && im!.TagType == EchoType.String) importer = im.StringValue;
            if (meta.TryGet("importerVersion", out EchoObject? v) && v!.TagType == EchoType.Int) version = v.IntValue;
        }

        var info = new FileInfo(absolutePath);
        return new AssetInfo(relativePath, guid, importer, version, info.Length, info.LastWriteTimeUtc, hasMeta);
    }

    private static IEnumerable<string> EnumerateAssets(string root)
    {
        // Skip Library/, .xengine/ etc. — only real source assets under Assets/.
        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (rel.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith(".xengine/", StringComparison.OrdinalIgnoreCase)) continue;
            yield return path;
        }
    }

    private static string Describe(EchoObject? v)
    {
        if (v is null) return "NULL";
        return v.TagType switch
        {
            EchoType.Null => "NULL",
            EchoType.Compound or EchoType.List => "...",
            _ => v.StringValue,
        };
    }
}
