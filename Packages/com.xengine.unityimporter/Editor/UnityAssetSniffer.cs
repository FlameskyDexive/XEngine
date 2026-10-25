using System.IO;
using System.Text;


namespace XEngine.UnityImporter.Editor;

/// <summary>Content sniffing for Unity serialized assets (`%YAML 1.1` header), used to claim
/// extensions XEngine also owns natively (.mesh/.prefab/.mat/.anim/.controller).</summary>
public static class UnityAssetSniffer
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("%YAML 1.1");

    public static bool IsUnityYaml(string absolutePath)
    {
        try
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[Magic.Length];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) return false;
                read += n;
            }

            for (int i = 0; i < Magic.Length; i++)
                if (buffer[i] != Magic[i]) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
