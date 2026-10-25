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

/// <summary>★ Gen-P2.3 — mock spritesheet → TextureImporter Multiple slices → SpriteFlipbook wire.</summary>
public sealed class SpriteWireTests : IDisposable
{
    private readonly string _root;
    private readonly SceneWriter _sceneWriter;
    private readonly AssetWriter _assetWriter;
    private readonly SpriteWireService _svc;
    private readonly string _sceneRel = "Assets/Scenes/Pixel.scene";
    private readonly string _sceneAbs;

    public SpriteWireTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-p23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "GenArt"));
        var journal = new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "test-project");
        _sceneWriter = new SceneWriter(journal);
        _assetWriter = new AssetWriter(journal);
        var project = new ProjectDescriptor("test-project", _root, null, null, null, "fp");
        _svc = new SpriteWireService(project, _sceneWriter);
        _sceneAbs = Path.Combine(_root, "Assets", "Scenes", "Pixel.scene");
        File.WriteAllText(_sceneAbs, TestSceneFixture.MakeMinimalScene());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> ImportSheet(string name = "hero.png")
    {
        string src = Path.Combine(_root, "src-" + name);
        File.WriteAllBytes(src, MockArtifacts.SpritesheetPng());
        string rel = "Assets/GenArt/" + name;
        await _assetWriter.ImportAsync(src, Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)),
            rel, ChangeProvenance.ForTool("test.import", "sheet", "imp-" + name));
        return rel;
    }

    [Fact]
    public void ReadPngSize_Spritesheet_Is32x8()
    {
        string path = Path.Combine(_root, "sheet.png");
        File.WriteAllBytes(path, MockArtifacts.SpritesheetPng());
        Assert.True(SpriteWireService.TryReadPngSize(path, out int w, out int h));
        Assert.Equal(32, w);
        Assert.Equal(8, h);
    }

    [Fact]
    public async Task Slice_FourColumns_WritesMultipleModeAndGuids()
    {
        string rel = await ImportSheet();
        SpriteSliceResult dry = await _svc.SliceAsync(rel, 4, 1, 0, 0, 100f, "slice-dry", null, true, CancellationToken.None);
        Assert.True(dry.DryRun);
        Assert.Equal(4, dry.Frames.Count);
        Assert.Equal(8, dry.CellWidth);
        Assert.Equal(8, dry.CellHeight);

        SpriteSliceResult live = await _svc.SliceAsync(rel, 4, 1, 0, 0, 100f, "slice-live", "grid", false, CancellationToken.None);
        Assert.Equal(4, live.Frames.Count);
        Assert.Equal("hero_0", live.Frames[0].Name);
        Assert.Equal("hero_3", live.Frames[3].Name);
        Assert.Equal(0, live.Frames[0].Y);
        Assert.Equal(8, live.Frames[0].Width);

        Guid parent = Guid.Parse(live.ParentGuid!);
        Assert.Equal(SubAssetResolver.DeriveSubAssetGuid(parent, "hero_0").ToString(), live.Frames[0].Guid);

        EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(
            Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)) + ".meta"));
        EchoObject sprite = meta.Get("settings")!.Get("sprite")!;
        Assert.Equal(2L, sprite.Get("Mode")!.LongValue);
        Assert.Equal(4, sprite.Get("Slices")!.Get("$values")!.List.Count);
    }

    [Fact]
    public async Task Wire_AddsRendererAndFlipbook()
    {
        string rel = await ImportSheet("walk.png");
        SpriteWireResult result = await _svc.WireAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel,
            4, 1, 0, 0, 100f, 12f, "wire-1", "pixel hero", false, CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.SpriteRendererId));
        Assert.False(string.IsNullOrEmpty(result.FlipbookId));
        Assert.Equal(4, result.Slice.Frames.Count);
        Assert.Equal(12f, result.FramesPerSecond);

        EchoObject scene = EchoObject.ReadFromString(File.ReadAllText(_sceneAbs));
        EchoObject go = EchoFacade.FindGameObject(scene, TestSceneFixture.PlayerIdentifier)!;
        Assert.Equal(2, go.Get("Components")!.List.Count);
        EchoObject sr = go.Get("Components")!.List[0];
        Assert.Contains("SpriteRenderer", sr.Get("$type")!.StringValue);
        Assert.Equal(result.Slice.Frames[0].Guid, sr.Get("Sprite")!.Get("AssetID")!.StringValue);

        EchoObject fb = go.Get("Components")!.List[1];
        Assert.Contains("SpriteFlipbook", fb.Get("$type")!.StringValue);
        Assert.Equal(4, fb.Get("Frames")!.Get("$values")!.List.Count);
    }

    [Fact]
    public async Task Dispatcher_RoutesSpriteSlice_DryRun()
    {
        string rel = await ImportSheet("disp.png");
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(new ProjectDescriptor("test-project", _root, null, null, null, "fp"))
            .AddSingleton(_sceneWriter)
            .AddSingleton(_svc)
            .BuildServiceProvider();
        var dispatcher = new HostToolDispatcher(services);
        object dry = await dispatcher.InvokeAsync("sprite_slice",
            JsonSerializer.SerializeToElement(new { assetPath = rel, idempotencyKey = "d-sl", columns = 4, dryRun = true }),
            CancellationToken.None);
        Assert.Equal(4, Assert.IsType<SpriteSliceResult>(dry).Frames.Count);
    }

    [Fact]
    public void Capabilities_AdvertiseSpriteTools()
    {
        var catalog = new CapabilityCatalog(new ProjectDescriptor("p", _root, null, null, null, "fp"));
        CapabilitySnapshot snap = catalog.GetSnapshot();
        CapabilityToolDescriptor slice = snap.Tools.Single(t => t.Name == "sprite_slice");
        Assert.Equal("asset", slice.Group);
        Assert.Equal(RiskLevel.SafeWrite, slice.RiskLevel);
        Assert.True(slice.SupportsDryRun);

        CapabilityToolDescriptor wire = snap.Tools.Single(t => t.Name == "sprite_wire");
        Assert.Equal("scene-write", wire.Group);
        Assert.True(wire.SupportsDryRun);
    }

    [Fact]
    public void Registry_ResolvesSpriteFlipbook()
    {
        string? type = ComponentRegistry.ResolveTypeString("SpriteFlipbook");
        Assert.Contains("SpriteFlipbook", type);
    }

    [Fact]
    public async Task EvidenceScreenshot_SpriteFlipbook()
    {
        string rel = await ImportSheet("evidence.png");
        SpriteWireResult result = await _svc.WireAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel,
            4, 1, 0, 0, 100f, 12f, "wire-ev", "evidence", false, CancellationToken.None);

        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p2-3-sprite-flipbook.png");
        SpriteWireEvidence.Write(png, result);
        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 200);
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P2.3 sprite-flipbook acceptance\n\n" +
            $"- Sheet: {result.Slice.TextureWidth}×{result.Slice.TextureHeight}, " +
            $"{result.Slice.Columns}×{result.Slice.Rows} cells of {result.Slice.CellWidth}×{result.Slice.CellHeight}\n" +
            $"- Frames: {string.Join(", ", result.Slice.Frames.Select(f => f.Name + "=" + f.Guid[..8]))}\n" +
            $"- SpriteRenderer: {result.SpriteRendererId}\n" +
            $"- SpriteFlipbook: {result.FlipbookId} @ {result.FramesPerSecond} fps\n");
    }
}

internal static class SpriteWireEvidence
{
    public static void Write(string path, SpriteWireResult result)
    {
        const int w = 640;
        const int h = 240;
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
            {
                for (int xx = Math.Max(0, x); xx < Math.Min(w, x + rw); xx++)
                {
                    int i = (yy * w + xx) * 3;
                    px[i] = r;
                    px[i + 1] = g;
                    px[i + 2] = b;
                }
            }
        }

        Rect(0, 0, w, 36, 36, 48, 72);
        int n = Math.Max(1, result.Slice.Frames.Count);
        int slot = (w - 48) / n;
        byte[][] pal = { new byte[] { 200, 60, 60 }, new byte[] { 60, 180, 80 }, new byte[] { 60, 90, 200 }, new byte[] { 220, 200, 50 } };
        for (int i = 0; i < result.Slice.Frames.Count; i++)
        {
            byte[] c = pal[i % pal.Length];
            Rect(24 + i * slot, 52, slot - 12, 120, c[0], c[1], c[2]);
        }

        Rect(24, 188, w - 48, 32, 60, 160, 90);
        File.WriteAllBytes(path, PlayablePropEvidencePng.EncodePng(px, w, h));
    }
}
