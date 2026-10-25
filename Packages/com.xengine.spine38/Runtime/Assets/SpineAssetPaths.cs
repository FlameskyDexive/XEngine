using System;
using System.IO;
using System.Text;

namespace XEngine.Spine38;

/// <summary>File-name and content sniffing for Spine 3.8 source assets.</summary>
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

        string[] candidates = Directory.GetFiles(dir, "*.atlas");
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

    public static bool FileLooksLikeSpine38Json(string path)
        => FileLooksLikeSpineJson(path) && FileLooksLikeSpine38Version(path);

    public static bool FileLooksLikeSpine38Version(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using FileStream stream = File.OpenRead(path);
            byte[] buffer = new byte[4096];
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return false;
            return LooksLikeSpine38Version(Encoding.UTF8.GetString(buffer, 0, read));
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

    public static bool LooksLikeSpine38Version(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("\"spine\":\"3.8", StringComparison.Ordinal)
            || text.Contains("\"spine\": \"3.8", StringComparison.Ordinal);
    }
}
