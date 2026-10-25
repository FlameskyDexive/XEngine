using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.UiPrototype;

internal sealed class QuillPaintValidationGame(string destination, bool stressAtlas = false) : Game
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
        var results = new List<object>();
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        using var reference = new PaperRenderer(); reference.Initialize(640, 260);
        ValidateIslandGrowth(reference);
        foreach (bool generationLimit in new[] { false, true })
        {
            var rejectionBudget = new UiMemoryBudget(generationLimit ? 8 : 4);
            using var owner = new UiCanvasAtlasOwner(reference, rejectionBudget, generationLimit ? 1 : 16);
            var firstTexture = (Texture2D)owner.CreateTexture(1, 1);
            bool rejected = false;
            try { owner.CreateTexture(1, 1); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || owner.LiveTextures != 1 || rejectionBudget.ReservedBytes != 4 || !firstTexture.IsValid())
                throw new InvalidOperationException("Atlas admission rejection changed live ownership.");
            owner.Dispose(); owner.Dispose();
            if (firstTexture.IsValid() || rejectionBudget.ReservedBytes != 0)
                throw new InvalidOperationException("Atlas owner did not release texture and charge exactly once.");
        }
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            int width = (int)(640 * dpi), height = (int)(260 * dpi);
            reference.UpdateProjection(width, height);
            var atlasBudget = new UiMemoryBudget(32 * 1024 * 1024);
            using var atlasOwner = new UiCanvasAtlasOwner(reference, atlasBudget);
            var canvas = new Canvas(atlasOwner, new FontAtlasSettings { AtlasSize = stressAtlas ? 256 : 2048 });
            var staticCanvas = new Canvas(new AtlasOwner(reference), new FontAtlasSettings());
            var budget = new UiMemoryBudget(UiQuillPaintCache.EstimateCapacityCharge(16384, 32768, 4, 64)
                + UiQuillPaintCache.EstimateCapacityCharge(128, 256, 1, 8));
            try
            {
            using var retained = new UiQuillPaintIsland(budget, 16384, 32768, 4, 64);
            using var staticPaint = new UiQuillPaintIsland(budget, 128, 256, 1, 8);
            if (budget.ReservedBytes != 0) throw new InvalidOperationException("Empty islands eagerly allocated caches.");
            using var nodes = new UiNodeStore(4, 4);
            var paintNode = nodes.Create(); nodes.SetRootBounds(paintNode, new(0, 0, 640, 260)); nodes.Layout();
            var staticNode = nodes.Create(); nodes.SetRootBounds(staticNode, new(0, 0, 640, 260)); nodes.Layout();
            var titleNode = nodes.Create(paintNode); var decorationNode = nodes.Create(paintNode);
            nodes.SetStyle(titleNode, UiStyle.Default with { Width = UiLength.Fixed(600), Height = UiLength.Fixed(60) });
            nodes.SetStyle(decorationNode, UiStyle.Default with { Width = UiLength.Fixed(600), Height = UiLength.Fixed(60) });
            nodes.Layout();
            using var scheduler = new UiPaintRegionScheduler(nodes, 2, 4); scheduler.Register(paintNode); scheduler.Register(staticNode);
            int staticBuilds = 0; bool staticSubmitted = false;
            string title = "Retained text / partial uploads";
            var painter = new PaintCallback(work =>
            {
                if (work.Root == staticNode)
                {
                    staticCanvas.BeginFrame(640, 260, dpi);
                    staticCanvas.RectFilled(15, 240, 610, 8, new Color32(35, 170, 120, 255));
                    staticPaint.Capture(staticCanvas); staticBuilds++; return;
                }
                if (work.Root != paintNode) throw new InvalidOperationException("Unexpected paint owner.");
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    Build(title);
                    if (!canvas.IsFontAtlasStableForFrame) continue;
                    retained.Capture(canvas); atlasOwner.RetireObsolete(canvas); return;
                }
                throw new InvalidOperationException("Font atlas did not stabilize within eight paint attempts.");
            });
            using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            reference.SetRenderTarget(target.frameBuffer);
            if (scheduler.Paint(ref painter).PaintedRegions != 2) throw new InvalidOperationException("Initial node paints missing.");
            if (budget.ReservedBytes != retained.CapacityCharge + staticPaint.CapacityCharge || budget.AvailableBytes <= 0)
                throw new InvalidOperationException("Content-sized island capacity accounting mismatch.");
            if (canvas.BrushRecordCount <= Canvas.BrushRecordsPerPage)
                throw new InvalidOperationException("Fixture did not cross a brush page boundary.");
            byte[] first = Compare("initial");
            nodes.Invalidate(titleNode, UiInvalidation.Paint); nodes.Invalidate(decorationNode, UiInvalidation.Paint);
            var coalesced = scheduler.Paint(ref painter);
            if (coalesced.PaintedRegions != 1 || coalesced.AcknowledgedNodes != 2 || staticBuilds != 1)
                throw new InvalidOperationException("Sibling dirty controls did not coalesce into one region capture.");
            results.Add(new { Dpi = dpi, Coalesced = coalesced });
            if (scheduler.Paint(ref painter) != default) throw new InvalidOperationException("Idle node rebuilt paint.");
            Clear();
            var idle = retained.Render(target.frameBuffer, width, height);
            RenderStatic();
            if (idle.VertexBytes != 0 || idle.IndexBytes != 0 || idle.BrushBytes != 0)
                throw new InvalidOperationException("Idle paint uploaded data.");
            if (!Read("idle").AsSpan().SequenceEqual(first)) throw new InvalidOperationException("Idle changed text pixels.");
            title = "Edited text / cached brushes"; nodes.Invalidate(paintNode, UiInvalidation.Paint);
            if (scheduler.Paint(ref painter).PaintedRegions != 1) throw new InvalidOperationException("Edited node paint missing.");
            byte[] edited = Compare("edited");
            nodes.SetStyle(paintNode, UiStyle.Default with { Visible = false }); nodes.Layout();
            title = "Hidden edits appear on restore"; nodes.Invalidate(paintNode, UiInvalidation.Paint);
            var hiddenPaint = scheduler.Paint(ref painter);
            if (hiddenPaint.PaintedRegions != 0) throw new InvalidOperationException("Hidden node rebuilt paint.");
            var hidden = retained.Render(target.frameBuffer, width, height, visible: false);
            if (hidden != default) throw new InvalidOperationException("Hidden paint submitted work.");
            if (!Read("hidden-unchanged").AsSpan().SequenceEqual(edited))
                throw new InvalidOperationException("Hidden update changed target pixels.");
            nodes.SetStyle(paintNode, UiStyle.Default); nodes.Layout();
            var restoredPaint = scheduler.Paint(ref painter);
            if (restoredPaint.PaintedRegions != 1) throw new InvalidOperationException("Restored dirty node did not repaint.");
            Clear(); canvas.Render(); staticCanvas.Render(); byte[] restoredReference = Read("restored-reference"); Clear();
            var restored = retained.Render(target.frameBuffer, width, height);
            RenderStatic();
            if (restored.VertexBytes == 0 || !Read("restored-retained").AsSpan().SequenceEqual(restoredReference))
                throw new InvalidOperationException("Hidden dirty geometry was lost.");
            if (stressAtlas)
            {
                object oldAtlas = canvas.FontAtlasTexture;
                var oldAtlasSize = reference.GetTextureSize(oldAtlas);
                int oldRevision = canvas.FontAtlasRevision;
                var chars = new char[94]; for (int i = 0; i < chars.Length; i++) chars[i] = (char)(33 + i);
                var stressSettings = TextLayoutSettings.Default;
                stressSettings.Font = font; stressSettings.PixelSize = 23; stressSettings.Quality = FontQuality.Ultra;
                canvas.DrawText(new string(chars), 30, 85, new Color32(255, 255, 255, 255), stressSettings);
                var expandedAtlasSize = reference.GetTextureSize(canvas.FontAtlasTexture);
                if (ReferenceEquals(oldAtlas, canvas.FontAtlasTexture) || canvas.FontAtlasRevision <= oldRevision || !retained.NeedsRecapture)
                    throw new InvalidOperationException("Atlas growth did not invalidate retained geometry.");
                bool staleRejected = false;
                try { retained.Render(target.frameBuffer, width, height); }
                catch (InvalidOperationException) { staleRejected = true; }
                if (!staleRejected) throw new InvalidOperationException("Stale atlas geometry was submitted.");
                bool captureRejected = false;
                try { retained.Capture(canvas); }
                catch (InvalidOperationException) { captureRejected = true; }
                if (!captureRejected) throw new InvalidOperationException("Old canvas geometry was recaptured after atlas growth.");
                nodes.Invalidate(paintNode, UiInvalidation.Paint);
                if (scheduler.Paint(ref painter).PaintedRegions != 1 || retained.NeedsRecapture || staticPaint.NeedsRecapture)
                    throw new InvalidOperationException("Atlas repaint did not refresh only its owning node.");
                if (((Texture2D)oldAtlas).IsValid() || atlasOwner.LiveTextures != 1 || atlasOwner.RetiredTextures == 0)
                    throw new InvalidOperationException("Old atlas ownership survived stable repaint.");
                if (atlasOwner.AdmittedPayloadBytes <= atlasOwner.LivePayloadBytes || atlasBudget.ReservedBytes != atlasOwner.AdmittedPayloadBytes)
                    throw new InvalidOperationException("Retired atlas generation lost its conservative admission charge.");
                results.Add(new { Dpi = dpi, atlasOwner.LiveTextures, atlasOwner.RetiredTextures,
                    atlasOwner.LivePayloadBytes, atlasOwner.AdmittedPayloadBytes });
                Compare("atlas-rebuilt");
                canvas.AddFallbackFont(new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf")));
                if (!retained.NeedsRecapture) throw new InvalidOperationException("Fallback revision was not observed.");
                nodes.Invalidate(paintNode, UiInvalidation.Paint); scheduler.Paint(ref painter);
                Compare("fallback-rebuilt");
                results.Add(new { Dpi = dpi, OldAtlasRevision = oldRevision, NewAtlasRevision = canvas.FontAtlasRevision,
                    OldAtlasWidth = oldAtlasSize.X, OldAtlasHeight = oldAtlasSize.Y,
                    ExpandedAtlasWidth = expandedAtlasSize.X, ExpandedAtlasHeight = expandedAtlasSize.Y,
                    StaleRejected = staleRejected, OldCaptureRejected = captureRejected });
            }
            retained.Clear();
            if (retained.Render(target.frameBuffer, width, height) != default)
                throw new InvalidOperationException("Empty paint submitted work.");
            if (staticBuilds != 1) throw new InvalidOperationException("Unchanged sibling paint rebuilt.");
            results.Add(new { Dpi = dpi, BudgetCharge = budget.ReservedBytes, PaintCacheCreations = retained.CacheCreations,
                StaticCacheCreations = staticPaint.CacheCreations, BrushRecords = canvas.BrushRecordCount, StaticBuilds = staticBuilds, Idle = idle, Hidden = hidden,
                Restored = restored, HiddenPaint = hiddenPaint, RestoredPaint = restoredPaint, PixelsEqual = true });

            void Build(string title)
            {
                canvas.BeginFrame(640, 260, dpi);
                canvas.RectFilled(0, 0, 640, 260, new Color32(24, 28, 36, 255));
                canvas.RoundedRectFilled(14, 12, 612, 150, 12, new Color32(50, 76, 110, 190));
                canvas.Scissor(25, 25, 565, 110);
                canvas.DrawText(title, 30, 28, new Color32(240, 242, 248, 255), 23, font);
                canvas.DrawText("0123456789  AaBb  fi ffi  punctuation: []()!?", 30, 65, new Color32(140, 210, 240, 255), 17, font);
                canvas.RoundedRectFilled(410, 40, 210, 100, 18, new Color32(230, 80, 40, 95));
                canvas.DrawText("Clipped + transparent", 380, 115, new Color32(255, 255, 255, 220), 18, font);
                canvas.ResetScissor();
                for (int i = 0; i < 110; i++)
                {
                    float x = 15 + (i % 55) * 11, y = 178 + (i / 55) * 28;
                    canvas.SetLinearBrush(x, y, x + 9, y + 22, new Color32((byte)(i + 80), 110, 220, 255), new Color32(230, (byte)i, 90, 190));
                    canvas.RectFilled(x, y, 9, 22, new Color32(255, 255, 255, 255));
                }
                canvas.ClearBrush();
            }
            byte[] Compare(string stage)
            {
                Clear(); canvas.Render(); staticCanvas.Render(); byte[] expected = Read(stage + "-reference");
                Clear();
                var stats = retained.Render(target.frameBuffer, width, height);
                RenderStatic();
                byte[] actual = Read(stage + "-retained");
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    // Diagnostics run only after preserving the original failed pair. Never
                    // accept a retry in place of the first result or change normal-path timing.
                    var probes = new List<object>();
                    string atlasBefore = ReadAtlas("before");
                    for (int probe = 0; probe < 3; probe++)
                    {
                        Clear(); canvas.Render(); staticCanvas.Render();
                        var referenceAgain = Read($"{stage}-probe-{probe}-reference");
                        Clear(); retained.Render(target.frameBuffer, width, height); RenderStatic();
                        var retainedAgain = Read($"{stage}-probe-{probe}-retained");
                        probes.Add(new { Probe = probe, ReferenceStable = referenceAgain.AsSpan().SequenceEqual(expected),
                            RetainedStable = retainedAgain.AsSpan().SequenceEqual(actual),
                            PairEqual = retainedAgain.AsSpan().SequenceEqual(referenceAgain) });
                    }
                    retained.Capture(canvas);
                    Clear(); retained.Render(target.frameBuffer, width, height); RenderStatic();
                    var recaptured = Read(stage + "-recaptured");
                    string atlasAfter = ReadAtlas("after");
                    File.WriteAllText(Path.Combine(destination, $"dpi-{dpi}-{stage}-mismatch.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { Probes = probes, AtlasBefore = atlasBefore,
                            AtlasAfter = atlasAfter, AtlasPixelsStable = atlasBefore == atlasAfter,
                            RecapturedEqualsOriginalReference = recaptured.AsSpan().SequenceEqual(expected),
                            canvas.FontAtlasRevision, canvas.IsFontAtlasStableForFrame, retained.CacheCreations,
                            retained.CapacityCharge }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    throw new InvalidOperationException($"{dpi}/{stage} pixel mismatch; repeated-draw diagnostics saved.");

                    string ReadAtlas(string suffix)
                    {
                        var atlas = (Texture2D)canvas.FontAtlasTexture;
                        var bytes = new byte[atlas.GetSize()]; atlas.GetData<byte>(bytes.AsMemory());
                        File.WriteAllBytes(Path.Combine(destination, $"dpi-{dpi}-{stage}-atlas-{suffix}.rgba"), bytes);
                        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    }
                }
                results.Add(new { Dpi = dpi, Stage = stage, Stats = stats });
                return actual;
            }
            void RenderStatic()
            {
                var stats = staticPaint.Render(target.frameBuffer, width, height);
                if (staticSubmitted && (stats.VertexBytes != 0 || stats.IndexBytes != 0 || stats.BrushBytes != 0))
                    throw new InvalidOperationException("Unchanged sibling paint uploaded data.");
                staticSubmitted = true;
            }
            void Clear()
            {
                using var command = Graphics.GetCommandBuffer("N3 Quill reference clear");
                command.SetRenderTarget(target.frameBuffer); command.SetViewport(0, 0, (uint)width, (uint)height); command.DisableScissor();
                command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f)); Graphics.Submit(command);
            }
            byte[] Read(string stage)
            {
                using (var flush = Graphics.GetCommandBuffer("N3 Quill readback")) Graphics.SubmitAndWait(flush);
                var pixels = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(pixels.AsMemory());
                // GL readback rows begin at the framebuffer bottom; archive top-origin images.
                if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
                    for (int y = 0; y < height / 2; y++)
                        for (int x = 0; x < width * 4; x++)
                        {
                            int top = y * width * 4 + x, bottom = (height - 1 - y) * width * 4 + x;
                            (pixels[top], pixels[bottom]) = (pixels[bottom], pixels[top]);
                        }
                int titlePixels = 0;
                for (int y = (int)(28 * dpi); y < (int)(55 * dpi); y++)
                    for (int x = (int)(30 * dpi); x < (int)(540 * dpi); x++)
                    {
                        int offset = (y * width + x) * 4;
                        if (pixels[offset] > 150 && pixels[offset + 1] > 150 && pixels[offset + 2] > 150) titlePixels++;
                    }
                if (titlePixels < 100) throw new InvalidOperationException("Independent title-region check found missing text.");
                using var image = new ImageMagick.MagickImage();
                image.ReadPixels(pixels, new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
                image.Write(Path.Combine(destination, $"dpi-{dpi}-{stage}.png"), ImageMagick.MagickFormat.Png32);
                return pixels;
            }
            }
            finally
            {
                staticCanvas.Dispose(); canvas.Dispose();
                if (atlasBudget.ReservedBytes != 0) throw new InvalidOperationException("Disposed atlas owner retained budget charges.");
                if (budget.ReservedBytes != 0) throw new InvalidOperationException("Disposed paint caches retained budget leases.");
            }
        }
        File.WriteAllText(Path.Combine(destination, "validation.json"), System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("N3 Quill: four DPI text/brush/reference/idle/hidden checks passed.");
    }

    private static void ValidateIslandGrowth(PaperRenderer renderer)
    {
        var budget = new UiMemoryBudget(2 * 1024 * 1024);
        using var atlasOwner = new AtlasOwner(renderer);
        var canvas = new Canvas(atlasOwner, new FontAtlasSettings { AtlasSize = 128 });
        using var island = new UiQuillPaintIsland(budget, 4096, 8192, 8, 64);
        canvas.BeginFrame(640, 260); island.Capture(canvas);
        if (island.CacheCreations != 0 || island.Render(null, 640, 260) != default)
            throw new InvalidOperationException("Empty island created or submitted resources.");
        Build(1); island.Capture(canvas);
        long smallCharge = island.CapacityCharge;
        using (var reservation = budget.Reserve(budget.AvailableBytes))
        {
            Build(100);
            bool refused = false;
            try { island.Capture(canvas); } catch (InvalidOperationException) { refused = true; }
            if (!refused || island.CacheCreations != 1 || island.CapacityCharge != smallCharge)
                throw new InvalidOperationException("Budget-rejected growth changed the live cache.");
        }
        island.Capture(canvas);
        if (island.CacheCreations != 2 || island.CapacityCharge <= smallCharge || budget.ReservedBytes != island.CapacityCharge)
            throw new InvalidOperationException("Successful growth did not replace and release the old capacity.");
        long grownCharge = island.CapacityCharge;
        Build(2000);
        bool tooLarge = false;
        try { island.Capture(canvas); } catch (InvalidOperationException) { tooLarge = true; }
        if (!tooLarge || island.CapacityCharge != grownCharge || island.CacheCreations != 2)
            throw new InvalidOperationException("Over-limit paint replaced the live cache.");
        island.Evict();
        if (budget.ReservedBytes != 0 || island.Render(null, 640, 260) != default)
            throw new InvalidOperationException("Eviction retained capacity or submitted stale paint.");
        Build(1); island.Capture(canvas);
        if (island.CapacityCharge != smallCharge || island.CacheCreations != 3)
            throw new InvalidOperationException("Reopened island retained oversized capacity.");
        island.Dispose();
        if (budget.ReservedBytes != 0) throw new InvalidOperationException("Island disposal leaked a lease.");

        void Build(int count)
        {
            canvas.BeginFrame(640, 260);
            for (int i = 0; i < count; i++)
                canvas.RectFilled(i % 64 * 10, i / 64 * 10, 8, 8, new Color32((byte)i, 160, 220, 255));
        }
    }

    private sealed class AtlasOwner(PaperRenderer renderer) : ICanvasRenderer
    {
        private readonly List<IDisposable> _textures = new();
        public object CreateTexture(uint width, uint height)
        { var texture = renderer.CreateTexture(width, height); _textures.Add((IDisposable)texture); return texture; }
        public Int2 GetTextureSize(object texture) => renderer.GetTextureSize(texture);
        public void SetTextureData(object texture, IntRect bounds, byte[] data) => renderer.SetTextureData(texture, bounds, data);
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) => renderer.RenderCalls(canvas, calls);
        public void Dispose() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); }
    }

    private readonly struct PaintCallback(Action<UiPaintRegionWork> callback) : IUiRegionPainter
    {
        public bool TryPaintRegion(in UiPaintRegionWork work) { callback(work); return true; }
    }
}
