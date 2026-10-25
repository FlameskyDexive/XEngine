// Prepared while the static timing binary is frozen. Not part of that binary.
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.UiPrototype;

internal sealed class CjkPaintValidationGame(string destination, string fallbackPath) : Game
{
    public override void OnGui(Scene? scene, Paper paper)
    {
        try { Validate(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 2; }
        Quit();
    }

    private void Validate()
    {
        Directory.CreateDirectory(destination);
        var latin = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        var chinese = new FontFile(fallbackPath);
        const string first = "编辑器性能测试", second = "中文输入缓存恢复";
        foreach (char c in first + second)
            if (latin.FindGlyphIndex(c) != 0 || chinese.FindGlyphIndex(c) == 0)
                throw new InvalidOperationException("Font provenance check failed: fixture needs genuine CJK fallback.");
        using var reference = new PaperRenderer(); reference.Initialize(640, 180);
        var results = new List<object>();
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            int width = (int)(640 * dpi), height = (int)(180 * dpi);
            reference.UpdateProjection(width, height);
            var budget = new UiMemoryBudget(16 * 1024 * 1024);
            using var owner = new UiCanvasAtlasOwner(reference, budget);
            var canvas = new Canvas(owner, new FontAtlasSettings { AtlasSize = 512 });
            using var island = new UiQuillPaintIsland(budget, 16384, 32768, 4, 64);
            using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            reference.SetRenderTarget(target.frameBuffer);
            Build(first); island.Capture(canvas);
            canvas.AddFallbackFont(chinese);
            if (!island.NeedsRecapture) throw new InvalidOperationException("Adding actual CJK fallback did not invalidate paint.");
            bool refused = false;
            try { island.Render(target.frameBuffer, width, height); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Stale fallback paint was submitted.");
            foreach (char c in first + second)
            {
                var glyph = canvas.Text.FontEngine.GetOrCreateGlyph(c, latin, FontQuality.Normal);
                if (glyph == null || !ReferenceEquals(glyph.Font, chinese) || glyph.GlyphIndex == 0)
                    throw new InvalidOperationException("Chinese glyph resolved to the wrong font or .notdef.");
            }
            Repaint(first); Compare("fallback");
            Repaint(second); Compare("edited");
            island.Evict(); Repaint(first); Compare("reopened");
            island.Dispose(); owner.Dispose();
            if (budget.ReservedBytes != 0) throw new InvalidOperationException("CJK fixture retained admission leases.");
            results.Add(new { Dpi = dpi, GenuineFallbackGlyphs = true, ReferencePixelsEqual = true, ClosedBudgetBytes = budget.ReservedBytes });

            void Build(string text)
            {
                canvas.BeginFrame(640, 180, dpi);
                canvas.RectFilled(0, 0, 640, 180, new Color32(24, 28, 36, 255));
                canvas.DrawText("CJK fallback / retained paint", 20, 14, new Color32(235, 240, 245, 255), 20, latin);
                canvas.DrawText(text, 20, 60, new Color32(220, 235, 250, 255), 32, latin);
                canvas.DrawText("Frame 120 / 中文 / 125% DPI", 20, 118, new Color32(130, 210, 235, 255), 20, latin);
            }
            void Repaint(string text)
            {
                for (int attempt = 0; attempt < 8; attempt++)
                { Build(text); if (!canvas.IsFontAtlasStableForFrame) continue; island.Capture(canvas); owner.RetireObsolete(canvas); return; }
                throw new InvalidOperationException("CJK atlas did not stabilize.");
            }
            void Clear()
            {
                using var command = Graphics.GetCommandBuffer("CJK validation clear");
                command.SetRenderTarget(target.frameBuffer); command.SetViewport(0, 0, (uint)width, (uint)height); command.DisableScissor();
                command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f)); Graphics.Submit(command);
            }
            void Compare(string stage)
            {
                Clear(); canvas.Render(); var expected = Read(stage + "-reference");
                Clear(); island.Render(target.frameBuffer, width, height); var actual = Read(stage + "-retained");
                if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException($"CJK {dpi}/{stage} pixels differ.");
            }
            byte[] Read(string stage)
            {
                using (var flush = Graphics.GetCommandBuffer("CJK readback")) Graphics.SubmitAndWait(flush);
                var data = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(data.AsMemory());
                if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
                    for (int y = 0; y < height / 2; y++)
                        for (int x = 0; x < width * 4; x++)
                        { int a = y * width * 4 + x, b = (height - 1 - y) * width * 4 + x; (data[a], data[b]) = (data[b], data[a]); }
                int bright = 0;
                for (int y = (int)(60 * dpi); y < (int)(104 * dpi); y++)
                    for (int x = (int)(20 * dpi); x < (int)(450 * dpi); x++) if (data[(y * width + x) * 4] > 150) bright++;
                if (bright < 100) throw new InvalidOperationException("Chinese text region is empty.");
                using var image = new ImageMagick.MagickImage();
                image.ReadPixels(data, new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
                image.Write(Path.Combine(destination, $"dpi-{dpi}-{stage}.png"), ImageMagick.MagickFormat.Png32);
                return data;
            }
        }
        File.WriteAllText(Path.Combine(destination, "validation.json"), System.Text.Json.JsonSerializer.Serialize(new
        { FontPath = fallbackPath, FontSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fallbackPath))), Results = results,
            Scope = "Actual CJK fallback drawing, edits and eviction/repaint. No OS IME, text editing widget, multilingual shaping or accessibility acceptance." },
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
