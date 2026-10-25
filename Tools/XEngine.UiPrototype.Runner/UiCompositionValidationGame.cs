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

namespace XEngine.UiPrototype;

// N4 command-encoding foundation. Paper is the full-frame reference here;
// actual mixed Paper draw-list markers and production panels are not integrated yet.
internal sealed class UiCompositionValidationGame(string destination) : Game
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
        foreach (float dpi in new[] { 1f, 1.5f, 2f })
        {
            int width = (int)(320 * dpi), height = (int)(220 * dpi);
            renderer.UpdateProjection(width, height);
            var atlasBudget = new UiMemoryBudget(4 * 1024 * 1024);
            var budget = new UiMemoryBudget(8 * 1024 * 1024);
            var canvases = new Canvas[3]; var islands = new UiQuillPaintIsland[3];
            var reference = new Canvas(new UiCanvasAtlasOwner(renderer, atlasBudget), new FontAtlasSettings { AtlasSize = 128 });
            using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    canvases[i] = new Canvas(new UiCanvasAtlasOwner(renderer, atlasBudget), new FontAtlasSettings { AtlasSize = 128 });
                    canvases[i].BeginFrame(320, 220, dpi); BuildPass(canvases[i], i, false);
                    islands[i] = new(budget, 4096, 8192, 4, 16); islands[i].Capture(canvases[i]);
                }

                var cancelled = new UiPaintEncoding[2];
                using (var command = Graphics.GetCommandBuffer("N4 deliberately discarded composition"))
                {
                    try
                    {
                        cancelled[0] = islands[0].Encode(command, width, height);
                        cancelled[1] = islands[1].Encode(command, width, height);
                        MustThrow<InvalidOperationException>(() => islands[0].Encode(command, width, height));
                        MustThrow<ArgumentException>(() => islands[1].CompleteEncoding(cancelled[0], false));
                        MustThrow<InvalidOperationException>(() => islands[0].Evict());
                        // Exceed the existing small capacity to exercise island replacement protection.
                        canvases[0].BeginFrame(320, 220, dpi);
                        for (int i = 0; i < 200; i++) canvases[0].RectFilled(i % 20 * 10, i / 20 * 10, 8, 8, new Color32(150, 100, 90, 255));
                        MustThrow<InvalidOperationException>(() => islands[0].Capture(canvases[0]));
                        MustThrow<InvalidOperationException>(() => islands[0].Dispose());
                    }
                    finally
                    {
                        for (int i = 0; i < 2; i++) if (cancelled[i].IsValid) islands[i].CompleteEncoding(cancelled[i], false);
                    }
                }
                MustThrow<ArgumentException>(() => islands[0].CompleteEncoding(cancelled[0], false));
                canvases[0].BeginFrame(320, 220, dpi); BuildPass(canvases[0], 0, false);

                Compare("initial-after-cancel", false, false, false);
                Compare("idle", false, false, true);
                canvases[1].BeginFrame(320, 220, dpi); BuildPass(canvases[1], 1, true); islands[1].Capture(canvases[1]);
                Compare("edited-middle-layer", true, false, false);
                Compare("host-scissor", true, true, true);

                void Compare(string stage, bool edited, bool scissor, bool expectZero)
                {
                    reference.BeginFrame(320, 220, dpi);
                    for (int i = 0; i < 3; i++) BuildPass(reference, i, edited);
                    Clear(target, width, height); renderer.SetRenderTarget(target.frameBuffer); reference.Render(); Wait();
                    byte[] expected = Read(target, width, height);
                    // Hardware scissor is a hard integer pixel mask. Canvas.Scissor uses
                    // shader feathering and is not an equivalent reference at fractional DPI.
                    // Apply the host's exact half-width mask to the unclipped Paper pixels.
                    if (scissor)
                        for (int y = 0; y < height; y++) for (int x = width / 2; x < width; x++)
                        { int p = (y * width + x) * 4; expected[p] = expected[p + 1] = expected[p + 2] = 0; expected[p + 3] = 255; }
                    Clear(target, width, height);
                    var tickets = new UiPaintEncoding[3];
                    bool submitted = false;
                    using (var command = Graphics.GetCommandBuffer("N4 ordered three-layer host"))
                    {
                        command.SetRenderTarget(target.frameBuffer); command.SetViewport(0, 0, (uint)width, (uint)height);
                        if (scissor) command.SetScissor(0, 0, (uint)width / 2, (uint)height); else command.DisableScissor();
                        try
                        {
                            for (int i = 0; i < 3; i++) tickets[i] = islands[i].Encode(command, width, height);
                            if (islands[0].Encode(command, width, height, visible: false).IsValid)
                                throw new InvalidOperationException("Hidden layer opened an encoding.");
                            Graphics.Submit(command); submitted = true;
                        }
                        finally
                        { for (int i = 0; i < 3; i++) if (tickets[i].IsValid) islands[i].CompleteEncoding(tickets[i], submitted); }
                    }
                    MustThrow<ArgumentException>(() => islands[0].CompleteEncoding(tickets[0], true));
                    for (int i = 0; i < 3; i++)
                    {
                        long bytes = Uploaded(tickets[i]);
                        if (expectZero && bytes != 0) throw new InvalidOperationException("Unchanged layer uploaded data.");
                        if (stage == "initial-after-cancel" && (bytes == 0 || i < 2 && tickets[i].Stats != cancelled[i].Stats))
                            throw new InvalidOperationException("Cancellation lost pending data.");
                        if (stage == "edited-middle-layer" && (i == 1 ? bytes == 0 : bytes != 0))
                            throw new InvalidOperationException("Middle edit did not isolate uploads.");
                    }
                    Wait(); byte[] actual = Read(target, width, height);
                    Save(expected, $"dpi-{dpi}-{stage}-reference.png", width, height);
                    Save(actual, $"dpi-{dpi}-{stage}-retained.png", width, height);
                    if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException($"Ordered composition pixels differ: {dpi}/{stage}.");
                    results.Add(new { Dpi = dpi, Stage = stage, PixelsEqual = true, HostSubmissions = 1,
                        Layer0 = tickets[0].Stats, Layer1 = tickets[1].Stats, Layer2 = tickets[2].Stats });
                }
            }
            finally
            {
                foreach (var island in islands) island?.Dispose();
                foreach (var canvas in canvases) canvas?.Dispose(); reference.Dispose();
            }
            if (budget.ReservedBytes != 0 || atlasBudget.ReservedBytes != 0)
                throw new InvalidOperationException("Composition owners leaked admission.");
        }
        File.WriteAllText(Path.Combine(destination, "validation.json"), JsonSerializer.Serialize(new {
            Backend = Graphics.Device!.Backend.ToString(), Results = results,
            Scope = "Three retained layers in one host command; Paper full-frame reference, with an explicit integer half-width pixel mask only for the hardware-scissor case. Tests order/transparency, captured child clips, inherited horizontal hardware scissor, cancellation/retry, stale/foreign tickets, mutation/disposal guards and isolated dirty uploads. Not production Paper marker integration, arbitrary rounded parent clipping, docking or panel migration."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static long Uploaded(UiPaintEncoding encoding) => encoding.Stats.VertexBytes + encoding.Stats.IndexBytes + encoding.Stats.BrushBytes;
    private static void MustThrow<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void BuildPass(Canvas canvas, int index, bool edited)
    {
        canvas.SaveState();
        if (index == 0) canvas.RectFilled(10, 10, 240, 175, new Color32(35, 150, 120, 255));
        else if (index == 1)
        {
            canvas.IntersectScissor(70, 45, 180, 110);
            canvas.RectFilled(40, 20, 260, 170, edited ? new Color32(245, 190, 30, 175) : new Color32(210, 35, 70, 175));
        }
        else
        {
            canvas.RectFilled(40, 115, 240, 90, new Color32(40, 80, 220, 140));
            canvas.RectFilled(80, 130, 180, 4, new Color32(245, 245, 245, 255));
        }
        canvas.RestoreState();
    }
    private static void Clear(RenderTexture target, int width, int height)
    {
        using var command = Graphics.GetCommandBuffer("N4 composition clear"); command.SetRenderTarget(target.frameBuffer);
        command.SetViewport(0, 0, (uint)width, (uint)height); command.DisableScissor();
        command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f)); Graphics.Submit(command);
    }
    private static void Wait() { using var command = Graphics.GetCommandBuffer("N4 composition readback"); Graphics.SubmitAndWait(command); }
    private static byte[] Read(RenderTexture target, int width, int height)
    {
        var bytes = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(bytes.AsMemory());
        if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
            for (int y = 0; y < height / 2; y++) for (int x = 0; x < width * 4; x++)
            { int a = y * width * 4 + x, b = (height - y - 1) * width * 4 + x; (bytes[a], bytes[b]) = (bytes[b], bytes[a]); }
        return bytes;
    }
    private void Save(byte[] bytes, string name, int width, int height)
    {
        using var image = new ImageMagick.MagickImage();
        image.ReadPixels(bytes, new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
        image.Write(Path.Combine(destination, name), ImageMagick.MagickFormat.Png32);
    }
}
