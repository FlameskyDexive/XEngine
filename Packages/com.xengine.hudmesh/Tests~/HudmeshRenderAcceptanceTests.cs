using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime;
using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Hudmesh.Tests;

/// <summary>
/// Pixel-level acceptance through the REAL rendering path: a scene with a camera and three
/// full nameplates (blood bar, texts, icon), a floating damage number and a chat bubble,
/// rendered by <see cref="UniversalRenderPipeline"/> into a render texture on a real Vulkan
/// device. Saves an inspectable PNG next to the package evidence and asserts the HUD quads
/// actually reached the framebuffer (foreground coverage + color variety over the flat clear
/// color). Skips when no Vulkan device is available (same pattern as the engine's
/// VirtualGeometry/RhiContract acceptance tests).
/// </summary>
public sealed class HudmeshRenderAcceptanceTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;

    private readonly GraphicsBackend? _previousBackend;

    public HudmeshRenderAcceptanceTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
        }
        catch (Exception)
        {
            GraphicsBackendSelection.Preferred = _previousBackend; // GPU unavailable — tests skip
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void Optional_Hudmesh_PipelineRendersNameplates_OrSkip()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently like the engine acceptance tests

        HudManager manager = HudManager.Init(HudSpriteTable.CreateBundled());

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("hudmesh acceptance camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 100f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(0f, 1.2f, -6.5f);
        scene.Add(cameraObject);
        scene.Add(manager.GameObject);

        string[] names = { "Aelric", "Brissa", "Corvin" };
        Hud[] huds = new Hud[3];
        for (int i = 0; i < 3; i++)
        {
            var anchor = new GameObject($"Target {i + 1}");
            anchor.Transform.Position = new Float3(-2.2f + 2.2f * i, 1f, 0f);

            Hud hud = manager.CreateHud(i + 1);
            hud.Init(anchor.Transform, new Float3(0f, 1.1f, 0f));
            hud.SetBloodVisible(true, (HudRelation)int.Clamp(i * 2, 0, 4));
            hud.SetBloodPercent(0.35f + 0.25f * i);
            hud.SetText(HudSlot.Name, names[i], HudRelation.Self);
            hud.SetText(HudSlot.Title, "Knight", HudRelation.Self);
            hud.SetIcon(HudSlot.GuildIcon, "UI_headtop_sign_001_6", HudRelation.Self);
            scene.Add(anchor);
            huds[i] = hud;
        }

        // Floating damage numbers in both glyph modes (digit atlas 'b', SDF text) on the
        // middle target, plus a chat bubble.
        huds[1].PopNumber(0, -512, new Float3(0.3f, 0.5f, 0f));
        huds[1].PopNumber(1, 268, new Float3(-0.3f, 0.7f, 0f));
        huds[1].Talk(0, "Hello from Hudmesh!");

        scene.Enable();
        try
        {
            // Warm-up: shader compilation, glyph atlas population, texture uploads.
            for (int warmup = 0; warmup < 48; warmup++)
                RenderFrame(camera, target);

            RenderFrame(camera, target);
            device.WaitIdle();
            byte[] rgba = device.ReadTexture2D(device.Images[target.MainTexture.Handle.Handle], 4);

            // ReadTexture2D returns bottom-up rows; flip to top-down for the PNG.
            byte[] flipped = new byte[rgba.Length];
            int rowBytes = Width * 4;
            for (int y = 0; y < Height; y++)
                Buffer.BlockCopy(rgba, y * rowBytes, flipped, (Height - 1 - y) * rowBytes, rowBytes);
            rgba = flipped;

            int foreground = 0;
            var colors = new HashSet<uint>();
            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (rgba[i] + rgba[i + 1] + rgba[i + 2] > 120)
                    foreground++;
                uint key = (uint)(rgba[i] | (rgba[i + 1] << 8) | (rgba[i + 2] << 16));
                colors.Add(key);
            }

            string path = SaveScreenshotPng("hudmesh-acceptance-nameplates", rgba, Width, Height);
            Assert.True(colors.Count > 32,
                $"HUD did not render: only {colors.Count} unique colors (flat background). Screenshot: {path}");
            Assert.True(foreground > Width * Height * 0.02,
                $"HUD did not render: only {foreground} foreground pixels. Screenshot: {path}");
        }
        finally
        {
            for (int i = 0; i < 3; i++)
                manager.DeleteHud(i + 1);
        }
    }

    private static void RenderFrame(Camera camera, RenderTexture target)
    {
        using (var clear = Graphics.GetCommandBuffer("hudmesh-acceptance-clear"))
        {
            clear.SetRenderTarget(target.frameBuffer);
            clear.SetViewport(0, 0, (uint)Width, (uint)Height);
            clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.07f, 0.08f, 0.11f, 1f), 1f);
            Graphics.Submit(clear);
        }
        camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
        Graphics.EndFrameAndWait();
    }

    // ── PNG helpers (self-contained, mirrors the engine acceptance test writer) ──

    private static string SaveScreenshotPng(string name, byte[] rgba, int width, int height)
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable("XENGINE_HUDMESH_SCREENSHOT_DIR");
        string dir = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "docs", "superpowers", "evidence", "packages", "hudmesh"))
            : Path.GetFullPath(overrideDirectory);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");

        using var memory = new MemoryStream();
        memory.WriteByte(0x89); memory.WriteByte(0x50); memory.WriteByte(0x4E); memory.WriteByte(0x47);
        memory.WriteByte(0x0D); memory.WriteByte(0x0A); memory.WriteByte(0x1A); memory.WriteByte(0x0A);

        byte[] ihdr = new byte[13];
        WriteBigEndianInt32(ihdr, 0, width);
        WriteBigEndianInt32(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WritePngChunk(memory, "IHDR", ihdr);

        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        byte[] idat;
        using (var deflate = new MemoryStream())
        {
            deflate.WriteByte(0x78);
            deflate.WriteByte(0x9C);
            using (var compressor = new System.IO.Compression.DeflateStream(deflate, System.IO.Compression.CompressionLevel.Optimal, true))
                compressor.Write(raw, 0, raw.Length);
            uint adler = Adler32(raw);
            byte[] adlerBytes = new byte[4];
            WriteBigEndianInt32(adlerBytes, 0, (int)adler);
            deflate.Write(adlerBytes, 0, 4);
            idat = deflate.ToArray();
        }
        WritePngChunk(memory, "IDAT", idat);
        WritePngChunk(memory, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, memory.ToArray());
        return Path.GetFullPath(path);
    }

    private static void WritePngChunk(MemoryStream memory, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteBigEndianInt32(length, 0, data.Length);
        memory.Write(length, 0, 4);
        foreach (char c in type)
            memory.WriteByte((byte)c);
        memory.Write(data, 0, data.Length);
        uint crc = Crc32(type, data);
        byte[] crcBytes = new byte[4];
        WriteBigEndianInt32(crcBytes, 0, (int)crc);
        memory.Write(crcBytes, 0, 4);
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte t in data)
        {
            a = (a + t) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint Crc32(string type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (char c in type)
        {
            crc ^= c;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        foreach (byte t in data)
        {
            crc ^= t;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        }
        return crc ^ 0xFFFFFFFF;
    }
}
