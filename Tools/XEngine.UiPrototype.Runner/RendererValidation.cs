using System.Text.Json;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.UiPrototype;

internal static class RendererValidation
{
    public static bool Run(string directory, string name, Func<IPrototypeAdapter> create)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>(); bool passed = true;
        using var borrowed = new Texture2D(2, 2);
        Fill(borrowed, 200, 30, 40);
        for (int cycle = 0; cycle < 3; cycle++)
        {
            var adapter = create();
            try
            {
                foreach (var size in new[] { (640, 360, 1f), (960, 540, 1.5f), (800, 600, 2f) })
                {
                    Fill(borrowed, 200, 30, 40);
                    adapter.SetInput(new PrototypeInput(-1, -1));
                    adapter.Prepare(size.Item1 / size.Item3, size.Item2 / size.Item3, size.Item3, borrowed);
                    byte[] before = Capture(adapter, size.Item1, size.Item2);
                    bool initial = PixelIs(before, size.Item1, size.Item2, 200, 30, 40);
                    long builds = adapter.BuildCount;
                    Fill(borrowed, 30, 200, 40);
                    adapter.Prepare(size.Item1 / size.Item3, size.Item2 / size.Item3, size.Item3, borrowed);
                    byte[] updated = Capture(adapter, size.Item1, size.Item2);
                    bool update = PixelIs(updated, size.Item1, size.Item2, 30, 200, 40);
                    bool reused = name != "retained" || adapter.BuildCount == builds;
                    using var replacement = new Texture2D(2, 2);
                    Fill(replacement, 30, 40, 200);
                    adapter.Prepare(size.Item1 / size.Item3, size.Item2 / size.Item3, size.Item3, replacement);
                    byte[] replaced = Capture(adapter, size.Item1, size.Item2);
                    bool replace = PixelIs(replaced, size.Item1, size.Item2, 30, 40, 200);
                    bool itemPassed = initial && update && reused && replace;
                    passed &= itemPassed;
                    checks.Add(new { Cycle = cycle, Width = size.Item1, Height = size.Item2, Scale = size.Item3,
                        Initial = initial, ContentUpdate = update, RetainedContentReuse = reused, Replacement = replace, Passed = itemPassed });
                    if (cycle == 0) Save(replaced, size.Item1, size.Item2, Path.Combine(directory, $"{name}-{size.Item1}x{size.Item2}.png"));
                }
            }
            finally { adapter.Dispose(); adapter.Dispose(); Flush(); }
            // Disposal must not destroy the borrowed viewport. Test after each cycle.
            var data = new byte[16]; borrowed.GetData<byte>(data.AsMemory());
            bool alive = data[0] == 30 && data[1] == 200 && data[2] == 40;
            passed &= alive; checks.Add(new { Cycle = cycle, BorrowedTextureSurvived = alive });
        }
        if (name == "imgui")
        {
            bool indices = ImGuiIndexValidation.Run(directory);
            passed &= indices;
            checks.Add(new { LargeIndicesMultipleListsAndClip = indices });
        }
        File.WriteAllText(Path.Combine(directory, name + "-validation.json"), JsonSerializer.Serialize(new {
            Adapter = name, Passed = passed, Checks = checks,
            Scope = "RHI texture content/replacement, offscreen dimensions/DPI, repeated adapter disposal; not OS resize, device loss or a memory-leak proof"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    private static void Fill(Texture2D texture, byte r, byte g, byte b)
        => texture.SetData(new Memory<byte>([r,g,b,255,r,g,b,255,r,g,b,255,r,g,b,255]), 0, 0, 2, 2);

    private static bool PixelIs(byte[] pixels, int width, int height, byte r, byte g, byte b)
    {
        int at = ((height - 20) * width + width - 20) * 4;
        return Math.Abs(pixels[at] - r) <= 1 && Math.Abs(pixels[at + 1] - g) <= 1
            && Math.Abs(pixels[at + 2] - b) <= 1 && pixels[at + 3] == 255;
    }

    internal static byte[] Capture(IPrototypeAdapter adapter, int width, int height)
    {
        using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
        adapter.SetTarget(target.frameBuffer);
        try
        {
            using (var clear = Graphics.GetCommandBuffer("N1 validation clear"))
            {
                clear.SetRenderTarget(target.frameBuffer); clear.SetViewport(0, 0, (uint)width, (uint)height);
                clear.DisableScissor(); clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new XEngine.Vector.Color(0f, 0f, 0f, 1f));
                Graphics.Submit(clear);
            }
            adapter.Render(); Flush();
            byte[] data = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(data.AsMemory()); return data;
        }
        finally { adapter.SetTarget(null); }
    }
    private static void Flush()
    { using var flush = Graphics.GetCommandBuffer("N1 validation flush"); Graphics.SubmitAndWait(flush); }
    private static void Save(byte[] bytes, int width, int height, string path)
    {
        using var image = new ImageMagick.MagickImage();
        image.ReadPixels(bytes, new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
        image.Write(path, ImageMagick.MagickFormat.Png32);
    }
}
