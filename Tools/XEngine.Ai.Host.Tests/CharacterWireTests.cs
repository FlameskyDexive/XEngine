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

/// <summary>★ Gen-P2.2 — mock motion GLB → clip GUIDs → AnimatorController + scene Animator.</summary>
public sealed class CharacterWireTests : IDisposable
{
    private readonly string _root;
    private readonly SceneWriter _sceneWriter;
    private readonly AssetWriter _assetWriter;
    private readonly CharacterWireService _svc;
    private readonly string _sceneRel = "Assets/Scenes/Dojo.scene";
    private readonly string _sceneAbs;

    public CharacterWireTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-p22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "GenArt"));
        var journal = new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "test-project");
        _sceneWriter = new SceneWriter(journal);
        _assetWriter = new AssetWriter(journal);
        var project = new ProjectDescriptor("test-project", _root, null, null, null, "fp");
        _svc = new CharacterWireService(project, _sceneWriter);
        _sceneAbs = Path.Combine(_root, "Assets", "Scenes", "Dojo.scene");
        File.WriteAllText(_sceneAbs, TestSceneFixture.MakeMinimalScene());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> ImportHero()
    {
        string src = Path.Combine(_root, "hero.glb");
        File.WriteAllBytes(src, MockArtifacts.AnimatedGlb());
        const string rel = "Assets/GenArt/hero.glb";
        await _assetWriter.ImportAsync(src, Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)),
            rel, ChangeProvenance.ForTool("test.import", "hero", "imp-hero"));
        return rel;
    }

    [Fact]
    public async Task AnimatedGlb_DerivesIdleAndWalkClips()
    {
        string rel = await ImportHero();
        SubAssetList subs = SubAssetResolver.Resolve(
            Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)), rel);
        Assert.Contains(subs.SubAssets, s => s.Kind == "AnimationClip" && s.StableId == "Idle");
        Assert.Contains(subs.SubAssets, s => s.Kind == "AnimationClip" && s.StableId == "Walk");
    }

    [Fact]
    public async Task Wire_BuildsControllerWithBothStates_AndBindsAnimator()
    {
        string rel = await ImportHero();
        CharacterWireResult result = await _svc.WireAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel,
            "Assets/Animators/Hero.controller", "wire-1", "hero loco", dryRun: false, CancellationToken.None);

        Assert.Equal(2, result.Clips.Count);
        Assert.Contains(result.Clips, c => c.StateName == "Idle");
        Assert.Contains(result.Clips, c => c.StateName == "Walk");
        Assert.False(string.IsNullOrEmpty(result.ControllerGuid));
        Assert.False(string.IsNullOrEmpty(result.AnimatorComponentId));
        Assert.DoesNotContain(result.Diagnostics, d => d.StartsWith("error", StringComparison.OrdinalIgnoreCase));

        string ctrlAbs = Path.Combine(_root, "Assets", "Animators", "Hero.controller");
        Assert.True(File.Exists(ctrlAbs));
        AnimatorInspectResult inspect = AnimationAssetOps.InspectController(File.ReadAllText(ctrlAbs), result.ControllerPath);
        Assert.Contains(inspect.States, s => s.Name == "Idle");
        Assert.Contains(inspect.States, s => s.Name == "Walk");
        Assert.Equal(result.Clips[0].StateName, inspect.DefaultStateName);
        Assert.Contains(inspect.Parameters, p => p.Name == "Speed");
        Assert.Contains(inspect.Transitions, t => t.Name == "IdleToWalk");
        Assert.Contains(inspect.Transitions, t => t.Name == "WalkToIdle");

        EchoObject scene = EchoObject.ReadFromString(File.ReadAllText(_sceneAbs));
        EchoObject go = EchoFacade.FindGameObject(scene, TestSceneFixture.PlayerIdentifier)!;
        EchoObject animator = Assert.Single(go.Get("Components")!.List);
        Assert.Contains("Animator", animator.Get("$type")!.StringValue);
        Assert.Equal(result.ControllerGuid, animator.Get("Controller")!.Get("AssetID")!.StringValue);
    }

    [Fact]
    public async Task Wire_DryRun_DoesNotWrite()
    {
        string rel = await ImportHero();
        CharacterWireResult dry = await _svc.WireAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel, "Assets/Animators/Dry.controller",
            "wire-dry", null, dryRun: true, CancellationToken.None);
        Assert.True(dry.DryRun);
        Assert.Equal(2, dry.Clips.Count);
        Assert.False(File.Exists(Path.Combine(_root, "Assets", "Animators", "Dry.controller")));
    }

    [Fact]
    public async Task Wire_ModelWithoutClips_Throws()
    {
        string src = Path.Combine(_root, "prop.glb");
        File.WriteAllBytes(src, MockArtifacts.BoxGlb());
        const string rel = "Assets/GenArt/prop.glb";
        await _assetWriter.ImportAsync(src, Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)),
            rel, ChangeProvenance.ForTool("test.import", "prop", "imp-prop"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.WireAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel, null, "wire-empty", null, false, CancellationToken.None));
        Assert.Contains("no AnimationClip", ex.Message);
    }

    [Fact]
    public async Task Dispatcher_RoutesCharacterWire_DryRun()
    {
        string rel = await ImportHero();
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(new ProjectDescriptor("test-project", _root, null, null, null, "fp"))
            .AddSingleton(_sceneWriter)
            .AddSingleton(_svc)
            .BuildServiceProvider();
        var dispatcher = new HostToolDispatcher(services);

        object dry = await dispatcher.InvokeAsync("character_wire",
            JsonSerializer.SerializeToElement(new
            {
                scenePath = _sceneRel,
                objectIdentifier = TestSceneFixture.PlayerIdentifier,
                modelAssetPath = rel,
                controllerPath = "Assets/Animators/Disp.controller",
                idempotencyKey = "wire-disp",
                dryRun = true,
            }),
            CancellationToken.None);
        CharacterWireResult result = Assert.IsType<CharacterWireResult>(dry);
        Assert.True(result.DryRun);
        Assert.Equal(2, result.Clips.Count);
    }

    [Fact]
    public void Capabilities_AdvertiseCharacterWireRiskAndDryRun()
    {
        var catalog = new CapabilityCatalog(new ProjectDescriptor("p", _root, null, null, null, "fp"));
        CapabilityToolDescriptor wire = catalog.GetSnapshot().Tools.Single(t => t.Name == "character_wire");
        Assert.Equal("animation", wire.Group);
        Assert.Equal(RiskLevel.SafeWrite, wire.RiskLevel);
        Assert.True(wire.SupportsDryRun);
    }

    [Fact]
    public async Task EvidenceScreenshot_CharacterWire()
    {
        string rel = await ImportHero();
        CharacterWireResult result = await _svc.WireAsync(
            _sceneRel, TestSceneFixture.PlayerIdentifier, rel,
            "Assets/Animators/EvidenceHero.controller", "wire-ev", "evidence", false, CancellationToken.None);

        string evidenceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gen-asset"));
        Directory.CreateDirectory(evidenceDir);
        string png = Path.Combine(evidenceDir, "2026-08-16-p2-2-character-wire.png");
        CharacterWireEvidence.Write(png, result);
        Assert.True(File.Exists(png));
        Assert.True(new FileInfo(png).Length > 200);
        File.WriteAllText(Path.ChangeExtension(png, ".md"),
            "# P2.2 character-wire acceptance\n\n" +
            $"- Clips: {string.Join(", ", result.Clips.Select(c => c.StateName + "=" + c.ClipGuid[..8]))}\n" +
            $"- Controller: {result.ControllerPath} guid={result.ControllerGuid}\n" +
            $"- Animator component: {result.AnimatorComponentId}\n" +
            $"- Diagnostics: {(result.Diagnostics.Count == 0 ? "none" : string.Join("; ", result.Diagnostics))}\n");
    }
}

internal static class CharacterWireEvidence
{
    public static void Write(string path, CharacterWireResult result)
    {
        const int w = 640;
        const int h = 280;
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

        Rect(0, 0, w, 40, 36, 48, 72);
        int n = Math.Max(1, result.Clips.Count);
        int slot = (w - 48) / n;
        for (int i = 0; i < result.Clips.Count; i++)
        {
            byte r = (byte)(70 + i * 50);
            byte g = (byte)(140 - i * 30);
            byte b = (byte)(90 + i * 40);
            Rect(24 + i * slot, 64, slot - 16, 120, r, g, b);
        }

        bool clean = result.Diagnostics.Count == 0;
        Rect(24, 210, w - 48, 40, clean ? (byte)60 : (byte)180, clean ? (byte)160 : (byte)70, (byte)90);
        File.WriteAllBytes(path, PlayablePropEvidencePng.EncodePng(px, w, h));
    }
}
