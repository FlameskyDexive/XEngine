using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using XEngine.Ai.Gateway;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;

using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Gen-P2.1 — playable-prop loop: mesh stats + AABB collider + triangle-budget remesh + prefab extract.
/// </summary>
public sealed class PlayablePropTests : IDisposable
{
    private readonly string _root;
    private readonly ChangeJournal _journal;
    private readonly SceneWriter _sceneWriter;
    private readonly AssetWriter _assetWriter;
    private readonly GenTaskStore _store;
    private readonly PlayablePropService _svc;
    private readonly string _sceneRel = "Assets/Scenes/Arena.scene";
    private readonly string _sceneAbs;

    public PlayablePropTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-p21-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "GenArt"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Prefabs"));
        _journal = new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "test-project");
        _sceneWriter = new SceneWriter(_journal);
        _assetWriter = new AssetWriter(_journal);
        _store = new GenTaskStore(_root);
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        var project = new ProjectDescriptor("test-project", _root, null, null, null, "fp");
        var gen = new GenService(project, registry, _store, _assetWriter);
        _svc = new PlayablePropService(project, _sceneWriter, _assetWriter, gen, _store);
        _sceneAbs = Path.Combine(_root, "Assets", "Scenes", "Arena.scene");
        File.WriteAllText(_sceneAbs, TestSceneFixture.MakeMinimalScene());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> ImportBox(string name = "crate.glb")
    {
        string src = Path.Combine(_root, "src-" + name);
        File.WriteAllBytes(src, MockArtifacts.BoxGlb());
        string rel = "Assets/GenArt/" + name;
        await _assetWriter.ImportAsync(src, Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)),
            rel, ChangeProvenance.ForTool("test.import", "import " + name, "imp-" + name));
        return rel;
    }

    [Fact]
    public async Task MeshStats_BoxGlb_Reports12TrianglesAndUnitAabb()
    {
        string rel = await ImportBox();
        MeshStatsResult stats = _svc.MeshStats(rel, maxTriangles: 4);

        Assert.Equal(12, stats.TotalTriangles);
        Assert.Equal(8, stats.TotalVertices);
        Assert.True(stats.OverBudget);
        Assert.True(stats.SuggestedBoxSizeX >= 0.99f && stats.SuggestedBoxSizeX <= 1.01f);
        Assert.True(stats.SuggestedBoxSizeY >= 0.99f && stats.SuggestedBoxSizeY <= 1.01f);
        Assert.True(stats.SuggestedBoxSizeZ >= 0.99f && stats.SuggestedBoxSizeZ <= 1.01f);
        Assert.Contains(stats.Meshes, m => m.Name.Contains("BoxMesh", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(stats.Meshes[0].Guid));
    }

    [Fact]
    public async Task EnsureBudget_OverCap_OverwritesWithOneTriangle_PreservesGuid()
    {
        string rel = await ImportBox();
        string abs = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        string guidBefore = ReadGuid(abs + ".meta");

        BudgetEnsureResult dry = await _svc.EnsureBudgetAsync(rel, 4, null, "budget-dry", "dry", dryRun: true, CancellationToken.None);
        Assert.True(dry.OverBudget);
        Assert.True(dry.WouldRemesh);
        Assert.False(dry.Remeshed);
        Assert.Equal(12, dry.TotalTriangles);

        BudgetEnsureResult live = await _svc.EnsureBudgetAsync(rel, 4, null, "budget-live", "decimate crate", dryRun: false, CancellationToken.None);
        Assert.True(live.Remeshed);
        Assert.Equal(1, live.TotalTriangles);
        Assert.False(live.OverBudget);
        Assert.Equal(guidBefore, ReadGuid(abs + ".meta"));
    }

    [Fact]
    public async Task AddCollider_Box_WritesSizeFromAabb()
    {
        string rel = await ImportBox();
        ColliderAddResult result = await _svc.AddColliderAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel, "box", convex: false,
            "col-box-1", "auto box", dryRun: false, expectedBeforeHash: null, CancellationToken.None);

        Assert.Equal("box", result.Shape);
        Assert.False(string.IsNullOrEmpty(result.ComponentIdentifier));
        EchoObject root = EchoObject.ReadFromString(File.ReadAllText(_sceneAbs));
        EchoObject go = EchoFacade.FindGameObject(root, TestSceneFixture.PlayerIdentifier)!;
        EchoObject collider = Assert.Single(go.Get("Components")!.List);
        Assert.Contains("BoxCollider", collider.Get("$type")!.StringValue);
        Assert.InRange(collider.Get("size")!.Get("X")!.FloatValue, 0.99f, 1.01f);
    }

    [Fact]
    public async Task AddCollider_Convex_WiresLargestMeshGuid()
    {
        string rel = await ImportBox();
        MeshStatsResult stats = _svc.MeshStats(rel, null);
        string meshGuid = Assert.Single(stats.Meshes).Guid!;

        ColliderAddResult result = await _svc.AddColliderAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel, "convex", convex: true,
            "col-convex-1", "auto convex", dryRun: false, expectedBeforeHash: null, CancellationToken.None);

        Assert.Equal("convex", result.Shape);
        EchoObject root = EchoObject.ReadFromString(File.ReadAllText(_sceneAbs));
        EchoObject go = EchoFacade.FindGameObject(root, TestSceneFixture.PlayerIdentifier)!;
        EchoObject collider = Assert.Single(go.Get("Components")!.List);
        Assert.Contains("MeshCollider", collider.Get("$type")!.StringValue);
        Assert.Equal(meshGuid, collider.Get("mesh")!.Get("AssetID")!.StringValue);
        Assert.True(collider.Get("convex")!.BoolValue);
    }

    [Fact]
    public async Task PrefabCreate_ExtractsWiredObject()
    {
        string rel = await ImportBox();
        await _sceneWriter.AddComponentAsync(_sceneAbs, _sceneRel, TestSceneFixture.PlayerIdentifier, "MeshRenderer",
            new[] { new SceneFieldPatch("Mesh/AssetID", "11111111-1111-1111-1111-111111111111") },
            ChangeProvenance.ForTool("test.add", "renderer", "add-mr-p21"), dryRun: false);
        await _svc.AddColliderAsync(_sceneRel, TestSceneFixture.PlayerIdentifier, rel, "box", false,
            "col-prefab-1", "box", false, null, CancellationToken.None);

        PrefabCreateResult prefab = await _svc.CreatePrefabAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, "Assets/Prefabs/Crate.prefab",
            "prefab-1", "extract crate", overwrite: false, dryRun: false, CancellationToken.None);

        Assert.Equal("Assets/Prefabs/Crate.prefab", prefab.PrefabPath);
        Assert.False(string.IsNullOrEmpty(prefab.Guid));
        string prefabAbs = Path.Combine(_root, "Assets", "Prefabs", "Crate.prefab");
        Assert.True(File.Exists(prefabAbs));
        Assert.True(File.Exists(prefabAbs + ".meta"));
        SceneHierarchy hierarchy = SceneReader.ReadPrefabHierarchy(prefabAbs);
        Assert.Equal("Player", Assert.Single(hierarchy.RootObjects).Name);
        Assert.Equal(2, hierarchy.RootObjects[0].Components.Count);
    }

    [Fact]
    public async Task Dispatcher_RoutesNewPlayablePropTools()
    {
        string rel = await ImportBox("disp.glb");
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(new ProjectDescriptor("test-project", _root, null, null, null, "fp"))
            .AddSingleton(_journal)
            .AddSingleton(_sceneWriter)
            .AddSingleton(_assetWriter)
            .AddSingleton(_store)
            .AddSingleton(_svc)
            .BuildServiceProvider();
        var dispatcher = new HostToolDispatcher(services);

        object stats = await dispatcher.InvokeAsync("asset_mesh_stats",
            JsonSerializer.SerializeToElement(new { assetPath = rel, maxTriangles = 4 }), CancellationToken.None);
        Assert.True(Assert.IsType<MeshStatsResult>(stats).OverBudget);

        object dry = await dispatcher.InvokeAsync("gen_ensure_budget",
            JsonSerializer.SerializeToElement(new { assetPath = rel, maxTriangles = 4, idempotencyKey = "d-bud", dryRun = true }),
            CancellationToken.None);
        Assert.True(Assert.IsType<BudgetEnsureResult>(dry).WouldRemesh);
    }

    [Fact]
    public async Task Capabilities_AdvertisePlayablePropRiskAndDryRun()
    {
        var catalog = new CapabilityCatalog(new ProjectDescriptor("p", _root, null, null, null, "fp"));
        CapabilitySnapshot snap = catalog.GetSnapshot();
        CapabilityToolDescriptor stats = snap.Tools.Single(t => t.Name == "asset_mesh_stats");
        Assert.Equal(RiskLevel.ReadOnly, stats.RiskLevel);
        Assert.False(stats.SupportsDryRun);

        CapabilityToolDescriptor budget = snap.Tools.Single(t => t.Name == "gen_ensure_budget");
        Assert.Equal(RiskLevel.SafeWrite, budget.RiskLevel);
        Assert.True(budget.SupportsDryRun);

        CapabilityToolDescriptor collider = snap.Tools.Single(t => t.Name == "scene_collider_add");
        Assert.Equal(RiskLevel.SafeWrite, collider.RiskLevel);
        Assert.True(collider.SupportsDryRun);

        CapabilityToolDescriptor prefab = snap.Tools.Single(t => t.Name == "prefab_create");
        Assert.Equal(RiskLevel.SafeWrite, prefab.RiskLevel);
        Assert.True(prefab.SupportsDryRun);
    }

    [Fact]
    public async Task EvidenceScreenshot_PlayablePropLoop()
    {
        string rel = await ImportBox("evidence.glb");
        MeshStatsResult before = _svc.MeshStats(rel, 4);
        BudgetEnsureResult budget = await _svc.EnsureBudgetAsync(rel, 4, null, "ev-budget", "evidence remesh", false, CancellationToken.None);
        MeshStatsResult after = budget.Stats;

        SceneWriteResult created = await _sceneWriter.CreateAsync(
            _sceneAbs, _sceneRel, "Crate", null,
            ChangeProvenance.ForTool("test.create", "host", "ev-go"), dryRun: false);
        string goId = created.CreatedIdentifier!;
        MeshStatEntry mesh = after.Meshes[0];
        await _sceneWriter.AddComponentAsync(_sceneAbs, _sceneRel, goId, "MeshRenderer",
            new[]
            {
                new SceneFieldPatch("Mesh/AssetID", mesh.Guid ?? ""),
                new SceneFieldPatch("Materials/$values/0/AssetID", "00000000-0000-0000-0000-000000000000"),
            },
            ChangeProvenance.ForTool("test.add", "mr", "ev-mr"), dryRun: false);
        ColliderAddResult collider = await _svc.AddColliderAsync(
            _sceneRel, goId, rel, "box", false, "ev-col", "box", false, null, CancellationToken.None);
        PrefabCreateResult prefab = await _svc.CreatePrefabAsync(
            _sceneRel, goId, "Assets/Prefabs/EvidenceCrate.prefab", "ev-prefab", "extract", false, false,
            CancellationToken.None);

        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p2-1-playable-prop.png");
        PlayablePropEvidencePng.Write(png, before, after, collider, prefab);

        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 200);
        Assert.Equal(1, after.TotalTriangles);
        Assert.False(string.IsNullOrEmpty(prefab.Guid));
    }

    private static string ReadGuid(string metaPath)
    {
        EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
        return meta.Get("guid")!.StringValue;
    }
}

/// <summary>Tiny 24-bit BMP so the P2.1 acceptance shot does not need Magick.NET / Skia.</summary>
internal static class PlayablePropEvidencePng
{
    public static void Write(string path, MeshStatsResult before, MeshStatsResult after, ColliderAddResult collider, PrefabCreateResult prefab)
    {
        const int w = 720;
        const int h = 400;
        var px = new byte[w * h * 3];
        Fill(px, w, h, 24, 26, 32);

        // Title bar
        Rect(px, w, h, 0, 0, w, 48, 36, 48, 72);
        // Before / after panels
        Rect(px, w, h, 24, 64, 328, 200, 40, 44, 54);
        Rect(px, w, h, 368, 64, 328, 200, 40, 44, 54);

        // AABB squares (XY projection), scaled into each panel
        DrawAabb(px, w, h, 24, 64, 328, 200, before, 220, 80, 70);
        DrawAabb(px, w, h, 368, 64, 328, 200, after, 70, 180, 110);

        // Budget bar
        int barY = 284;
        Rect(px, w, h, 24, barY, 672, 28, 50, 54, 64);
        int cap = Math.Max(before.MaxTriangles ?? 4, 1);
        int fill = Math.Min(672, 672 * before.TotalTriangles / Math.Max(before.TotalTriangles, cap));
        Rect(px, w, h, 24, barY, fill, 28, 200, 70, 70);
        int ok = Math.Min(672, 672 * after.TotalTriangles / Math.Max(before.TotalTriangles, cap));
        Rect(px, w, h, 24, barY + 36, ok, 20, 60, 160, 90);

        // Footer: collider + prefab confirmation strips
        Rect(px, w, h, 24, 352, 328, 32, 60, 90, 140);
        Rect(px, w, h, 368, 352, 328, 32, 90, 70, 140);

        // Encode as PNG (true PNG, not BMP) so the evidence folder stays image-previewable.
        File.WriteAllBytes(path, EncodePng(px, w, h));

        string md = Path.ChangeExtension(path, ".md");
        File.WriteAllText(md,
            "# P2.1 playable-prop acceptance\n\n" +
            $"- Before remesh: {before.TotalTriangles} tris / {before.TotalVertices} verts, OverBudget={before.OverBudget}\n" +
            $"- After remesh: {after.TotalTriangles} tris, Remeshed parent GUID preserved via overwrite\n" +
            $"- Collider: {collider.Shape} id={collider.ComponentIdentifier} size=" +
            $"{collider.Stats.SuggestedBoxSizeX.ToString("0.###", CultureInfo.InvariantCulture)}³\n" +
            $"- Prefab: {prefab.PrefabPath} guid={prefab.Guid}\n");
    }

    private static void DrawAabb(byte[] px, int w, int h, int x, int y, int pw, int ph, MeshStatsResult stats, byte r, byte g, byte b)
    {
        float sx = Math.Max(stats.CombinedSizeX, 0.01f);
        float sy = Math.Max(stats.CombinedSizeY, 0.01f);
        int box = Math.Min(pw, ph) - 48;
        int bw = Math.Max(8, (int)(box * Math.Min(sx, 2f) / 2f));
        int bh = Math.Max(8, (int)(box * Math.Min(sy, 2f) / 2f));
        int cx = x + pw / 2;
        int cy = y + ph / 2;
        Rect(px, w, h, cx - bw / 2, cy - bh / 2, bw, bh, r, g, b);
    }

    private static void Fill(byte[] px, int w, int h, byte r, byte g, byte b)
    {
        for (int i = 0; i < w * h; i++)
        {
            px[i * 3] = r;
            px[i * 3 + 1] = g;
            px[i * 3 + 2] = b;
        }
    }

    private static void Rect(byte[] px, int w, int h, int x, int y, int rw, int rh, byte r, byte g, byte b)
    {
        int x1 = Math.Clamp(x, 0, w);
        int y1 = Math.Clamp(y, 0, h);
        int x2 = Math.Clamp(x + rw, 0, w);
        int y2 = Math.Clamp(y + rh, 0, h);
        for (int yy = y1; yy < y2; yy++)
        {
            int row = yy * w;
            for (int xx = x1; xx < x2; xx++)
            {
                int i = (row + xx) * 3;
                px[i] = r;
                px[i + 1] = g;
                px[i + 2] = b;
            }
        }
    }

    internal static byte[] EncodePng(byte[] rgb, int w, int h)
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
        WriteBe(b, 0, w);
        WriteBe(b, 4, h);
        b[8] = 8;
        b[9] = 2;
        return b;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        WriteBeStream(s, data.Length);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, data);
        WriteBeStream(s, (int)crc);
    }

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static void WriteBeStream(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte x in a) c = CrcTab[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in b) c = CrcTab[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTab = MakeCrc();

    private static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }
}
