using System;
using System.IO;
using System.Text;

namespace XEngine.Spine;

/// <summary>
/// File-name and content sniffing for Spine source assets. Used by importers so
/// <c>.txt</c> / <c>.json</c> / <c>.bytes</c> are not claimed globally.
/// </summary>
public static class SpineAssetPaths
{
    public const string AtlasTxtSuffix = ".atlas.txt";
    public const string SkelBytesSuffix = ".skel.bytes";

    public static bool IsAtlasTxtFileName(string path)
        => Path.GetFileName(path).EndsWith(AtlasTxtSuffix, StringComparison.OrdinalIgnoreCase);

    public static bool IsSkelBytesFileName(string path)
        => Path.GetFileName(path).EndsWith(SkelBytesSuffix, StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string path)
    {
        string file = Path.GetFileName(path);
        if (file.EndsWith(AtlasTxtSuffix, StringComparison.OrdinalIgnoreCase))
            return file[..^AtlasTxtSuffix.Length];
        if (file.EndsWith(SkelBytesSuffix, StringComparison.OrdinalIgnoreCase))
            return file[..^SkelBytesSuffix.Length];
        return Path.GetFileNameWithoutExtension(file);
    }

    public static string GetSkeletonStem(string path)
    {
        string file = Path.GetFileName(path);
        if (file.EndsWith(SkelBytesSuffix, StringComparison.OrdinalIgnoreCase))
            return file[..^SkelBytesSuffix.Length];
        return Path.GetFileNameWithoutExtension(file);
    }

    public static string? FindSiblingAtlas(string skeletonPath)
    {
        string? dir = Path.GetDirectoryName(skeletonPath);
        if (string.IsNullOrEmpty(dir)) return null;

        string stem = GetSkeletonStem(skeletonPath);
        string atlas = Path.Combine(dir, stem + ".atlas");
        if (File.Exists(atlas)) return atlas;

        string atlasTxt = Path.Combine(dir, stem + AtlasTxtSuffix);
        if (File.Exists(atlasTxt)) return atlasTxt;

        // Spine exports commonly pair "-ess" skeleton JSON with a "-pma" (premultiplied) atlas
        // (e.g. spineboy-ess.json + spineboy-pma.atlas), so the exact-stem lookup misses. Fall
        // back to the single sibling atlas in the same directory — one atlas per export folder
        // is the Spine convention, and an ambiguous folder (multiple atlases) stays unlinked.
        string?[] candidates = Directory.GetFiles(dir, "*.atlas");
        if (candidates.Length == 1) return candidates[0];
        string[] txtCandidates = Directory.GetFiles(dir, "*" + AtlasTxtSuffix);
        if (candidates.Length == 0 && txtCandidates.Length == 1) return txtCandidates[0];
        return null;
    }

    public static bool FileLooksLikeSpineJson(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using FileStream stream = File.OpenRead(path);
            // Spine exports "skeleton"/"bones" first, but "slots" can sit well past 8 KB once a
            // skeleton has many bones (raptor ~9.6 KB, mix-and-match ~22 KB in the official
            // samples). Sniff a 1 MB window — "animations" (the bulk of any larger export)
            // always comes after "slots", so the scan stays bounded.
            byte[] buffer = new byte[1024 * 1024];
            int read = 0;
            while (read < buffer.Length)
            {
                int chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }
            if (read <= 0) return false;
            int start = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;

            return LooksLikeSpineJson(Encoding.UTF8.GetString(buffer, start, read - start));
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool LooksLikeSpineJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length || text[i] != '{') return false;

        return text.Contains("\"skeleton\"", StringComparison.Ordinal)
            && text.Contains("\"bones\"", StringComparison.Ordinal)
            && text.Contains("\"slots\"", StringComparison.Ordinal);
    }
}
