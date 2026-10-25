using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XEngine.Editor.AssetBundles;

/// <summary>One bounded, disk-only simulation build. No asset instances or open handles are retained.</summary>
internal static class EditorSimulationCache
{
    internal static bool Prepare(string root, string inputHash, Action<string> build)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string output = Path.GetFullPath(Path.Combine(root, "current"));
        string marker = Path.Combine(root, "complete.txt");
        if (File.Exists(marker) && Directory.Exists(output))
        {
            string[] saved = File.ReadAllLines(marker);
            if (saved.Length == 2 && saved[0] == inputHash && saved[1] == HashOutput(output))
                return true;
        }

        // Invalidate before writing: interrupted/failed builds must never become cache hits.
        File.Delete(marker);
        if (!string.Equals(Path.GetDirectoryName(output), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Simulation cache output escaped its root.");
        if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
        build(output);
        string temporary = marker + ".tmp";
        File.WriteAllLines(temporary, new[] { inputHash, HashOutput(output) });
        File.Move(temporary, marker, overwrite: true);
        return false;
    }

    internal static string HashOutput(string output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string[] files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            AppendText(hash, Path.GetRelativePath(output, file));
            AppendFile(hash, file);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void AppendText(IncrementalHash hash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    internal static void AppendFile(IncrementalHash hash, string path)
    {
        // Hash real contents, not mtime/length: a same-size rewrite with preserved timestamps
        // must invalidate. SHA256 streams through the OS cache without deserializing payloads.
        using var stream = File.OpenRead(path);
        hash.AppendData(SHA256.HashData(stream));
    }
}
