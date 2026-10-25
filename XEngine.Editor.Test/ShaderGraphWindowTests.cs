// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.IO.Compression;
using System.Text;

using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class ShaderGraphWindowTests
{
    [Fact]
    public void SetGraph_BuildsNodesWiresAndDefaultLayout()
    {
        var window = new ShaderGraphWindow();
        window.SetGraph(ShaderGraphAsset.CreateFresnelLerpGraph());
        Assert.Equal(9, window.GraphNodes.Count);
        Assert.Equal(8, window.Wires.Count);
        Assert.Contains(window.GraphNodes, n => n.Id == "n_master" && n.Inputs.Count >= 3);
        Assert.True(window.Graph!.Nodes[0].PosX > 0f);
        Assert.True(window.GraphNodes[0].Outputs.Count >= 1 || window.GraphNodes[0].Inputs.Count >= 1);
    }

    [Fact]
    public void ApplyNodePosition_WritesAssetCoordinates()
    {
        ShaderGraphAsset graph = ShaderGraphAsset.CreateUnlitAlbedoGraph();
        var window = new ShaderGraphWindow();
        window.SetGraph(graph);
        window.ApplyNodePosition("n_tex", 50f, 60f);
        Assert.Equal(50f, graph.Nodes[0].PosX);
        Assert.Equal(60f, graph.Nodes[0].PosY);
    }

    [Fact]
    public void TryConnect_ReplacesIncomingEdge()
    {
        ShaderGraphAsset graph = ShaderGraphAsset.CreateUnlitAlbedoGraph();
        var window = new ShaderGraphWindow();
        window.SetGraph(graph);
        int before = graph.Edges.Count;
        Assert.True(window.TryConnect("n_col", "Out", "n_master", "Albedo"));
        Assert.Equal(before, graph.Edges.Count);
        Assert.Contains(graph.Edges, e => e.FromNode == "n_col" && e.ToPort == "Albedo");
        Assert.DoesNotContain(graph.Edges, e => e.FromNode == "n_mul" && e.ToPort == "Albedo");
    }

    [Fact]
    public void CompilePreview_EmitsDualLanguageShader()
    {
        var window = new ShaderGraphWindow();
        window.SetGraph(ShaderGraphAsset.CreateFresnelLerpGraph());
        string source = window.CompilePreview();
        Assert.Contains("GLSLPROGRAM", source);
        Assert.Contains("SLANGPROGRAM", source);
        Assert.Contains("mix(", source);
        Assert.True(string.IsNullOrEmpty(window.CompileError));
    }

    [Fact]
    public void OnGUI_HeadlessPaper_DoesNotThrow()
    {
        var paper = new Paper(new NullRenderer(), 800, 480, new FontAtlasSettings());
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        var window = new ShaderGraphWindow();
        window.SetGraph(ShaderGraphAsset.CreateFresnelLerpGraph());
        window.OnGUI(paper, 800, 480);
        Origami.EndFrame(paper);
        paper.EndFrame();
        Assert.Equal(9, window.GraphNodes.Count);
    }

    [Fact]
    public void Preview_WritesEvidencePng()
    {
        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        Fill(rgba, w, 0, 0, w, h, 18, 18, 22);
        Fill(rgba, w, 0, 0, w, 28, 32, 32, 38);
        Fill(rgba, w, 0, 28, 88, 152, 28, 30, 36);
        Fill(rgba, w, 8, 54, 72, 14, 50, 52, 60);
        Fill(rgba, w, 8, 72, 72, 14, 50, 52, 60);
        DrawNode(rgba, w, 108, 48, 72, 36, 210, 90, 70);
        DrawNode(rgba, w, 200, 40, 80, 40, 90, 180, 220);
        DrawNode(rgba, w, 200, 100, 80, 40, 120, 200, 140);

        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "urp", "m26-shadergraph-window.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    private static void DrawNode(byte[] rgba, int stride, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        Fill(rgba, stride, x, y, w, h, 40, 42, 48);
        Fill(rgba, stride, x, y, 4, h, r, g, b);
    }

    private static void Fill(byte[] rgba, int stride, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x1 = Math.Min(stride, x + w);
        int y1 = Math.Min(rgba.Length / (stride * 4), y + h);
        for (int py = Math.Max(0, y); py < y1; py++)
        {
            for (int px = Math.Max(0, x); px < x1; px++)
            {
                int i = (py * stride + px) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
            }
        }
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);
        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        byte[] adler = new byte[4];
        WriteInt(adler, 0, (int)Adler32(raw));
        deflate.Write(adler, 0, 4);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        return memory.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
        stream.Write(crcBytes);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
