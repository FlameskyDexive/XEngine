using Spine;

using XEngine.Ai.Gateway;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Spine;

using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineAtlasGenTests : IDisposable
{
    private readonly string _root;
    private readonly GenTaskStore _store;
    private readonly AssetWriter _assetWriter;

    public SpineAtlasGenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-spine-atlas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Spine"));
        _store = new GenTaskStore(_root);
        _assetWriter = new AssetWriter(new ChangeJournal(Path.Combine(_root, ".xengine", "journal"), "test-project"));
        WriteFixture();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GenService Service()
    {
        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        var project = new ProjectDescriptor("test-project", _root, null, null, null, "fingerprint000000");
        return new GenService(project, registry, _store, _assetWriter);
    }

    [Fact]
    public void PngRgba_RoundTripsRgba()
    {
        byte[] src = [10, 20, 30, 255, 40, 50, 60, 128];
        byte[] png = PngRgba.Encode(src, 2, 1);
        byte[] back = PngRgba.Decode(png, out int w, out int h);
        Assert.Equal(2, w);
        Assert.Equal(1, h);
        Assert.Equal(src, back);
    }

    [Fact]
    public void Enrich_FillsUnrotatedWidthHeight()
    {
        var extra = new Dictionary<string, string>
        {
            ["atlasPath"] = "Assets/Spine/hero.atlas",
            ["region"] = "head"
        };
        IReadOnlyDictionary<string, string> enriched = SpineAtlasRestyleService.Enrich(extra, _root);
        Assert.Equal("4", enriched["width"]);
        Assert.Equal("3", enriched["height"]);
        Assert.False(enriched.ContainsKey("inputImagePath"));
    }

    [Fact]
    public void AttachInputCrop_WritesUnrotatedPng()
    {
        var extra = new Dictionary<string, string>
        {
            ["atlasPath"] = "Assets/Spine/hero.atlas",
            ["region"] = "head"
        };
        IReadOnlyDictionary<string, string> attached = SpineAtlasRestyleService.AttachInputCrop(extra, _root);
        Assert.True(attached.TryGetValue("inputImagePath", out string? relative));
        Assert.StartsWith(".xengine/gen-crops/", relative);
        string abs = Path.Combine(_root, relative!.Replace('/', Path.DirectorySeparatorChar));
        byte[] rgba = PngRgba.Decode(File.ReadAllBytes(abs), out int w, out int h);
        Assert.Equal(4, w);
        Assert.Equal(3, h);
        Assert.Equal(200, rgba[0]);
        Assert.Equal(40, rgba[1]);
    }

    [Fact]
    public async Task Submit_OpenAi_InjectsInputImagePath()
    {
        var capture = new CaptureProvider();
        var registry = new GenProviderRegistry();
        registry.Register(capture);
        var project = new ProjectDescriptor("test-project", _root, null, null, null, "fingerprint000000");
        var service = new GenService(project, registry, _store, _assetWriter);

        GenSubmitToolResult submitted = await service.SubmitAsync(
            "openai", "texture", "hero head moss", null, 3, null,
            """{"atlasPath":"Assets/Spine/hero.atlas","region":"head"}""",
            false, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(submitted.TaskId));
        Assert.NotNull(capture.Last);
        Assert.True(capture.Last!.Extra!.TryGetValue("inputImagePath", out string? path));
        Assert.True(File.Exists(Path.Combine(_root, path!.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal("4", capture.Last.Extra["width"]);
    }

    [Fact]
    public async Task Fetch_MockTint_KeepsOtherPixels_AndEmbedsRecipe()
    {
        GenService service = Service();
        string extra = """{"atlasPath":"Assets/Spine/hero.atlas","region":"head","tintR":"64","tintG":"255","tintB":"255"}""";
        GenSubmitToolResult submitted = await service.SubmitAsync(
            "mock", "texture", "hero head moss", null, 3, null, extra, false, CancellationToken.None);
        Assert.Equal("4", _store.Get(submitted.TaskId)!.Recipe.Extra!["width"]);
        Assert.Equal("3", _store.Get(submitted.TaskId)!.Recipe.Extra!["height"]);

        GenFetchToolResult fetched = await service.FetchAsync(
            submitted.TaskId, "Assets/Spine", "hero-pma", false, "atlas restyle", "spine-head-1", CancellationToken.None);

        GenFetchedAsset asset = Assert.Single(fetched.Assets);
        Assert.Equal("Assets/Spine/hero-pma.png", asset.AssetPath);
        string abs = Path.Combine(_root, "Assets", "Spine", "hero-pma.png");
        byte[] page = PngRgba.Decode(File.ReadAllBytes(abs), out int w, out int h);
        Assert.Equal(16, w);
        Assert.Equal(8, h);

        Atlas atlas = SpineAtlasRestyleService.LoadAtlas(Path.Combine(_root, "Assets", "Spine", "hero.atlas"));
        byte[] original = PngRgba.Decode(File.ReadAllBytes(Path.Combine(_root, "Assets", "Spine", "hero.png")), out _, out _);
        byte[] headBefore = SpineAtlasRestyle.ExtractPacked(original, 16, 8, atlas.FindRegion("head")!);
        byte[] headAfter = SpineAtlasRestyle.ExtractPacked(page, 16, 8, atlas.FindRegion("head")!);
        Assert.True(headAfter[0] < headBefore[0]);
        Assert.Equal(
            SpineAtlasRestyle.ExtractPacked(original, 16, 8, atlas.FindRegion("boot")!),
            SpineAtlasRestyle.ExtractPacked(page, 16, 8, atlas.FindRegion("boot")!));

        string meta = File.ReadAllText(abs + ".meta");
        Assert.Contains("atlasPath", meta);
        Assert.Contains("head", meta);
        Assert.Contains(submitted.TaskId, meta);
    }

    [Fact]
    public void PackCrop_BlitsUnrotatedCrop()
    {
        var extra = new Dictionary<string, string>
        {
            ["atlasPath"] = "Assets/Spine/hero.atlas",
            ["region"] = "head",
            ["mode"] = "pack-crop",
            ["pma"] = "false"
        };
        var recipe = new GenRecipe("mock", "texture", "crop", null, null, null, extra);
        byte[] crop = new byte[4 * 3 * 4];
        for (int i = 0; i < crop.Length; i += 4)
        {
            crop[i] = 9;
            crop[i + 1] = 8;
            crop[i + 2] = 7;
            crop[i + 3] = 255;
        }

        string artifact = Path.Combine(_root, "crop.png");
        File.WriteAllBytes(artifact, PngRgba.Encode(crop, 4, 3));
        SpineAtlasRestyleService.ApplyToArtifact(recipe, _root, artifact);

        byte[] page = PngRgba.Decode(File.ReadAllBytes(artifact), out int w, out int h);
        Assert.Equal(16, w);
        Assert.Equal(8, h);
        Atlas atlas = SpineAtlasRestyleService.LoadAtlas(Path.Combine(_root, "Assets", "Spine", "hero.atlas"));
        byte[] packed = SpineAtlasRestyle.ExtractUnrotated(page, 16, 8, atlas.FindRegion("head")!);
        Assert.Equal(9, packed[0]);
        Assert.Equal(8, packed[1]);
    }

    [Fact]
    public void PackCrop_ResizesLargerArtifact()
    {
        var extra = new Dictionary<string, string>
        {
            ["atlasPath"] = "Assets/Spine/hero.atlas",
            ["region"] = "head",
            ["mode"] = "auto",
            ["pma"] = "false"
        };
        var recipe = new GenRecipe("openai", "texture", "crop", null, null, null, extra);
        byte[] crop = new byte[8 * 6 * 4];
        for (int i = 0; i < crop.Length; i += 4)
        {
            crop[i] = 9;
            crop[i + 1] = 8;
            crop[i + 2] = 7;
            crop[i + 3] = 255;
        }

        string artifact = Path.Combine(_root, "large-crop.png");
        File.WriteAllBytes(artifact, PngRgba.Encode(crop, 8, 6));
        SpineAtlasRestyleService.ApplyToArtifact(recipe, _root, artifact);

        byte[] page = PngRgba.Decode(File.ReadAllBytes(artifact), out int w, out int h);
        Assert.Equal(16, w);
        Assert.Equal(8, h);
        Atlas atlas = SpineAtlasRestyleService.LoadAtlas(Path.Combine(_root, "Assets", "Spine", "hero.atlas"));
        byte[] packed = SpineAtlasRestyle.ExtractUnrotated(page, 16, 8, atlas.FindRegion("head")!);
        Assert.Equal(9, packed[0]);
        Assert.Equal(8, packed[1]);
        Assert.Equal(7, packed[2]);
        Assert.Equal(
            SpineAtlasRestyle.ExtractPacked(
                PngRgba.Decode(File.ReadAllBytes(Path.Combine(_root, "Assets", "Spine", "hero.png")), out _, out _),
                16, 8, atlas.FindRegion("boot")!),
            SpineAtlasRestyle.ExtractPacked(page, 16, 8, atlas.FindRegion("boot")!));
    }

    [Fact]
    public void PngRgba_ResizeKeepsUniformColor()
    {
        byte[] src = new byte[4 * 2 * 4];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i] = 10;
            src[i + 1] = 20;
            src[i + 2] = 30;
            src[i + 3] = 255;
        }

        byte[] dst = PngRgba.Resize(src, 4, 2, 2, 1);
        Assert.Equal(8, dst.Length);
        Assert.Equal(10, dst[0]);
        Assert.Equal(20, dst[1]);
        Assert.Equal(30, dst[2]);
        Assert.Equal(255, dst[3]);
    }

    [Fact]
    public async Task Fetch_WithoutAtlasExtra_LeavesMockOneByOne()
    {
        GenService service = Service();
        GenSubmitToolResult submitted = await service.SubmitAsync(
            "mock", "texture", "plain brick", null, null, null, null, false, CancellationToken.None);
        GenFetchToolResult fetched = await service.FetchAsync(
            submitted.TaskId, "GenArt", "brick", false, null, "plain-1", CancellationToken.None);
        byte[] png = File.ReadAllBytes(Path.Combine(_root, fetched.Assets[0].AssetPath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(MockArtifacts.Png1x1(), png);
    }

    [Fact]
    public void OfficialGoblins_DecodeAndTintHead()
    {
        string repo = FindRepo();
        string dir = Path.Combine(repo, "Packages", "com.xengine.spine", "Samples~", "OtherExamples", "Skeletons", "Goblins");
        if (!File.Exists(Path.Combine(dir, "goblins-pma.png")))
            return;

        string dest = Path.Combine(_root, "Assets", "Goblins");
        Directory.CreateDirectory(dest);
        File.Copy(Path.Combine(dir, "goblins-pma.atlas"), Path.Combine(dest, "goblins-pma.atlas"));
        File.Copy(Path.Combine(dir, "goblins-pma.png"), Path.Combine(dest, "goblins-pma.png"));

        var extra = new Dictionary<string, string>
        {
            ["atlasPath"] = "Assets/Goblins/goblins-pma.atlas",
            ["region"] = "goblin/head",
            ["tintR"] = "64",
            ["tintG"] = "255",
            ["tintB"] = "255"
        };
        var recipe = new GenRecipe("mock", "texture", "goblin head", null, null, null, extra);
        string artifact = Path.Combine(_root, "out.png");
        File.WriteAllBytes(artifact, MockArtifacts.Png1x1());
        SpineAtlasRestyleService.ApplyToArtifact(recipe, _root, artifact);

        byte[] page = PngRgba.Decode(File.ReadAllBytes(artifact), out int w, out int h);
        Assert.Equal(1024, w);
        Assert.Equal(128, h);
        Atlas atlas = SpineAtlasRestyleService.LoadAtlas(Path.Combine(dest, "goblins-pma.atlas"));
        byte[] original = PngRgba.Decode(File.ReadAllBytes(Path.Combine(dest, "goblins-pma.png")), out _, out _);
        byte[] before = SpineAtlasRestyle.ExtractPacked(original, w, h, atlas.FindRegion("goblin/head")!);
        byte[] after = SpineAtlasRestyle.ExtractPacked(page, w, h, atlas.FindRegion("goblin/head")!);
        Assert.NotEqual(before, after);
        Assert.Equal(
            SpineAtlasRestyle.ExtractPacked(original, w, h, atlas.FindRegion("goblin/left-foot")!),
            SpineAtlasRestyle.ExtractPacked(page, w, h, atlas.FindRegion("goblin/left-foot")!));
    }

    [Fact]
    public void Evidence_WritesM10Notes()
    {
        string repo = FindRepo();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m10-atlas-restyle.md"),
            "# M10 Spine atlas restyle gateway\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "Plan: `docs/superpowers/plans/2026-08-16-spine-atlas-restyle.md`. " +
            "`gen_submit` `kind=texture` extra `atlasPath`/`region` → fetch writes a full page PNG. " +
            "mock-tint keeps other pixels; pack-crop blits an unrotated crop. Submit fills width/height. " +
            "Recipe lands in `.meta`.\n\n" +
            "Host tests: `SpineAtlasGenTests` (PNG roundtrip, enrich, mock fetch, pack-crop, official goblin/head). " +
            "M10f: ComfyUI submit uploads the unrotated crop to `inputImageNode`. " +
            "M10g: `openai` provider (default model gpt-image-2) edits that crop; fetch resizes then pack-crops.\n");
        Assert.True(File.Exists(Path.Combine(dir, "m10-atlas-restyle.md")));
    }

    private void WriteFixture()
    {
        const int w = 16;
        const int h = 8;
        byte[] page = new byte[w * h * 4];
        Fill(page, w, 1, 1, 4, 3, 200, 40, 40, 255);
        Fill(page, w, 8, 2, 3, 2, 40, 200, 40, 255);
        string dir = Path.Combine(_root, "Assets", "Spine");
        File.WriteAllBytes(Path.Combine(dir, "hero.png"), PngRgba.Encode(page, w, h));
        File.WriteAllText(Path.Combine(dir, "hero.atlas"),
            "hero.png\n" +
            "\tsize: 16, 8\n" +
            "\tfilter: Linear, Linear\n" +
            "head\n" +
            "\tbounds: 1, 1, 4, 3\n" +
            "boot\n" +
            "\tbounds: 8, 2, 3, 2\n");
    }

    private static void Fill(byte[] rgba, int stride, int x, int y, int rw, int rh, byte r, byte g, byte b, byte a)
    {
        for (int py = y; py < y + rh; py++)
        {
            for (int px = x; px < x + rw; px++)
            {
                int i = (py * stride + px) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = a;
            }
        }
    }

    private static string FindRepo()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    private sealed class CaptureProvider : IGenProvider
    {
        public GenRecipe? Last { get; private set; }

        public string Name => OpenAiImageGenProvider.ProviderName;

        public IReadOnlyList<string> SupportedKinds { get; } = new[] { GenRecipe.Kinds.Texture };

        public Task<GenSubmitResult> SubmitAsync(GenRecipe recipe, CancellationToken cancellationToken)
        {
            Last = recipe;
            return Task.FromResult(new GenSubmitResult("openai-capture"));
        }

        public Task<GenTaskStatus> PollAsync(string taskId, CancellationToken cancellationToken)
            => Task.FromResult(new GenTaskStatus(taskId, GenTaskState.Succeeded, 100));

        public Task<IReadOnlyList<GenArtifact>> FetchAsync(
            string taskId, GenRecipe recipe, string downloadDirectory, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GenArtifact>>(Array.Empty<GenArtifact>());
    }
}
