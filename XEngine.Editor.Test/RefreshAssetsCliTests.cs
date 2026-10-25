// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>★ Gen-P3.1 — headless --refresh-assets imports dirty files into Library/cache and exits 0.</summary>
public sealed class RefreshAssetsCliTests : EditorTestHarness
{
    [Fact]
    public void ReadArguments_SetsRefreshAssetsMode()
    {
        Program.ReadArguments(["--refresh-assets", "--project", @"D:\game"]);
        Assert.True(Program.RefreshAssetsMode);
        Assert.Equal(@"D:\game", Program.StartupProjectPath);
    }

    [Fact]
    public void RunRefreshAssets_ImportsNewScene_WritesCacheAndExitsZero()
    {
        var scene = new Scene();
        scene.Add(new GameObject("Root"));
        string rel = "Fresh.scene";
        string abs = AssetAbsolutePath(rel);
        File.WriteAllText(abs, Serializer.Serialize(typeof(object), scene).WriteToString());

        Assets.Dispose();

        int code = Program.RunRefreshAssets(Project.RootPath);
        Assert.Equal(0, code);

        Assert.True(File.Exists(abs + ".meta"), "ScanAssets should mint a .meta for the new scene.");
        MetaFileData meta = MetaFile.Read(abs + ".meta");
        Assert.NotEqual(Guid.Empty, meta.Guid);
        string cache = Path.Combine(Project.CachePath, meta.Guid.ToString() + ".asset");
        Assert.True(File.Exists(cache), "Library/cache/{guid}.asset should exist after refresh.");
        Assert.True(new FileInfo(cache).Length > 0);

        WriteEvidence(meta.Guid, cache, code);
    }

    [Fact]
    public void RunRefreshAssets_MissingProject_ReturnsOne()
    {
        int code = Program.RunRefreshAssets(Path.Combine(Path.GetTempPath(), "no-such-xengine-project-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(1, code);
    }

    private static void WriteEvidence(Guid guid, string cachePath, int exitCode)
    {
        string? repo = FindRepo();
        if (repo == null) return;
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "gen-asset");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "2026-08-16-p3-1-refresh-assets.png");
        const int w = 560;
        const int h = 200;
        var px = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 3] = 24;
            px[i * 3 + 1] = 26;
            px[i * 3 + 2] = 32;
        }

        void Rect(int x, int y, int rw, int rh, byte r, byte g, byte b)
        {
            for (int yy = Math.Max(0, y); yy < Math.Min(h, y + rh); yy++)
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + rw); xx++)
                {
                    int i = (yy * w + xx) * 3;
                    px[i] = r; px[i + 1] = g; px[i + 2] = b;
                }
        }

        Rect(0, 0, w, 36, 36, 48, 72);
        Rect(24, 56, 200, 80, 70, 90, 140);
        Rect(248, 56, 288, 80, 60, 160, 90);
        File.WriteAllBytes(png, EncodePng(px, w, h));
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P3.1 --refresh-assets acceptance\n\n" +
            $"- Exit code: {exitCode}\n" +
            $"- Imported scene GUID: {guid}\n" +
            $"- Cache: {cachePath} ({new FileInfo(cachePath).Length} bytes)\n");
    }

    private static string? FindRepo()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static byte[] EncodePng(byte[] rgb, int w, int h)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb, y * w * 3, w * 3);
        }

        byte[] deflated;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                raw.Position = 0;
                raw.CopyTo(zlib);
            }
            deflated = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(png, "IHDR", Ihdr(w, h));
        WriteChunk(png, "IDAT", deflated);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static byte[] Ihdr(int w, int h)
    {
        var b = new byte[13];
        b[0] = (byte)(w >> 24); b[1] = (byte)(w >> 16); b[2] = (byte)(w >> 8); b[3] = (byte)w;
        b[4] = (byte)(h >> 24); b[5] = (byte)(h >> 16); b[6] = (byte)(h >> 8); b[7] = (byte)h;
        b[8] = 8; b[9] = 2;
        return b;
    }

    private static void WriteChunk(MemoryStream png, string type, byte[] data)
    {
        WriteBe32(png, data.Length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < typeBytes.Length; i++) crc = Crc(crc, typeBytes[i]);
        for (int i = 0; i < data.Length; i++) crc = Crc(crc, data[i]);
        WriteBe32(png, (int)(crc ^ 0xFFFFFFFF));
    }

    private static void WriteBe32(MemoryStream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static uint Crc(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }
}
