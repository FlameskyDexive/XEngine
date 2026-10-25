using System.Text.Json;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using Color = System.Drawing.Color;

namespace XEngine.UiPrototype;

internal sealed class UiPaperRegionValidationGame(string destination) : Game
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
        using var renderer = new PaperRenderer(); renderer.Initialize(360, 240);
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        foreach (float dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            int width = (int)(360 * dpi), height = (int)(240 * dpi);
            renderer.UpdateProjection(width, height);
            using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
            renderer.SetRenderTarget(target.frameBuffer);
            var geometry = new UiMemoryBudget(8 * 1024 * 1024);
            var atlases = new UiMemoryBudget(16 * 1024 * 1024);
            var host = new Paper(new UiCanvasAtlasOwner(renderer, atlases), 360, 240, new FontAtlasSettings { AtlasSize = 512 });
            var reference = new Paper(new UiCanvasAtlasOwner(renderer, atlases), 360, 240, new FontAtlasSettings { AtlasSize = 512 });
            bool edited = false, throwPaint = false, invalidateDuringPaint = false, foreground = false;
            float margin = 12;
            UiPaperPaintRegion? region = null;
            region = new UiPaperPaintRegion(host, geometry, atlases, (canvas, rect) =>
            {
                if (throwPaint) throw new InvalidOperationException("Injected paint failure.");
                Paint(canvas, rect);
                if (invalidateDuringPaint) { invalidateDuringPaint = false; region!.Invalidate(); }
            });
            try
            {
                Compare("initial", 1, false);
                Compare("idle", 1, true);
                edited = true; region.Invalidate(); Compare("edited", 2, false);
                margin = 19.5f; Compare("moved", 3, false);
                throwPaint = true; region.Invalidate();
                Build(host, true, true); Clear();
                MustThrow<InvalidOperationException>(() => host.EndFrame());
                if (region.CaptureCount != 3) throw new InvalidOperationException("Failed paint acknowledged dirty state.");
                throwPaint = false; Compare("retry-paint", 4, true);
                region.Invalidate(); invalidateDuringPaint = true;
                Compare("invalidate-during-paint", 5, true);
                Compare("deferred-invalidation", 6, true);
                Build(host, true, false); Clear(); host.EndFrame(); Wait();
                if (region.CaptureCount != 6) throw new InvalidOperationException("Hidden region repainted.");
                Compare("shown-again", 6, true);
                foreground = true; Compare("foreground-region", 6, true);

                // A previously built Canvas can still contain a marker when its panel closes.
                region.Dispose(); Clear(); host.Canvas.Render(); Wait(); byte[] tombstone = Read();
                Build(reference, false, false); Clear(); reference.EndFrame(); Wait();
                Check(Read(), tombstone, "close-after-marker");
                region = new UiPaperPaintRegion(host, geometry, atlases, Paint);

                // Closing after registration, before rendering, must suppress the queued callback.
                Build(host, true, true); region.Dispose(); Clear(); host.EndFrame(); Wait(); byte[] closed = Read();
                Build(reference, false, false); Clear(); reference.EndFrame(); Wait(); byte[] expected = Read();
                Check(expected, closed, "close-before-callback");
                if (geometry.ReservedBytes != 0) throw new InvalidOperationException("Closed region retained geometry admission.");
                MustThrow<ObjectDisposedException>(() => region.Invalidate());
            }
            finally { region.Dispose(); host.Canvas.Dispose(); reference.Canvas.Dispose(); }
            if (geometry.ReservedBytes != 0 || atlases.ReservedBytes != 0) throw new InvalidOperationException("Region owner leases leaked.");

            void Compare(string stage, long captures, bool zeroUpload)
            {
                Build(reference, false, true); Clear(); reference.EndFrame(); Wait(); byte[] expected = Read();
                Build(host, true, true); Clear(); host.EndFrame(); Wait(); byte[] actual = Read();
                Check(expected, actual, stage);
                if (region.CaptureCount != captures) throw new InvalidOperationException($"Unexpected capture count at {stage}: {region.CaptureCount}.");
                if (zeroUpload && region.LastStats.BufferUploadBytes != 0) throw new InvalidOperationException($"Unchanged capture uploaded at {stage}.");
                results.Add(new { Dpi = dpi, Stage = stage, region.CaptureCount, region.LastStats, PixelsEqual = true });
            }

            void Build(Paper paper, bool cached, bool includeRegion)
            {
                paper.BeginFrame(1f / 60, dpi);
                using (paper.Column("outer").Size(360, 240).BackgroundColor(Color.FromArgb(23, 31, 47)).Enter())
                {
                    using (var panel = paper.Column("clipped").Width(240).Height(160).Margin(margin, 13, 0, 0)
                        .Clip().BackgroundColor(Color.FromArgb(45, 65, 95)).Enter())
                    {
                        var handle = paper.CurrentParent;
                        paper.Draw(ref handle, (canvas, _) => canvas.SetGlobalAlpha(0.75f));
                        if (includeRegion)
                        {
                            if (cached) region.Attach(ref handle, foreground);
                            else if (foreground) paper.DrawForeground(ref handle, Paint);
                            else paper.Draw(ref handle, Paint);
                        }
                        paper.Box("child").Size(95, 40).Margin(48, 37, 0, 0).BackgroundColor(Color.FromArgb(220, 180, 45, 30));
                        paper.DrawForeground(ref handle, (canvas, rect) => canvas.RectFilled(rect.Min.X - 10, rect.Min.Y + 95, 290, 3, new Color32(245, 235, 210, 230)));
                    }
                    using (var popup = paper.Box("popup").Size(180, 48).PositionType(PositionType.SelfDirected).Left(90).Top(75)
                        .Rounded(8).BackgroundColor(Color.FromArgb(235, 45, 35, 75)).Layer(100).Enter())
                        paper.Box("popup-child").Size(80, 4).Margin(12, 13, 0, 0).BackgroundColor(Color.FromArgb(230, 180, 195, 220));
                }
            }
            void Paint(Canvas canvas, Rect rect)
            {
                canvas.SaveState();
                canvas.RectFilled(rect.Min.X - 15, rect.Min.Y + 8, 280, 120, edited ? new Color32(170, 130, 40, 180) : new Color32(25, 165, 95, 180));
                canvas.IntersectScissor(rect.Min.X + 5, rect.Min.Y + 20, 205, 85);
                canvas.RoundedRectFilled(rect.Min.X + 10, rect.Min.Y + 15, 220, 100, 13, new Color32(60, 100, 210, 170));
                canvas.DrawText("Hierarchy row", rect.Min.X + 15, rect.Min.Y + 25, new Color32(245, 245, 245, 255), 15, font);
                canvas.RestoreState();
            }
            void Clear()
            {
                using var command = Graphics.GetCommandBuffer("N4 Paper region clear"); command.SetRenderTarget(target.frameBuffer);
                command.SetViewport(0, 0, (uint)width, (uint)height); command.DisableScissor();
                command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new XEngine.Vector.Color(0, 0, 0, 1)); Graphics.Submit(command);
            }
            byte[] Read()
            {
                var bytes = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(bytes.AsMemory());
                if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
                    for (int y = 0; y < height / 2; y++) for (int x = 0; x < width * 4; x++)
                    { int a = y * width * 4 + x, b = (height - y - 1) * width * 4 + x; (bytes[a], bytes[b]) = (bytes[b], bytes[a]); }
                return bytes;
            }
            void Check(byte[] expected, byte[] actual, string stage)
            {
                Save(expected, $"dpi-{dpi}-{stage}-reference.png", width, height); Save(actual, $"dpi-{dpi}-{stage}-region.png", width, height);
                if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException($"Paper element region differs: {dpi}/{stage}.");
            }
        }
        File.WriteAllText(Path.Combine(destination, "validation.json"), JsonSerializer.Serialize(new { Backend = Graphics.Device!.Backend.ToString(), Results = results,
            Scope = "Actual Paper element-builder callbacks, clipped parent, child and foreground, deferred popup, capture invalidation and queued-close lifecycle. No production Hierarchy migration or input/performance acceptance." },
            new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
    private static void Wait() { using var command = Graphics.GetCommandBuffer("N4 Paper region readback"); Graphics.SubmitAndWait(command); }
    private static void MustThrow<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private void Save(byte[] bytes, string name, int width, int height)
    {
        using var image = new ImageMagick.MagickImage(); image.ReadPixels(bytes,
            new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
        image.Write(Path.Combine(destination, name), ImageMagick.MagickFormat.Png32);
    }
}
