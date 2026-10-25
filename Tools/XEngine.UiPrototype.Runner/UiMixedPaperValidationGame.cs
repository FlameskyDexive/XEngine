using System.Text.Json;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Spatial;

namespace XEngine.UiPrototype;

internal sealed class UiMixedPaperValidationGame(string destination) : Game
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
        using var renderer = new PaperRenderer(); renderer.Initialize(320, 220);
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        using var image = new Texture2D(1, 1);
        image.SetData(new Memory<byte>([210, 75, 35, 255]), 0, 0, 1, 1);
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            int width = (int)(320 * dpi), height = (int)(220 * dpi);
            renderer.UpdateProjection(width, height);
            var budget = new UiMemoryBudget(8 * 1024 * 1024);
            var atlasBudget = new UiMemoryBudget(8 * 1024 * 1024);
            Canvas MakeCanvas() => new(new UiCanvasAtlasOwner(renderer, atlasBudget), new FontAtlasSettings { AtlasSize = 512 });
            var host = MakeCanvas(); var reference = MakeCanvas(); var children = new[] { MakeCanvas(), MakeCanvas() };
            var islands = new[] { new UiQuillPaintIsland(budget, 4096, 8192, 4, 16), new UiQuillPaintIsland(budget, 4096, 8192, 4, 16) };
            var layers = new ObservedLayer?[2];
            using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            renderer.SetRenderTarget(target.frameBuffer);
            try
            {
                // Give host and reference identical glyph packing to isolate composition.
                foreach (var canvas in new[] { host, reference })
                {
                    canvas.BeginFrame(320, 220, dpi);
                    canvas.DrawText("Toolbar Popup", 0, 0, new Color32(255, 255, 255, 255), 16, font);
                    if (!canvas.IsFontAtlasStableForFrame) throw new InvalidOperationException("Fixture atlas unexpectedly grew.");
                }
                Build(host, true, true, false, false);
                MustThrow<InvalidOperationException>(() => host.AppendRenderLayer(layers[0]!));
                // Fail during the second layer's Encode, after the first layer recorded uploads.
                layers[1]!.ThrowOnEncode = true;
                Clear(); MustThrow<InvalidOperationException>(() => host.Render()); Wait();
                AssertClear(Read());
                if (layers[0]!.Cancelled != 1 || layers[1]!.Completed != 0)
                    throw new InvalidOperationException("Host did not cancel exactly the successfully encoded layers.");
                layers[1]!.ThrowOnEncode = false;
                Compare("retry-after-failure", false, false);
                Compare("idle", false, true);
                Build(host, true, false, true, false); // recapture only layer B
                Compare("edited-layer-b", true, false);

                Build(host, true, false, false, true); // changed parent context, cached B unchanged
                Clear(); MustThrow<InvalidOperationException>(() => host.Render()); Wait(); AssertClear(Read());
                if (layers[0]!.Cancelled != 2) throw new InvalidOperationException("Stale context did not cancel earlier encoding.");
                Compare("restored-context", true, true);

                // A host containing only markers has no vertex/index/brush payload of its own.
                reference.BeginFrame(320, 220, dpi); host.BeginFrame(320, 220, dpi);
                for (int i = 0; i < 2; i++)
                {
                    var context = children[i].GetLayerContext();
                    reference.ApplyLayerContext(context); PaintLayer(reference, i, true);
                    host.ApplyLayerContext(context); host.AppendRenderLayer(layers[i]!);
                }
                using (var nested = new UiQuillPaintIsland(budget, 4096, 8192, 4, 16))
                    MustThrow<NotSupportedException>(() => nested.Capture(host));
                Clear(); reference.Render(); Wait(); byte[] markerExpected = Read();
                Clear(); host.Render(); Wait(); byte[] markerActual = Read();
                Save(markerExpected, $"dpi-{dpi}-marker-only-reference.png", width, height);
                Save(markerActual, $"dpi-{dpi}-marker-only-mixed.png", width, height);
                if (!markerExpected.AsSpan().SequenceEqual(markerActual) || renderer.LastUiStats.BufferUploadBytes != 0)
                    throw new InvalidOperationException("Marker-only host changed pixels or uploaded clean geometry.");
                results.Add(new { Dpi = dpi, Stage = "marker-only", PixelsEqual = true, Renderer = renderer.LastUiStats });

                void Compare(string stage, bool edited, bool expectClean)
                {
                    Build(reference, false, false, edited, false); Clear(); reference.Render(); Wait(); byte[] expected = Read();
                    Build(host, true, false, false, false); Clear(); host.Render(); Wait(); byte[] actual = Read();
                    Save(expected, $"dpi-{dpi}-{stage}-reference.png", width, height);
                    Save(actual, $"dpi-{dpi}-{stage}-mixed.png", width, height);
                    if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException($"Mixed Paper pixels differ: {dpi}/{stage}.");
                    for (int i = 0; i < 2; i++)
                    {
                        long bytes = layers[i]!.LastStats.BufferUploadBytes;
                        if (expectClean && bytes != 0) throw new InvalidOperationException("Clean layer uploaded geometry.");
                        if (stage == "retry-after-failure" && bytes == 0) throw new InvalidOperationException("Failed composition lost pending upload.");
                        if (stage == "edited-layer-b" && (i == 1 ? bytes == 0 : bytes != 0)) throw new InvalidOperationException("Layer edit did not isolate uploads.");
                    }
                    results.Add(new { Dpi = dpi, Stage = stage, PixelsEqual = true, Renderer = renderer.LastUiStats,
                        LayerA = layers[0]!.LastStats, LayerB = layers[1]!.LastStats,
                        CancelsA = layers[0]!.Cancelled, CancelsB = layers[1]!.Cancelled });
                }

                void Build(Canvas canvas, bool markers, bool captureAll, bool editedB, bool staleB)
                {
                    canvas.BeginFrame(320, 220, dpi);
                    canvas.RectFilled(0, 0, 320, 220, new Color32(18, 24, 36, 255));
                    canvas.DrawText("Toolbar", 10, 4, new Color32(245, 245, 245, 255), 16, font);
                    for (int layer = 0; layer < 2; layer++)
                    {
                        canvas.SaveState();
                        canvas.CurrentTransform(Transform2D.CreateTranslation(17 + layer * 30 + (layer == 1 && staleB ? 1 : 0), 25 + layer * 12));
                        canvas.Scissor(4, 3, 215 - layer * 20, 135);
                        var context = canvas.GetLayerContext();
                        if (!markers) PaintLayer(canvas, layer, editedB);
                        else
                        {
                            if (captureAll || layer == 1 && editedB)
                            {
                                children[layer].BeginFrame(320, 220, dpi); children[layer].ApplyLayerContext(context);
                                PaintLayer(children[layer], layer, editedB); islands[layer].Capture(children[layer]);
                                layers[layer] = new ObservedLayer(new UiQuillCanvasLayer(islands[layer], width, height, context));
                            }
                            canvas.AppendRenderLayer(layers[layer]!);
                        }
                        // Paper foreground after every retained layer, using its own brush and texture.
                        canvas.RectFilled(15, 28 + layer * 20, 205, 3, new Color32(245, 235, 210, 240));
                        canvas.DrawImage(image, 175, 85, 40, 30);
                        canvas.RestoreState();
                    }
                    canvas.RoundedRectFilled(100, 85, 185, 65, 9, new Color32(60, 75, 115, 225));
                    canvas.DrawText("Popup", 112, 100, new Color32(255, 255, 255, 255), 16, font);
                    canvas.RectFilled(0, 215, 320, 5, new Color32(180, 200, 230, 255));
                }
                void Clear()
                {
                    using var command = Graphics.GetCommandBuffer("N4 mixed clear"); command.SetRenderTarget(target.frameBuffer);
                    command.SetViewport(0, 0, (uint)width, (uint)height); command.DisableScissor();
                    command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f)); Graphics.Submit(command);
                }
                byte[] Read()
                {
                    var bytes = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(bytes.AsMemory());
                    if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
                        for (int y = 0; y < height / 2; y++) for (int x = 0; x < width * 4; x++)
                        { int a = y * width * 4 + x, b = (height - y - 1) * width * 4 + x; (bytes[a], bytes[b]) = (bytes[b], bytes[a]); }
                    return bytes;
                }
            }
            finally
            {
                host.Dispose(); reference.Dispose(); foreach (var child in children) child.Dispose();
                foreach (var island in islands) island.Dispose();
            }
            if (budget.ReservedBytes != 0 || atlasBudget.ReservedBytes != 0) throw new InvalidOperationException("Mixed owner cleanup leaked admission.");
        }
        File.WriteAllText(Path.Combine(destination, "validation.json"), JsonSerializer.Serialize(new {
            Backend = Graphics.Device!.Backend.ToString(), Results = results,
            Scope = "Actual PaperRenderer mixed Canvas stream with two retained markers, Paper geometry/images/text between and after them, exact translated shader clips, four DPI scales, failure cancellation and stale-context refusal. Not yet an editor DockPanel or Paper element-builder integration."
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
    private static void PaintLayer(Canvas canvas, int layer, bool edited)
    {
        canvas.SaveState();
        canvas.RectFilled(-12, -10, 265, 160, layer == 0 ? new Color32(25, 150, 95, 220) : edited ? new Color32(220, 165, 25, 180) : new Color32(160, 40, 190, 180));
        canvas.IntersectScissor(18, 12, 175, 85);
        canvas.RoundedRectFilled(5, 5, 195, 125, 15, new Color32(40, 90, 200, 100));
        // Leave an unchanged span between edits so B also emits a nonzero-offset
        // vertex update after earlier host/layer draws have opened a render pass.
        for (int i = 0; i < 48; i++)
            canvas.RectFilled(25 + i * 3, 65, 2, 4, new Color32(170, 210, 220, 210));
        canvas.RectFilled(28, 72, 90, 5, layer == 1 && edited
            ? new Color32(250, 180, 70, 230) : new Color32(80, 190, 210, 230));
        canvas.RestoreState();
    }
    private static void Wait() { using var command = Graphics.GetCommandBuffer("N4 mixed readback"); Graphics.SubmitAndWait(command); }
    private static void MustThrow<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void AssertClear(byte[] bytes)
    { for (int i = 0; i < bytes.Length; i += 4) if (bytes[i] != 0 || bytes[i + 1] != 0 || bytes[i + 2] != 0 || bytes[i + 3] != 255) throw new InvalidOperationException("Failed composition submitted partial pixels."); }
    private void Save(byte[] bytes, string name, int width, int height)
    {
        using var image = new ImageMagick.MagickImage(); image.ReadPixels(bytes,
            new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
        image.Write(Path.Combine(destination, name), ImageMagick.MagickFormat.Png32);
    }
    private sealed class ObservedLayer(IUiCommandLayer inner) : IUiCommandLayer
    {
        public bool ThrowOnEncode;
        public int Cancelled, Completed;
        public UiRenderStats LastStats;
        public void Validate(in UiCommandLayerContext context) => inner.Validate(context);
        public UiRenderStats Encode(CommandBuffer command, in UiCommandLayerContext context)
        {
            if (ThrowOnEncode) throw new InvalidOperationException("Injected later layer failure.");
            return LastStats = inner.Encode(command, context);
        }
        public void Complete(bool submitted) { if (!submitted) Cancelled++; Completed++; inner.Complete(submitted); }
    }
}
