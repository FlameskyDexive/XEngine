using Spine;

using XEngine.Ai.Gateway;
using XEngine.Spine;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ M10 — Layout-preserving Spine page restyle on gen_fetch.
/// <c>kind=texture</c> + extra <c>atlasPath</c>: mock-tint the source page, or pack-crop a
/// provider crop whose size matches the unrotated region.
/// </summary>
internal static class SpineAtlasRestyleService
{
    public const string ExtraAtlasPath = "atlasPath";
    public const string ExtraRegion = "region";
    public const string ExtraPageName = "pageName";
    public const string ExtraSourcePagePath = "sourcePagePath";
    public const string ExtraMode = "mode";
    public const string ExtraTintR = "tintR";
    public const string ExtraTintG = "tintG";
    public const string ExtraTintB = "tintB";
    public const string ExtraPma = "pma";
    public const string ExtraInputImagePath = "inputImagePath";

    public static bool IsRequested(GenRecipe recipe)
        => recipe.Kind.Equals(GenRecipe.Kinds.Texture, StringComparison.OrdinalIgnoreCase)
           && recipe.Extra is not null
           && recipe.Extra.TryGetValue(ExtraAtlasPath, out string? path)
           && !string.IsNullOrWhiteSpace(path);

    /// <summary>Copy extra and fill width/height from the unrotated region when missing.</summary>
    public static IReadOnlyDictionary<string, string> Enrich(IReadOnlyDictionary<string, string> extra, string projectRoot)
    {
        var copy = new Dictionary<string, string>(extra, StringComparer.Ordinal);
        if (!copy.TryGetValue(ExtraAtlasPath, out string? atlasPath) || string.IsNullOrWhiteSpace(atlasPath))
            return copy;

        string regionName = copy.TryGetValue(ExtraRegion, out string? region) ? region : "";
        if (string.IsNullOrWhiteSpace(regionName) || regionName == "*")
            return copy;

        if (regionName.Contains(',', StringComparison.Ordinal))
            return copy;

        try
        {
            Atlas atlas = LoadAtlas(ResolveExisting(projectRoot, atlasPath));
            AtlasRegion? found = atlas.FindRegion(regionName.Trim());
            if (found == null)
                return copy;

            UnrotatedSize(found, out int w, out int h);
            if (!copy.ContainsKey("width"))
                copy["width"] = w.ToString();
            if (!copy.ContainsKey("height"))
                copy["height"] = h.ToString();
        }
        catch (Exception)
        {
            // Submit still records the extra; fetch will fail with a clear path error.
        }

        return copy;
    }

    /// <summary>ComfyUI / OpenAI img2img: write the unrotated region PNG and set
    /// <c>inputImagePath</c>. No-op when the path is already set, or the region is
    /// missing / <c>*</c> / a comma list.</summary>
    public static IReadOnlyDictionary<string, string> AttachInputCrop(
        IReadOnlyDictionary<string, string> extra, string projectRoot)
    {
        var copy = extra as Dictionary<string, string>
            ?? new Dictionary<string, string>(extra, StringComparer.Ordinal);
        if (copy.TryGetValue(ExtraInputImagePath, out string? existing) && !string.IsNullOrWhiteSpace(existing))
            return copy;

        if (!copy.TryGetValue(ExtraAtlasPath, out string? atlasPath) || string.IsNullOrWhiteSpace(atlasPath))
            return copy;

        if (!copy.TryGetValue(ExtraRegion, out string? regionName)
            || string.IsNullOrWhiteSpace(regionName)
            || regionName == "*"
            || regionName.Contains(',', StringComparison.Ordinal))
            return copy;

        try
        {
            Atlas atlas = LoadAtlas(ResolveExisting(projectRoot, atlasPath));
            AtlasRegion? found = atlas.FindRegion(regionName.Trim());
            if (found == null)
                return copy;

            AtlasPage page = found.page;
            string sourcePagePath = copy.TryGetValue(ExtraSourcePagePath, out string? overridePage)
                && !string.IsNullOrWhiteSpace(overridePage)
                ? ResolveExisting(projectRoot, overridePage)
                : ResolvePagePng(atlasPath, page.name, projectRoot);
            byte[] pageRgba = PngRgba.Decode(File.ReadAllBytes(sourcePagePath), out int pageW, out int pageH);
            byte[] crop = SpineAtlasRestyle.ExtractUnrotated(pageRgba, pageW, pageH, found);
            UnrotatedSize(found, out int w, out int h);

            string slug = SanitizeFileToken(regionName.Trim());
            string stamp = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(atlasPath + "\n" + regionName)))[..8];
            string relative = Path.Combine(".xengine", "gen-crops", slug + "-" + stamp + ".png").Replace('\\', '/');
            string absolute = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllBytes(absolute, PngRgba.Encode(crop, w, h));
            copy[ExtraInputImagePath] = relative;
        }
        catch (Exception)
        {
            // Submit still records the extra; the provider will fail with a path/upload error.
        }

        return copy;
    }

    public static bool ProviderUsesInputCrop(string provider)
        => provider.Equals(ComfyUiGenProvider.ProviderName, StringComparison.OrdinalIgnoreCase)
           || provider.Equals(OpenAiImageGenProvider.ProviderName, StringComparison.OrdinalIgnoreCase);

    public static void ApplyToArtifact(GenRecipe recipe, string projectRoot, string artifactPath)
    {
        IReadOnlyDictionary<string, string> extra = recipe.Extra
            ?? throw new InvalidOperationException("Atlas restyle requires extra.atlasPath.");
        string atlasPath = extra[ExtraAtlasPath];
        Atlas atlas = LoadAtlas(ResolveExisting(projectRoot, atlasPath));
        AtlasPage page = SelectPage(atlas, extra);
        string sourcePagePath = extra.TryGetValue(ExtraSourcePagePath, out string? overridePage) && !string.IsNullOrWhiteSpace(overridePage)
            ? ResolveExisting(projectRoot, overridePage)
            : ResolvePagePng(atlasPath, page.name, projectRoot);
        byte[] pageRgba = PngRgba.Decode(File.ReadAllBytes(sourcePagePath), out int pageW, out int pageH);
        if (page.width > 0 && page.height > 0 && (pageW != page.width || pageH != page.height))
            throw new InvalidDataException(
                $"Source page {pageW}x{pageH} does not match atlas size {page.width}x{page.height}.");

        List<AtlasRegion> regions = SelectRegions(atlas, page, extra);
        if (regions.Count == 0)
            throw new InvalidOperationException("No atlas regions matched extra.region.");

        string mode = extra.TryGetValue(ExtraMode, out string? modeValue) ? modeValue.Trim() : "auto";
        bool pma = extra.TryGetValue(ExtraPma, out string? pmaValue)
            ? pmaValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            : page.pma;

        byte[]? artifactPng = File.Exists(artifactPath) ? File.ReadAllBytes(artifactPath) : null;
        int artW = 0, artH = 0;
        byte[]? artifactRgba = null;
        if (artifactPng is { Length: > 8 })
        {
            try
            {
                artifactRgba = PngRgba.Decode(artifactPng, out artW, out artH);
            }
            catch (InvalidDataException)
            {
                artifactRgba = null;
            }
        }

        bool looksLikePage = artifactRgba != null && artW == pageW && artH == pageH;
        bool packCrop = mode.Equals("pack-crop", StringComparison.OrdinalIgnoreCase)
            || (mode.Equals("auto", StringComparison.OrdinalIgnoreCase)
                && regions.Count == 1
                && artifactRgba != null
                && (MatchesUnrotated(regions[0], artW, artH) || !looksLikePage));

        if (packCrop)
        {
            if (artifactRgba == null || regions.Count != 1)
                throw new InvalidOperationException("pack-crop needs one region and a crop PNG.");
            UnrotatedSize(regions[0], out int targetW, out int targetH);
            if (artW != targetW || artH != targetH)
                artifactRgba = PngRgba.Resize(artifactRgba, artW, artH, targetW, targetH);
            if (pma)
                SpineAtlasRestyle.Premultiply(artifactRgba);
            SpineAtlasRestyle.BlitUnrotated(pageRgba, pageW, pageH, regions[0], artifactRgba);
        }
        else
        {
            byte r = ParseByte(extra, ExtraTintR, 80);
            byte g = ParseByte(extra, ExtraTintG, 180);
            byte b = ParseByte(extra, ExtraTintB, 255);
            for (int i = 0; i < regions.Count; i++)
                SpineAtlasRestyle.TintPacked(pageRgba, pageW, pageH, regions[i], r, g, b);
        }

        File.WriteAllBytes(artifactPath, PngRgba.Encode(pageRgba, pageW, pageH));
    }

    internal static void UnrotatedSize(AtlasRegion region, out int width, out int height)
    {
        if (region.degrees == 90)
        {
            width = region.height;
            height = region.width;
        }
        else
        {
            width = region.width;
            height = region.height;
        }
    }

    internal static Atlas LoadAtlas(string atlasAbsolute)
    {
        using var reader = new StreamReader(atlasAbsolute);
        return new Atlas(reader, Path.GetDirectoryName(atlasAbsolute) ?? "", SpineNoopTextureLoader.Instance);
    }

    internal static string ResolveExisting(string projectRoot, string path)
    {
        if (Path.IsPathRooted(path) && File.Exists(path))
            return path;

        string normalized = path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string[] candidates =
        [
            Path.Combine(projectRoot, normalized),
            Path.Combine(projectRoot, "Assets", normalized),
        ];
        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return candidates[i];
        }

        throw new FileNotFoundException("Atlas restyle path not found: " + path);
    }

    private static string ResolvePagePng(string atlasPath, string pageName, string projectRoot)
    {
        string atlasAbs = Path.IsPathRooted(atlasPath) ? atlasPath : ResolveExisting(projectRoot, atlasPath);
        string sibling = Path.Combine(Path.GetDirectoryName(atlasAbs) ?? "", pageName);
        if (File.Exists(sibling))
            return sibling;
        return ResolveExisting(projectRoot, pageName);
    }

    private static AtlasPage SelectPage(Atlas atlas, IReadOnlyDictionary<string, string> extra)
    {
        if (extra.TryGetValue(ExtraPageName, out string? name) && !string.IsNullOrWhiteSpace(name))
        {
            for (int i = 0; i < atlas.Pages.Count; i++)
            {
                if (string.Equals(atlas.Pages[i].name, name, StringComparison.OrdinalIgnoreCase))
                    return atlas.Pages[i];
            }

            throw new InvalidOperationException("Atlas page not found: " + name);
        }

        return atlas.Pages[0];
    }

    private static List<AtlasRegion> SelectRegions(Atlas atlas, AtlasPage page, IReadOnlyDictionary<string, string> extra)
    {
        var selected = new List<AtlasRegion>();
        extra.TryGetValue(ExtraRegion, out string? spec);
        bool all = string.IsNullOrWhiteSpace(spec) || spec == "*";
        string[] names = all
            ? []
            : spec!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < atlas.Regions.Count; i++)
        {
            AtlasRegion region = atlas.Regions[i];
            if (region.page != page)
                continue;
            if (all)
            {
                selected.Add(region);
                continue;
            }

            for (int n = 0; n < names.Length; n++)
            {
                if (string.Equals(region.name, names[n], StringComparison.Ordinal))
                {
                    selected.Add(region);
                    break;
                }
            }
        }

        return selected;
    }

    private static bool MatchesUnrotated(AtlasRegion region, int width, int height)
    {
        UnrotatedSize(region, out int w, out int h);
        return width == w && height == h;
    }

    private static byte ParseByte(IReadOnlyDictionary<string, string> extra, string key, byte fallback)
    {
        if (!extra.TryGetValue(key, out string? text) || !int.TryParse(text, out int value))
            return fallback;
        if (value < 0)
            return 0;
        return value > 255 ? (byte)255 : (byte)value;
    }

    private static string SanitizeFileToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        string token = sb.ToString().Trim('-');
        return token.Length == 0 ? "region" : token;
    }
}
