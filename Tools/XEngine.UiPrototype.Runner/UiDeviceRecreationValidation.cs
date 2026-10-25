using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.UiPrototype;

internal static class UiDeviceRecreationValidation
{
    public static void Run(string destination)
    {
        Directory.CreateDirectory(destination);
        BuiltInAssets.Initialize();
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        byte[]? baseline = null;
        object? previousDevice = null;
        UiQuillPaintCache? oldPaint = null;
        UiCanvasAtlasOwner? oldOwner = null;
        Canvas? oldCanvas = null;
        PaperRenderer? oldRenderer = null;
        var budget = new UiMemoryBudget(8 * 1024 * 1024);
        const int width = 320, height = 120;
        try
        {
            for (int cycle = 0; cycle < 3; cycle++)
            {
                Graphics.Initialize(false);
                var device = Graphics.Device!;
                if (ReferenceEquals(previousDevice, device)) throw new InvalidOperationException("Device was not recreated.");
                device.BeginFrame();
                if (oldPaint != null)
                {
                    Reject(() => oldPaint.Render(null, width, height, false));
                    Reject(() => oldPaint.Capture(oldCanvas!));
                    Reject(() => oldOwner!.CreateTexture(1, 1));
                    oldPaint.Dispose(); oldCanvas!.Dispose(); oldRenderer!.Dispose();
                    oldPaint = null; oldCanvas = null; oldOwner = null; oldRenderer = null;
                    if (budget.ReservedBytes != 0) throw new InvalidOperationException("Old device owners retained admission charges.");
                }
                var renderer = oldRenderer = new PaperRenderer(); renderer.Initialize(width, height);
                var owner = oldOwner = new UiCanvasAtlasOwner(renderer, budget);
                var canvas = oldCanvas = new Canvas(owner, new FontAtlasSettings { AtlasSize = 512 });
                var paint = oldPaint = new UiQuillPaintCache(2048, 4096, 2, 16, budget);
                canvas.BeginFrame(width, height);
                canvas.RectFilled(0, 0, width, height, new Color32(22, 30, 44, 255));
                canvas.RoundedRectFilled(8, 8, 304, 104, 10, new Color32(35, 100, 125, 255));
                canvas.DrawText("Device recreated", 26, 35, new Color32(255, 255, 255, 255), 24, font);
                paint.Capture(canvas); owner.RetireObsolete(canvas);
                using (var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]))
                {
                    renderer.SetRenderTarget(target.frameBuffer); canvas.Render();
                    using (var referenceWait = Graphics.GetCommandBuffer("Device recreation reference")) Graphics.SubmitAndWait(referenceWait);
                    var referencePixels = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(referencePixels.AsMemory());
                    paint.Render(target.frameBuffer, width, height);
                    using (var wait = Graphics.GetCommandBuffer("Device recreation readback")) Graphics.SubmitAndWait(wait);
                    var pixels = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(pixels.AsMemory());
                    if (!referencePixels.AsSpan().SequenceEqual(pixels)) throw new InvalidOperationException("Recreated paint differs from the Paper reference.");
                    if (baseline != null && !baseline.AsSpan().SequenceEqual(pixels)) throw new InvalidOperationException("Recreated device changed pixels.");
                    int bright = 0;
                    for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 200 && pixels[i + 1] > 200 && pixels[i + 2] > 200) bright++;
                    if (bright < 100) throw new InvalidOperationException("Text was absent from recreation output.");
                    baseline ??= pixels;
                    using var image = new ImageMagick.MagickImage();
                    image.ReadPixels(pixels, new ImageMagick.PixelReadSettings(width, height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
                    image.Write(Path.Combine(destination, $"cycle-{cycle}.png"), ImageMagick.MagickFormat.Png32);
                }
                previousDevice = device;
                device.EndFrame(); device.WaitIdle();
                Graphics.Dispose();
                Console.WriteLine($"Device cycle {cycle}: pixels verified; device disposed with stale CPU owners retained.");
            }
            oldPaint!.Dispose(); oldCanvas!.Dispose(); oldRenderer!.Dispose();
            oldPaint = null; oldCanvas = null; oldRenderer = null;
            if (budget.ReservedBytes != 0) throw new InvalidOperationException("Final owner charges remain.");
            File.WriteAllText(Path.Combine(destination, "validation.json"), "{\"Cycles\":3,\"DeviceReplacements\":2,\"OldOwnerRejections\":6,\"PixelsEqual\":true,\"PaperReferenceEqual\":true,\"AdmissionBytesAfterDispose\":0}");
        }
        finally { oldPaint?.Dispose(); oldCanvas?.Dispose(); oldRenderer?.Dispose(); Graphics.Dispose(); }
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Old device owner accepted work on a replacement device.");
    }
}
