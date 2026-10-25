using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;


namespace XEngine.UnityImporter.Editor;

/// <summary>Resolves Unity asset GUIDs to file paths by scanning the source project's .meta files.</summary>
public sealed partial class UnityAssetResolver
{
    [GeneratedRegex(@"^guid:\s*([0-9a-f]{32})", RegexOptions.Multiline)]
    private static partial Regex GuidRegex();

    private readonly Dictionary<string, string> _guidToPath = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _guidToPath.Count;

    public static UnityAssetResolver Scan(string rootDir, params string[] extensions)
    {
        var resolver = new UnityAssetResolver();
        foreach (string meta in Directory.EnumerateFiles(rootDir, "*.meta", SearchOption.AllDirectories))
        {
            // Cheap pre-filter on the first 200 chars — guid is on line 2 of Unity .meta files.
            using var stream = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[256];
            int read = stream.Read(buffer, 0, buffer.Length);
            string head = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            Match match = GuidRegex().Match(head);
            if (!match.Success) continue;

            string assetPath = meta[..^".meta".Length];
            if (!File.Exists(assetPath)) continue;
            string assetExtension = Path.GetExtension(assetPath).ToLowerInvariant();
            bool extensionWanted = extensions.Length == 0;
            for (int i = 0; i < extensions.Length; i++)
                if (extensions[i] == assetExtension) { extensionWanted = true; break; }
            if (!extensionWanted)
                continue;
            resolver._guidToPath[match.Groups[1].Value] = assetPath;
        }
        return resolver;
    }

    public string? Resolve(string guid)
    {
        if (guid.Length == 0 || !_guidToPath.TryGetValue(guid, out string? resolved))
            return null;
        return resolved;
    }
}
