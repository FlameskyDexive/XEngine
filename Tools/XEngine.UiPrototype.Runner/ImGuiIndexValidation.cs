using ImGuiNET;
using System.Numerics;
using System.Text.Json;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.UiPrototype;

/// <summary>Independent raster oracle for native 16-bit index rollover,
/// multiple draw-list storage and clip/texture transitions. Not a benchmark.</summary>
internal static class ImGuiIndexValidation
{
    public static unsafe bool Run(string directory)
    {
        Directory.CreateDirectory(directory);
        IntPtr previous = ImGui.GetCurrentContext(), context = ImGui.CreateContext();
        try
        {
            ImGui.SetCurrentContext(context);
            var io = ImGui.GetIO();
            io.NativePtr->IniFilename = null; io.NativePtr->LogFilename = null;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.DisplaySize = new Vector2(320, 200); io.DisplayFramebufferScale = Vector2.One;
            io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
            using var atlas = new Texture2D((uint)width, (uint)height);
            byte[] atlasBytes = new byte[checked(width * height * 4)];
            System.Runtime.InteropServices.Marshal.Copy(pixels, atlasBytes, 0, atlasBytes.Length);
            atlas.SetData(atlasBytes.AsMemory(), 0, 0, (uint)width, (uint)height);
            io.Fonts.SetTexID((IntPtr)1);
            using var blue = new Texture2D(1, 1);
            blue.SetData(new byte[] { 0, 0, 255, 255 }.AsMemory(), 0, 0, 1, 1);
            using var renderer = new ImGuiRhiRenderer();
            using var target = new RenderTexture(320, 200, true, [TextureImageFormat.Color4b]);
            renderer.Target = target.frameBuffer;
            ImGui.NewFrame();
            var back = ImGui.GetBackgroundDrawList();
            // 18,000 quads force a native draw command with nonzero VtxOffset.
            // Keep their raster area tiny; this verifies indices without a fill-rate stress.
            for (int i = 0; i < 18000; i++) back.AddRectFilled(new Vector2(1, 1), new Vector2(2, 2), 0xffffffff);
            back.AddRectFilled(new Vector2(20, 20), new Vector2(100, 100), 0xff0000ff);
            var front = ImGui.GetForegroundDrawList();
            front.AddRectFilled(new Vector2(60, 60), new Vector2(140, 140), 0xff00ff00);
            front.PushClipRect(new Vector2(180, 40), new Vector2(220, 80), true);
            front.AddImage((IntPtr)2, new Vector2(160, 20), new Vector2(240, 100));
            front.PopClipRect();
            ImGui.Render();
            var data = ImGui.GetDrawData();
            bool offset = false;
            for (int li = 0; li < data.CmdListsCount; li++)
                for (int ci = 0; ci < data.CmdLists[li].CmdBuffer.Size; ci++)
                    offset |= data.CmdLists[li].CmdBuffer[ci].VtxOffset > 0;
            using (var clear = Graphics.GetCommandBuffer("N1 large-index clear"))
            {
                clear.SetRenderTarget(target.frameBuffer); clear.SetViewport(0, 0, 320, 200);
                clear.DisableScissor();
                clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new XEngine.Vector.Color(0f, 0f, 0f, 1f));
                Graphics.Submit(clear);
            }
            renderer.Render(data, atlas, blue);
            using (var flush = Graphics.GetCommandBuffer("N1 large-index flush")) Graphics.SubmitAndWait(flush);
            byte[] bytes = new byte[target.MainTexture.GetSize()];
            target.MainTexture.GetData<byte>(bytes.AsMemory());
            bool red = Pixel(bytes, 30, 30, 255, 0, 0), green = Pixel(bytes, 80, 80, 0, 255, 0);
            bool clippedBlue = Pixel(bytes, 200, 60, 0, 0, 255), outside = Pixel(bytes, 170, 60, 0, 0, 0);
            bool passed = data.TotalVtxCount > 65535 && data.CmdListsCount >= 2 && offset && red && green && clippedBlue && outside;
            using var image = new ImageMagick.MagickImage();
            image.ReadPixels(bytes, new ImageMagick.PixelReadSettings(320, 200, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
            image.Write(Path.Combine(directory, "imgui-large-index.png"), ImageMagick.MagickFormat.Png32);
            File.WriteAllText(Path.Combine(directory, "imgui-large-index.json"), JsonSerializer.Serialize(new {
                Passed = passed, data.TotalVtxCount, data.CmdListsCount, NonzeroVertexOffset = offset,
                FirstListRed = red, SecondListGreen = green, ClippedBlue = clippedBlue, OutsideClipBlack = outside
            }, new JsonSerializerOptions { WriteIndented = true }));
            return passed;
        }
        finally { ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous); }
    }

    private static bool Pixel(byte[] data, int x, int y, byte r, byte g, byte b)
    {
        int at = (y * 320 + x) * 4;
        return Math.Abs(data[at] - r) <= 1 && Math.Abs(data[at + 1] - g) <= 1
            && Math.Abs(data[at + 2] - b) <= 1 && data[at + 3] == 255;
    }
}
