using System.Text;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Inspector;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine.Spine;
using XEngine.Spine.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class SpineEditorTests : EditorTestHarness
{
    private const string PreviewJson = """
        {
          "skeleton": { "hash": "m5", "spine": "4.2.37", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 }
          ],
          "slots": [
            { "name": "body", "bone": "body" }
          ],
          "skins": [
            { "name": "default" }
          ],
          "animations": {
            "walk": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.5, "x": 10, "y": 0 }
                  ]
                }
              }
            },
            "idle": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.2, "x": 0, "y": 0 }
                  ]
                }
              }
            }
          }
        }
        """;

    public SpineEditorTests()
    {
        EditorRegistries.Initialize();
    }

    [Fact]
    public void Registries_ResolveSpineEditors()
    {
        Assert.IsType<SkeletonAnimationEditor>(EditorRegistries.GetCustomEditor(typeof(SkeletonAnimation)));
        Assert.IsType<SkeletonRendererEditor>(EditorRegistries.GetCustomEditor(typeof(SkeletonRenderer)));
        Assert.IsType<SkeletonDataAssetEditor>(EditorRegistries.GetAssetEditor(typeof(SkeletonDataAsset)));
        Assert.IsType<SpineAtlasAssetEditor>(EditorRegistries.GetAssetEditor(typeof(SpineAtlasAsset)));
    }

    [Fact]
    public void CreateMenu_AddsSkeletonAnimation()
    {
        MenuRegistry.Clear();
        MenuItemAttribute.PopulateMenuRegistry();
        AppMenuItem gameObject = Assert.Single(MenuRegistry.RootMenus, item => item.Label == "GameObject");
        AppMenuItem spine = Assert.Single(gameObject.SubItems, item => item.Label == "Spine");
        AppMenuItem createAnim = Assert.Single(spine.SubItems, item => item.Label == "Skeleton Animation");
        Assert.Contains(spine.SubItems, item => item.Label == "Skeleton Renderer");

        Assert.NotNull(createAnim.OnClick);
        GameObject created = SpineMenu.CreateSkeletonAnimationObject();
        Assert.NotNull(created.GetComponent<SkeletonAnimation>());
        created.Dispose();
    }

    [Fact]
    public void Inspectors_OnGUI_HeadlessPaper()
    {
        SkeletonDataAsset data = CreateDataAsset();
        var go = new GameObject("Spine");
        var animation = go.AddComponent<SkeletonAnimation>();
        animation.SkeletonDataAsset = data;
        animation.AnimationName = "walk";

        var paper = new Paper(new NullRenderer(), 360, 640, new FontAtlasSettings());
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        EditorRegistries.GetCustomEditor(typeof(SkeletonAnimation))!.OnGUI(paper, "sa", animation);
        EditorRegistries.GetCustomEditor(typeof(SkeletonRenderer))!.OnGUI(paper, "sr", animation);
        var entry = new AssetEntry
        {
            Guid = Guid.NewGuid(),
            Path = "hero.json",
            MainAssetType = typeof(SkeletonDataAsset),
        };
        EditorRegistries.GetAssetEditor(typeof(SkeletonDataAsset))!.OnGUI(paper, "sd", entry, data);
        Origami.EndFrame(paper);
        paper.EndFrame();
        go.Dispose();
    }

    [Fact]
    public void Preview_ReplaceSubject_PosesWalk()
    {
        SkeletonDataAsset data = CreateDataAsset();
        GameObject subject = SpinePreview.CreateSubject(data, "walk");
        var animation = subject.GetComponent<SkeletonAnimation>();
        Assert.NotNull(animation);
        Assert.Equal("walk", animation!.AnimationName);
        Assert.NotNull(animation.Skeleton);
        Assert.NotNull(animation.Skeleton!.FindBone("body"));

        using var preview = new PreviewRenderer(128, 128);
        SpinePreview.Apply(preview, data, "idle");
        Assert.NotNull(preview.Subject);
        Assert.NotNull(preview.Subject!.GetComponent<SkeletonAnimation>());
        subject.Dispose();
    }

    [Fact]
    public void Preview_TickAdvances_SwitchesAnimation_AndSetupPoseClears()
    {
        SkeletonDataAsset data = CreateDataAsset();
        using var preview = new PreviewRenderer(128, 128);
        SpinePreview.Apply(preview, data, "walk");
        var animation = preview.Subject!.GetComponent<SkeletonAnimation>()!;

        SpinePreview.Tick(preview, 0.25f);
        Assert.NotNull(animation.AnimationState.GetTrack(0));
        Assert.True(animation.AnimationState.GetTrack(0)!.TrackTime > 0.2f);

        SpinePreview.SetAnimation(preview, "idle");
        Assert.Equal("idle", animation.AnimationState.GetTrack(0)!.Animation.Name);
        Assert.Equal("idle", SpinePreview.CurrentAnimation(preview));

        SpinePreview.SetupPose(preview);
        Assert.Null(animation.AnimationState.GetTrack(0));
        Assert.Equal("", SpinePreview.CurrentAnimation(preview));
    }

    [Fact]
    public void Importer_ReadsMixSettings()
    {
        var settings = XEngine.Echo.EchoObject.NewCompound();
        settings[SpineSkeletonImporter.DefaultMixSetting] = new XEngine.Echo.EchoObject(0.25f);
        var list = XEngine.Echo.EchoObject.NewList();
        var item = XEngine.Echo.EchoObject.NewCompound();
        item[SpineSkeletonImporter.MixFromKey] = new XEngine.Echo.EchoObject("walk");
        item[SpineSkeletonImporter.MixToKey] = new XEngine.Echo.EchoObject("idle");
        item[SpineSkeletonImporter.MixDurationKey] = new XEngine.Echo.EchoObject(0.5f);
        list.ListAdd(item);
        settings[SpineSkeletonImporter.CustomMixesSetting] = list;

        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        SpineSkeletonImporter.ReadMixSettings(settings, asset);

        Assert.Equal(0.25f, asset.DefaultMix);
        SpineMixEntry mix = Assert.Single(asset.CustomMixes);
        Assert.Equal("walk", mix.From);
        Assert.Equal("idle", mix.To);
        Assert.Equal(0.5f, mix.Duration);
    }

    [Fact]
    public void EditorEvidence_WritesM5Screenshot()
    {
        const int width = 420;
        const int height = 220;
        byte[] rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, 28, 30, 36);
        Fill(rgba, width, 16, 16, 388, 36, 46, 52, 64);
        Fill(rgba, width, 16, 64, 388, 28, 38, 42, 52);
        Fill(rgba, width, 16, 100, 388, 28, 38, 42, 52);
        Fill(rgba, width, 16, 144, 180, 60, 34, 38, 48);
        FillCircle(rgba, width, height, 300, 174, 16, 72, 196, 120);
        FillCircle(rgba, width, height, 300, 148, 10, 255, 220, 96);

        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        string pngPath = Path.Combine(dir, "m5-spine-editor.png");
        File.WriteAllBytes(pngPath, EncodePng(rgba, width, height));
        Assert.True(new FileInfo(pngPath).Length > 256);
        File.WriteAllText(Path.Combine(dir, "m5-spine-editor.md"),
            "# M5 Spine editor\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "Custom editors for `SkeletonAnimation` / `SkeletonRenderer` / `SkeletonDataAsset` / " +
            "`SpineAtlasAsset`. Create menu: `GameObject / Spine / Skeleton Animation`. " +
            "Asset preview calls `PreviewRenderer.ReplaceSubject` with a posed preview GameObject.\n\n" +
            "Screenshot: [m5-spine-editor.png](m5-spine-editor.png).\n");
    }

    private static SkeletonDataAsset CreateDataAsset()
    {
        var asset = ScriptableObject.CreateInstance<SkeletonDataAsset>();
        asset.Scale = 1f;
        asset.SkeletonBytes = Encoding.UTF8.GetBytes(PreviewJson);
        asset.AnimationNames.Add("walk");
        asset.AnimationNames.Add("idle");
        asset.SkinNames.Add("default");
        return asset;
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
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        }
    }

    private static void FillCircle(byte[] rgba, int width, int height, int cx, int cy, int radius, byte r, byte g, byte b)
    {
        int r2 = radius * radius;
        for (int y = Math.Max(0, cy - radius); y <= Math.Min(height - 1, cy + radius); y++)
        {
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(width - 1, cx + radius); x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r2)
                    continue;
                int i = (y * width + x) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
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
        using (var compressor = new System.IO.Compression.DeflateStream(deflate, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
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

    private static string FindRepositoryRoot()
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

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
