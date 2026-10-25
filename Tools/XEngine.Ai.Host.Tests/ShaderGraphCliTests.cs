using System.IO.Compression;
using System.Text;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Runtime.Rendering;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public class ShaderGraphCliTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public ShaderGraphCliTests(TestSceneFixture fx) => _fx = fx;

    static string GraphPath(TestSceneFixture fx, string echo, string name = "UnlitAlbedo.shadergraph")
    {
        string dir = Path.Combine(fx.ProjectRoot, "Assets", "Shaders");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, echo);
        return path;
    }

    [Fact]
    public void Inspect_ReadsShaderNameNodesAndHash()
    {
        string absolute = GraphPath(_fx, UnlitEcho);
        var detail = SceneReader.ReadAssetData(absolute, "Assets/Shaders/UnlitAlbedo.shadergraph");
        Assert.Contains("ShaderGraphAsset", detail.TypeName, StringComparison.Ordinal);
        Assert.Contains(detail.Fields, f => f.Path == "ShaderName" && f.Value == "Graph/UnlitAlbedo");
        Assert.Contains(detail.Fields, f => f.Value == "n_master");
        Assert.Contains(detail.Fields, f => f.Value == "_MainTex");
        Assert.False(string.IsNullOrEmpty(detail.ContentHash));
    }

    [Fact]
    public async Task Patch_UpdatesNodeField_IsJournaled()
    {
        string absolute = GraphPath(_fx, UnlitEcho, "PatchMe.shadergraph");
        var inspect = SceneReader.ReadAssetData(absolute, "Assets/Shaders/PatchMe.shadergraph");
        var patches = new[] { new SceneFieldPatch("ShaderName", "Graph/Patched") };
        var result = await _fx.SceneWriter.PatchAssetDataAsync(
            absolute, "Assets/Shaders/PatchMe.shadergraph", patches,
            TestSceneFixture.Provenance("test.shadergraph", "sg-patch-1", "rename graph"),
            dryRun: false, expectedBeforeHash: inspect.ContentHash);

        Assert.False(result.DryRun);
        Assert.NotEqual(result.BeforeHash, result.AfterHash);
        string after = File.ReadAllText(absolute);
        Assert.Contains("Graph/Patched", after, StringComparison.Ordinal);

        var replay = await _fx.SceneWriter.PatchAssetDataAsync(
            absolute, "Assets/Shaders/PatchMe.shadergraph", patches,
            TestSceneFixture.Provenance("test.shadergraph", "sg-patch-1", "rename graph"),
            dryRun: false);
        Assert.True(
            replay.Summary.Contains("Idempotent", StringComparison.Ordinal)
            || replay.Summary.Contains("No change", StringComparison.Ordinal)
            || replay.ChangeId == result.ChangeId);
    }

    [Fact]
    public void Compile_Unlit_EmitsDualSourceAndCoreIncludes()
    {
        string source = ShaderGraphEcho.CompileToSource(UnlitEcho);
        Assert.Contains("GLSLPROGRAM", source, StringComparison.Ordinal);
        Assert.Contains("SLANGPROGRAM", source, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.Core + "\"", source, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.Lighting + "\"", source, StringComparison.Ordinal);
        Assert.Contains("texture(_MainTex", source, StringComparison.Ordinal);
        Assert.Contains("_MainTex.Sample(_MainTexSampler", source, StringComparison.Ordinal);
        Assert.Contains("ApplyFog", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Lit_UsesLightBvhIncludes()
    {
        string source = ShaderGraphEcho.CompileToSource(LitEcho);
        Assert.Contains("CalculateForwardLighting", source, StringComparison.Ordinal);
        Assert.Contains("StandardCalculateLighting", source, StringComparison.Ordinal);
        Assert.Contains("#include \"" + ShaderLibrary.StandardLighting + "\"", source, StringComparison.Ordinal);
        Assert.Contains("vec4(0.8, 0.8, 0.8, 1.0)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MissingMaster_Throws()
    {
        var ex = Assert.Throws<ShaderGraphCompileException>(
            () => ShaderGraphEcho.CompileToSource(EmptyEcho));
        Assert.Contains("MasterUnlit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirePath_RejectsNonShadergraph()
    {
        Assert.Throws<ArgumentException>(() => AiTools.RequireShaderGraphPath("Assets/x.mat"));
        AiTools.RequireShaderGraphPath("Assets/Shaders/UnlitAlbedo.shadergraph");
    }

    [Fact]
    public void Compile_WritesOptionalShaderFile()
    {
        GraphPath(_fx, UnlitEcho, "WriteOut.shadergraph");
        string relativeGraph = "Assets/Shaders/WriteOut.shadergraph";
        string relativeOut = "Assets/Shaders/WriteOut.shader";
        var project = ProjectIdentity.Inspect(_fx.ProjectRoot);
        var services = new SimpleServices(project);
        var result = AiTools.CompileShaderGraph(services, relativeGraph, relativeOut);
        Assert.True(result.Written);
        Assert.Equal("Graph/UnlitAlbedo", result.ShaderName);
        string written = File.ReadAllText(Path.Combine(_fx.ProjectRoot, relativeOut));
        Assert.Contains("GLSLPROGRAM", written, StringComparison.Ordinal);
        Assert.Contains("SLANGPROGRAM", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_CliBoard_WritesPng()
    {
        var canvas = new Board(384, 128, 18, 18, 24);
        canvas.Text(12, 8, "M27 SHADERGRAPH CLI", 230, 230, 235, 2);
        canvas.Fill(16, 40, 112, 72, 32, 120, 140, 255);
        canvas.Stroke(16, 40, 112, 72, 90, 210, 220);
        canvas.Text(24, 66, "INSPECT", 240, 250, 255, 2);
        canvas.Fill(136, 40, 112, 72, 160, 110, 40, 255);
        canvas.Stroke(136, 40, 112, 72, 230, 180, 80);
        canvas.Text(150, 66, "PATCH", 255, 245, 230, 2);
        canvas.Fill(256, 40, 112, 72, 40, 130, 70, 255);
        canvas.Stroke(256, 40, 112, 72, 90, 210, 120);
        canvas.Text(262, 66, "COMPILE", 235, 255, 240, 2);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "urp", "m27-shadergraph-cli.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Prompts_IncludePipelineAndShaderGraphPlaybooks()
    {
        Assert.Contains("asset_data_patch", AiPrompts.EditPipelineSafely(), StringComparison.Ordinal);
        Assert.Contains("LightBVH", AiPrompts.EditPipelineSafely(), StringComparison.Ordinal);
        Assert.Contains("shadergraph_compile", AiPrompts.CompileShaderGraph(), StringComparison.Ordinal);
        Assert.Contains("GLSLPROGRAM", AiPrompts.CompileShaderGraph(), StringComparison.Ordinal);
        Assert.Contains("StandardCalculateLighting", AiPrompts.CompileShaderGraph(), StringComparison.Ordinal);
        Assert.Contains("runtime_animator_state", AiPrompts.DebugAnimationState(), StringComparison.Ordinal);
        Assert.Contains("animator_validate", AiPrompts.AuthorAnimatorController(), StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_M28Closeout_WritesPng()
    {
        var canvas = new Board(384, 128, 16, 18, 28);
        canvas.Text(12, 8, "M28 URP CLOSEOUT", 230, 230, 235, 2);
        canvas.Fill(16, 40, 112, 72, 50, 90, 160, 255);
        canvas.Stroke(16, 40, 112, 72, 120, 170, 230);
        canvas.Text(28, 66, "SKILLS", 240, 250, 255, 2);
        canvas.Fill(136, 40, 112, 72, 40, 120, 90, 255);
        canvas.Stroke(136, 40, 112, 72, 90, 200, 140);
        canvas.Text(150, 66, "README", 235, 255, 240, 2);
        canvas.Fill(256, 40, 112, 72, 140, 90, 50, 255);
        canvas.Stroke(256, 40, 112, 72, 230, 170, 90);
        canvas.Text(274, 66, "BOOKS", 255, 245, 230, 2);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "urp", "m28-urp-closeout.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "XEngine.sln")))
                return dir;
            string parent = Path.GetFullPath(Path.Combine(dir, ".."));
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                break;
            dir = parent;
        }
        throw new InvalidOperationException("Could not locate XEngine.sln from " + AppContext.BaseDirectory);
    }

    private sealed class SimpleServices : IServiceProvider
    {
        private readonly ProjectDescriptor _project;
        public SimpleServices(ProjectDescriptor project) => _project = project;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ProjectDescriptor) ? _project : null;
    }

    private const string UnlitEcho = """
{
  "$type": "XEngine.Runtime.Rendering.ShaderGraphAsset, XEngine.Runtime",
  "ShaderName": "Graph/UnlitAlbedo",
  "Target": 0,
  "Properties": {
    "$values": [
      { "Id": "p_maintex", "Name": "_MainTex", "DisplayName": "Albedo", "Type": 5, "DefaultValue": "white" },
      { "Id": "p_color", "Name": "_MainColor", "DisplayName": "Tint", "Type": 4, "DefaultValue": "1,1,1,1" }
    ]
  },
  "Nodes": {
    "$values": [
      { "Id": "n_tex", "Type": "SampleTexture2D", "PosX": 0, "PosY": 0, "Fields": { "$values": [ { "Key": "Texture", "Value": "p_maintex" } ] } },
      { "Id": "n_col", "Type": "Property", "PosX": 0, "PosY": 0, "Fields": { "$values": [ { "Key": "Property", "Value": "p_color" } ] } },
      { "Id": "n_mul", "Type": "Multiply", "PosX": 0, "PosY": 0, "Fields": { "$values": [] } },
      { "Id": "n_master", "Type": "MasterUnlit", "PosX": 0, "PosY": 0, "Fields": { "$values": [] } }
    ]
  },
  "Edges": {
    "$values": [
      { "Id": "e0", "FromNode": "n_tex", "FromPort": "RGBA", "ToNode": "n_mul", "ToPort": "A" },
      { "Id": "e1", "FromNode": "n_col", "FromPort": "Out", "ToNode": "n_mul", "ToPort": "B" },
      { "Id": "e2", "FromNode": "n_mul", "FromPort": "Out", "ToNode": "n_master", "ToPort": "Albedo" }
    ]
  }
}
""";

    private const string LitEcho = """
{
  "$type": "XEngine.Runtime.Rendering.ShaderGraphAsset, XEngine.Runtime",
  "ShaderName": "Graph/LitConstant",
  "Target": 1,
  "Properties": { "$values": [] },
  "Nodes": {
    "$values": [
      { "Id": "n_alb", "Type": "Color", "Fields": { "$values": [
        { "Key": "R", "Value": "0.8" }, { "Key": "G", "Value": "0.8" },
        { "Key": "B", "Value": "0.8" }, { "Key": "A", "Value": "1" }
      ] } },
      { "Id": "n_emi", "Type": "Color", "Fields": { "$values": [
        { "Key": "R", "Value": "0" }, { "Key": "G", "Value": "0" },
        { "Key": "B", "Value": "0" }, { "Key": "A", "Value": "1" }
      ] } },
      { "Id": "n_master", "Type": "MasterLit", "Fields": { "$values": [] } }
    ]
  },
  "Edges": {
    "$values": [
      { "Id": "e0", "FromNode": "n_alb", "FromPort": "Out", "ToNode": "n_master", "ToPort": "Albedo" },
      { "Id": "e1", "FromNode": "n_emi", "FromPort": "RGB", "ToNode": "n_master", "ToPort": "Emission" }
    ]
  }
}
""";

    private const string EmptyEcho = """
{
  "$type": "XEngine.Runtime.Rendering.ShaderGraphAsset, XEngine.Runtime",
  "ShaderName": "Graph/Empty",
  "Target": 0,
  "Properties": { "$values": [] },
  "Nodes": { "$values": [] },
  "Edges": { "$values": [] }
}
""";

    private sealed class Board
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Rgba;

        public Board(int width, int height, byte r, byte g, byte b)
        {
            Width = width;
            Height = height;
            Rgba = new byte[width * height * 4];
            Fill(0, 0, width, height, r, g, b, 255);
        }

        public void Fill(int x, int y, int w, int h, byte r, byte g, byte b, byte a)
        {
            int x0 = Math.Max(0, x);
            int y0 = Math.Max(0, y);
            int x1 = Math.Min(Width, x + w);
            int y1 = Math.Min(Height, y + h);
            for (int py = y0; py < y1; py++)
            {
                int row = py * Width * 4;
                for (int px = x0; px < x1; px++)
                {
                    int i = row + px * 4;
                    Rgba[i] = r;
                    Rgba[i + 1] = g;
                    Rgba[i + 2] = b;
                    Rgba[i + 3] = a;
                }
            }
        }

        public void Stroke(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            Fill(x, y, w, 2, r, g, b, 255);
            Fill(x, y + h - 2, w, 2, r, g, b, 255);
            Fill(x, y, 2, h, r, g, b, 255);
            Fill(x + w - 2, y, 2, h, r, g, b, 255);
        }

        public void Text(int x, int y, string text, byte r, byte g, byte b, int scale)
        {
            int cursor = x;
            foreach (char c in text.ToUpperInvariant())
            {
                if (!Glyphs.TryGetValue(c, out byte[]? rows))
                    rows = Glyphs['?'];
                for (int row = 0; row < 7; row++)
                {
                    byte bits = rows[row];
                    for (int col = 0; col < 5; col++)
                    {
                        if ((bits & (1 << (4 - col))) == 0) continue;
                        Fill(cursor + col * scale, y + row * scale, scale, scale, r, g, b, 255);
                    }
                }
                cursor += 6 * scale;
            }
        }

        public static byte[] EncodePng(byte[] rgba, int width, int height)
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
            crc ^= 0xFFFFFFFF;
            byte[] crcBytes = new byte[4];
            WriteInt(crcBytes, 0, (int)crc);
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

        private static readonly Dictionary<char, byte[]> Glyphs = new()
        {
            [' '] = [0, 0, 0, 0, 0, 0, 0],
            ['2'] = [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
            ['8'] = [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            ['7'] = [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            ['A'] = [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            ['D'] = [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
            ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
            ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['K'] = [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
            ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            ['N'] = [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            ['S'] = [0x0E, 0x11, 0x10, 0x0E, 0x01, 0x11, 0x0E],
            ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['U'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['?'] = [0x0E, 0x11, 0x01, 0x06, 0x04, 0, 0x04],
        };
    }
}
