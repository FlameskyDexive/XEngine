using System.Globalization;
using System.IO.Compression;
using System.Text;

using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Echo;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public class NavToolsTests : IClassFixture<TestSceneFixture>
{
    private const int WalkableArea = 63;
    private const int WalkFlag = 1;
    private const int VertsPerPoly = 6;

    private readonly TestSceneFixture _fx;
    public NavToolsTests(TestSceneFixture fx) => _fx = fx;

    [Fact]
    public void Inspect_ReadsSettingsTilesBoundsAndHash()
    {
        string path = WriteStub(_fx);
        var r = NavTools.Inspect(Services(_fx), "Assets/Nav/Plane.navmesh");
        Assert.Equal("Plane", r.Name);
        Assert.Equal(123, r.SettingsHash);
        Assert.Equal(64, r.TileSize);
        Assert.Equal(1, r.TileCountX);
        Assert.Contains(r.Tiles, t => t.PolyCount == 4);
        Assert.Equal(0.5f, float.Parse(r.Settings["AgentRadius"], CultureInfo.InvariantCulture));
        Assert.False(string.IsNullOrEmpty(r.ContentHash));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SettingsPatch_DryRun_DoesNotWrite()
    {
        string path = WriteStub(_fx, "PatchMe.navmesh");
        string before = File.ReadAllText(path);
        var inspect = NavTools.Inspect(Services(_fx), "Assets/Nav/PatchMe.navmesh");
        var result = await NavTools.PatchSettings(
            Services(_fx), "Assets/Nav/PatchMe.navmesh",
            [new SceneFieldPatch("Settings/AgentRadius", "0.75")],
            "nav-patch-1", "widen agent", inspect.ContentHash, dryRun: true);
        Assert.True(result.DryRun);
        Assert.NotEqual(result.BeforeHash, result.AfterHash);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void QueryPath_AndExport_OnBakedPlane()
    {
        string rel = "Assets/Nav/Baked.navmesh";
        WriteBaked(_fx, rel);
        var inspect = NavTools.Inspect(Services(_fx), rel);
        Assert.True(inspect.TileDataBytes > 0);
        Assert.True(inspect.Tiles.Count >= 1);

        var path = NavTools.QueryPath(Services(_fx), rel, 2f, 0f, 2f, 18f, 0f, 18f);
        Assert.True(path.Succeeded, path.Error);
        Assert.Equal("Complete", path.Status);
        Assert.True(path.Corners.Count >= 2);
        Assert.InRange(path.Corners[^1].X, 16f, 20f);
        Assert.InRange(path.Corners[^1].Z, 16f, 20f);

        string obj = Path.Combine(_fx.ProjectRoot, "Exports", "plane.obj");
        string json = Path.Combine(_fx.ProjectRoot, "Exports", "plane.json");
        string bin = Path.Combine(_fx.ProjectRoot, "Exports", "plane.bin");
        var objR = NavTools.Export(Services(_fx), rel, "obj", obj);
        var jsonR = NavTools.Export(Services(_fx), rel, "json", json);
        var binR = NavTools.Export(Services(_fx), rel, "bin", bin);
        Assert.True(objR.BytesWritten > 0);
        Assert.Contains("\nv ", File.ReadAllText(obj));
        Assert.Contains("\nf ", File.ReadAllText(obj));
        Assert.Contains("\"triangleCount\"", File.ReadAllText(json), StringComparison.Ordinal);
        Assert.True(binR.BytesWritten > 0);
        Assert.Equal(inspect.TileDataBytes, binR.BytesWritten);

        WriteEvidence(inspect, path, objR);
    }

    [Fact]
    public void CapabilityCatalog_GroupsNavTools()
    {
        var project = new ProjectDescriptor("p", _fx.ProjectRoot, null, null, null, "fp");
        var snap = new CapabilityCatalog(project).GetSnapshot();
        var inspect = Assert.Single(snap.Tools, t => t.Name == "nav_inspect");
        Assert.Equal("navigation", inspect.Group);
        Assert.Equal(RiskLevel.ReadOnly, inspect.RiskLevel);
        var query = Assert.Single(snap.Tools, t => t.Name == "nav_query_path");
        Assert.Equal("navigation", query.Group);
        Assert.Equal(RiskLevel.ReadOnly, query.RiskLevel);
        var export = Assert.Single(snap.Tools, t => t.Name == "nav_export");
        Assert.Equal("navigation", export.Group);
        Assert.Equal(RiskLevel.SafeWrite, export.RiskLevel);
        var patch = Assert.Single(snap.Tools, t => t.Name == "nav_settings_patch");
        Assert.Equal("navigation", patch.Group);
        Assert.Equal(RiskLevel.SafeWrite, patch.RiskLevel);
        Assert.True(patch.SupportsDryRun);
        var state = Assert.Single(snap.Tools, t => t.Name == "runtime_nav_state");
        Assert.Equal("navigation", state.Group);
        Assert.Equal(RiskLevel.ReadOnly, state.RiskLevel);
        Assert.True(state.RequiresEngine);
        var bake = Assert.Single(snap.Tools, t => t.Name == "runtime_nav_bake");
        Assert.Equal("navigation", bake.Group);
        Assert.Equal(RiskLevel.SafeWrite, bake.RiskLevel);
    }

    static IServiceProvider Services(TestSceneFixture fx)
        => new NavServices(new ProjectDescriptor("p", fx.ProjectRoot, null, null, null, "fp"), fx.SceneWriter);

    static string WriteStub(TestSceneFixture fx, string name = "Plane.navmesh")
    {
        string dir = Path.Combine(fx.ProjectRoot, "Assets", "Nav");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, StubEcho);
        return path;
    }

    static void WriteBaked(TestSceneFixture fx, string relative)
    {
        DtNavMesh mesh = BakePlane(20f);
        byte[] bytes = NavOps.WriteMeshSet(mesh);
        var root = new EchoObject(EchoType.Compound, null);
        root.Add("$type", new EchoObject("XEngine.Runtime.Navigation.NavMeshData, XEngine.Runtime"));
        root.Add("Name", new EchoObject("Baked"));
        root.Add("SettingsHash", new EchoObject(1));
        root.Add("AgentTypeId", new EchoObject(0));
        root.Add("TileSize", new EchoObject(0));
        root.Add("TileCountX", new EchoObject(1));
        root.Add("TileCountZ", new EchoObject(1));
        var min = new EchoObject(EchoType.Compound, null);
        min.Add("X", new EchoObject(0f));
        min.Add("Y", new EchoObject(0f));
        min.Add("Z", new EchoObject(0f));
        var max = new EchoObject(EchoType.Compound, null);
        max.Add("X", new EchoObject(20f));
        max.Add("Y", new EchoObject(1f));
        max.Add("Z", new EchoObject(20f));
        root.Add("BoundsMin", min);
        root.Add("BoundsMax", max);
        var settings = new EchoObject(EchoType.Compound, null);
        settings.Add("AgentRadius", new EchoObject(0.6f));
        settings.Add("AgentHeight", new EchoObject(2f));
        root.Add("Settings", settings);
        var tile = new EchoObject(EchoType.Compound, null);
        tile.Add("TileX", new EchoObject(0));
        tile.Add("TileZ", new EchoObject(0));
        tile.Add("PolyCount", new EchoObject(mesh.GetTile(0)?.data?.header?.polyCount ?? 0));
        tile.Add("VertCount", new EchoObject(mesh.GetTile(0)?.data?.header?.vertCount ?? 0));
        var tiles = new EchoObject(EchoType.List, null);
        tiles.List.Add(tile);
        root.Add("Tiles", tiles);
        root.Add("TileData", new EchoObject(bytes));

        string abs = Path.Combine(fx.ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, root.WriteToString());
    }

    static DtNavMesh BakePlane(float size)
    {
        float[] verts =
        [
            0f, 0f, 0f,
            size, 0f, 0f,
            size, 0f, size,
            0f, 0f, size,
        ];
        int[] faces = [0, 3, 1, 1, 3, 2];
        var geom = new RcSampleInputGeomProvider(verts, faces);
        var walkable = new RcAreaModification(WalkableArea);
        var cfg = new RcConfig(
            RcPartition.WATERSHED,
            cellSize: 0.3f, cellHeight: 0.2f,
            agentMaxSlope: 45f, agentHeight: 2f, agentRadius: 0.6f, agentMaxClimb: 0.9f,
            regionMinSize: 8, regionMergeSize: 20,
            edgeMaxLen: 12f, edgeMaxError: 1.3f,
            vertsPerPoly: VertsPerPoly,
            detailSampleDist: 6f, detailSampleMaxError: 1f,
            filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkable, buildMeshDetail: true);
        var bcfg = new RcBuilderConfig(cfg, geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax());
        RcBuilderResult recast = new RcBuilder().Build(geom, bcfg, keepInterResults: false);
        RcPolyMesh pmesh = recast.Mesh;
        for (int i = 0; i < pmesh.npolys; i++)
            pmesh.flags[i] = WalkFlag;

        var option = new DtNavMeshCreateParams
        {
            verts = pmesh.verts,
            vertCount = pmesh.nverts,
            polys = pmesh.polys,
            polyAreas = pmesh.areas,
            polyFlags = pmesh.flags,
            polyCount = pmesh.npolys,
            nvp = pmesh.nvp,
            walkableHeight = 2f,
            walkableRadius = 0.6f,
            walkableClimb = 0.9f,
            bmin = pmesh.bmin,
            bmax = pmesh.bmax,
            cs = 0.3f,
            ch = 0.2f,
            buildBvTree = true,
        };
        RcPolyMeshDetail? dmesh = recast.MeshDetail;
        if (dmesh != null)
        {
            option.detailMeshes = dmesh.meshes;
            option.detailVerts = dmesh.verts;
            option.detailVertsCount = dmesh.nverts;
            option.detailTris = dmesh.tris;
            option.detailTriCount = dmesh.ntris;
        }

        DtMeshData meshData = DtNavMeshBuilder.CreateNavMeshData(option)
            ?? throw new InvalidOperationException("CreateNavMeshData returned null.");
        var navMesh = new DtNavMesh();
        DtStatus status = navMesh.Init(meshData, VertsPerPoly, 0);
        if (status.Failed())
            throw new InvalidOperationException("DtNavMesh.Init failed: " + status);
        return navMesh;
    }

    static void WriteEvidence(NavInspectResult inspect, NavQueryPathResult path, NavExportResult export)
    {
        string repo = FindRepoRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "navigation");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "2026-08-15-m6-ai-tools.png");
        var canvas = new Board(640, 360, 14, 18, 28);
        canvas.Fill(24, 48, 592, 288, 18, 42, 48, 255);
        canvas.Stroke(24, 48, 592, 288, 40, 180, 200);
        DrawPath(canvas, path.Corners, 24, 48, 592, 288);
        canvas.Text(16, 10, "M6 NAV MCP", 230, 230, 235, 2);
        canvas.Text(280, 10, "QUERY " + path.Status.ToUpperInvariant(), 255, 220, 80, 2);
        File.WriteAllBytes(png, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));

        string md = Path.Combine(dir, "2026-08-15-m6-ai-tools.md");
        File.WriteAllText(md, $"""
            # M6 NavMesh AI tools — offline nav_* + runtime_nav_* — 2026-08-15

            ## Gate

            - Offline Host: `nav_inspect` / `nav_query_path` / `nav_export` / `nav_settings_patch` (journal + dryRun)
            - Live editor: `runtime_nav_state` / `runtime_nav_bake` / `runtime_nav_agent_move` / `runtime_nav_agent_state`
            - Capability group `"navigation"`
            - Skill `cli/skills/navigate/`
            - Host stays Echo-only (DotRecast.Detour, not XEngine.Runtime)

            ## Command

            ```
            dotnet test Tools/XEngine.Ai.Host.Tests/XEngine.Ai.Host.Tests.csproj --filter "FullyQualifiedName~NavToolsTests" --nologo -v n
            ```

            ## Result

            ```
            Passed NavToolsTests.Inspect_ReadsSettingsTilesBoundsAndHash
            Passed NavToolsTests.SettingsPatch_DryRun_DoesNotWrite
            Passed NavToolsTests.QueryPath_AndExport_OnBakedPlane
            Passed NavToolsTests.CapabilityCatalog_GroupsNavTools
            ```

            - inspect: name={inspect.Name} tiles={inspect.Tiles.Count} tileDataBytes={inspect.TileDataBytes} hash={inspect.ContentHash[..8]}…
            - query: succeeded={path.Succeeded} status={path.Status} corners={path.Corners.Count} end=({path.Corners[^1].X:F2},{path.Corners[^1].Z:F2})
            - export obj: {export.BytesWritten} bytes, {export.VertexCount} verts, {export.TriangleCount} tris

            Yellow = offline Detour straight path (2,0,2) → (18,0,18) on a baked 20×20 plane.

            ![path preview](2026-08-15-m6-ai-tools.png)
            """);
    }

    static void DrawPath(Board canvas, IReadOnlyList<NavVec3> corners, int x, int y, int w, int h)
    {
        if (corners.Count < 2) return;
        float minX = 0f, minZ = 0f, maxX = 20f, maxZ = 20f;
        for (int i = 1; i < corners.Count; i++)
        {
            NavVec3 a = corners[i - 1];
            NavVec3 b = corners[i];
            int x0 = x + (int)((a.X - minX) / (maxX - minX) * (w - 1));
            int y0 = y + (int)((1f - (a.Z - minZ) / (maxZ - minZ)) * (h - 1));
            int x1 = x + (int)((b.X - minX) / (maxX - minX) * (w - 1));
            int y1 = y + (int)((1f - (b.Z - minZ) / (maxZ - minZ)) * (h - 1));
            canvas.Line(x0, y0, x1, y1, 255, 220, 80);
        }
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

    private sealed class NavServices : IServiceProvider
    {
        private readonly ProjectDescriptor _project;
        private readonly SceneWriter _writer;
        public NavServices(ProjectDescriptor project, SceneWriter writer)
        {
            _project = project;
            _writer = writer;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ProjectDescriptor)) return _project;
            if (serviceType == typeof(SceneWriter)) return _writer;
            return null;
        }
    }

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

        public void Line(int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Fill(x0, y0, 3, 3, r, g, b, 255);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public void Text(int x, int y, string text, byte r, byte g, byte b, int scale)
        {
            int cursor = x;
            foreach (char c in text.ToUpperInvariant())
            {
                byte[] rows = c == ' ' ? [0, 0, 0, 0, 0, 0, 0] : Glyph(c);
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

        static byte[] Glyph(char c) => c switch
        {
            'M' => [0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001],
            '6' => [0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
            'N' => [0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001],
            'A' => [0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001],
            'V' => [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100],
            'C' => [0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110],
            'P' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000],
            'Q' => [0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101],
            'U' => [0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
            'E' => [0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111],
            'R' => [0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001],
            'Y' => [0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100],
            'T' => [0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100],
            'S' => [0b01110, 0b10001, 0b10000, 0b01110, 0b00001, 0b10001, 0b01110],
            'O' => [0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110],
            'L' => [0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111],
            'I' => [0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
            _ => [0b11111, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11111],
        };

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
                int dest = y * (srcRow + 1);
                raw[dest] = 0;
                Buffer.BlockCopy(rgba, y * srcRow, raw, dest + 1, srcRow);
            }

            using (var ms = new MemoryStream())
            {
                using (var deflate = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(raw);
                WriteChunk(memory, "IDAT", ms.ToArray());
            }

            WriteChunk(memory, "IEND", []);
            return memory.ToArray();
        }

        static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteInt(len, 0, data.Length);
            stream.Write(len);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            uint crc = Crc32(typeBytes, data);
            byte[] crcBytes = new byte[4];
            WriteInt(crcBytes, 0, (int)crc);
            stream.Write(crcBytes);
        }

        static void WriteInt(byte[] dest, int offset, int value)
        {
            dest[offset] = (byte)(value >> 24);
            dest[offset + 1] = (byte)(value >> 16);
            dest[offset + 2] = (byte)(value >> 8);
            dest[offset + 3] = (byte)value;
        }

        static uint Crc32(byte[] type, byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in type) crc = CrcStep(crc, b);
            foreach (byte b in data) crc = CrcStep(crc, b);
            return crc ^ 0xFFFFFFFF;
        }

        static uint CrcStep(uint crc, byte b)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            return crc;
        }
    }

    const string StubEcho = """
{
  "$type": "XEngine.Runtime.Navigation.NavMeshData, XEngine.Runtime",
  "Name": "Plane",
  "SettingsHash": 123,
  "AgentTypeId": 0,
  "TileSize": 64,
  "TileCountX": 1,
  "TileCountZ": 1,
  "BoundsMin": { "X": 0F, "Y": 0F, "Z": 0F },
  "BoundsMax": { "X": 20F, "Y": 1F, "Z": 20F },
  "Settings": {
    "AgentRadius": 0.5F,
    "AgentHeight": 2F,
    "AgentMaxSlope": 45F
  },
  "Tiles": {
    "$values": [
      { "TileX": 0, "TileZ": 0, "PolyCount": 4, "VertCount": 8 }
    ]
  }
}
""";
}
