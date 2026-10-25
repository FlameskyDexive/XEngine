// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Video;
using XEngine.Vector;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

/// <summary>
/// N4 output-integration acceptance: the VideoPlayer component drives a real playback
/// session, presents frames through MaterialOverride onto a quad, and the quad renders
/// through the REAL pipeline (UniversalRenderPipeline on a real Vulkan device) — the
/// engine's "3D screen". Readback is saved as evidence PNG. Skips when no Vulkan device
/// is available.
/// </summary>
public sealed class VideoOutputAcceptanceTests : IDisposable
{
    private const int Width = 480;
    private const int Height = 270;

    private readonly ITestOutputHelper _output;
    private readonly GraphicsBackend? _previousBackend;
    private readonly AssetBackendBase? _previousAssetBackend;
    private bool _graphicsReady;

    public VideoOutputAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
        _previousBackend = GraphicsBackendSelection.Preferred;
        _previousAssetBackend = AssetDatabase.Current;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
            _graphicsReady = true;
        }
        catch (Exception ex) when (IsExpectedGpuUnavailable(ex))
        {
            GraphicsBackendSelection.Preferred = _previousBackend;
        }
    }

    public void Dispose()
    {
        if (_graphicsReady)
            Graphics.Dispose();
        AssetDatabase.Current = _previousAssetBackend;
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    private static string MediaRoot => Path.Combine(FindRepoRoot(), "XEngine.Runtime.Test", "Media");

    private static string EvidenceDir => Video.VideoTestEvidence.Root;

    [Video.FFmpegFact]
    public void Optional_VideoPlayer_MaterialOverride_Renders3DScreen_OrSkip()
    {
        FFmpegNativeLoader.EnsureLoaded();

        if (!_graphicsReady)
            return;

        VulkanGraphicsDevice? device;
        try
        {
            device = Graphics.Device as VulkanGraphicsDevice;
        }
        catch (Exception ex) when (IsExpectedGpuUnavailable(ex))
        {
            return; // Vulkan unavailable on this host — skip (lazy Device creation throws)
        }
        if (device == null) return;

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("video acceptance camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 52f;
        camera.FarClipPlane = 2000f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(160, 120, -320);
        scene.Add(cameraObject);

        // The "3D screen": a quad in the XY plane facing the camera, with the video
        // player presenting through MaterialOverride.
        var material = new Material(Shader.LoadDefault(DefaultShader.Unlit));
        var screenObject = new GameObject("video screen");
        MeshRenderer screenRenderer = screenObject.AddComponent<MeshRenderer>();
        screenRenderer.Mesh = new AssetRef<Mesh>(BuildQuadMesh(320, 180));
        screenRenderer.Materials.Add(new AssetRef<Material>(material));
        screenObject.Transform.Position = new Float3(160, 120, 0);
        scene.Add(screenObject);

        var playerObject = new GameObject("video player");
        VideoPlayer player = playerObject.AddComponent<VideoPlayer>();
        player.AudioOutputMode = VideoAudioOutputMode.None; // headless test: no device
        player.RenderMode = VideoRenderMode.MaterialOverride;
        player.TargetRenderer = screenRenderer;
        player.Source = VideoSource.Url;
        player.Url = Path.Combine(MediaRoot, "n0-h264-aac.mp4");
        player.IsLooping = true;
        scene.Add(playerObject);

        scene.Enable();
        try
        {
            player.Play();
            var ready = Stopwatch.StartNew();
            while (!player.IsPlaying && ready.ElapsedMilliseconds < 10_000)
            {
                player.Update(); // presentation pump (normally driven by the engine tick)
                Thread.Yield();
            }

            Assert.True(player.IsPlaying, "video player never reached Playing");

            // Render warm-up + ~0.5s of playback through the pump.
            for (int i = 0; i < 64; i++)
            {
                player.Update();
                RenderFrame(camera, target);
            }

            device.WaitIdle();
            byte[] rgba = device.ReadTexture2D(device.Images[target.MainTexture.Handle.Handle], 4);
            int uniqueColors = CountUniqueColors(rgba);
            int litPixels = CountForegroundPixels(rgba);
            _output.WriteLine($"screen: uniqueColors={uniqueColors} litPixels={litPixels} time={player.Time:F3}s");
            SaveScreenshotPng("2026-09-12-n4-video-3d-screen", rgba, Width, Height, EvidenceDir);

            Assert.True(uniqueColors > 64, $"3D screen shows no video detail (unique colors {uniqueColors})");
            Assert.True(litPixels > Width * Height / 20, $"3D screen barely visible ({litPixels} lit px)");
            Assert.True(player.Time > 0.1, "video time did not advance while rendering");
        }
        finally
        {
            // Release contract: disabling the player stops + disposes the session.
            player.OnDisable();
            player.OnDispose();
            scene.Dispose();
        }
    }

    [Video.FFmpegFact]
    public void VideoPlayer_MultiPlayer_IndependentSessions()
    {
        FFmpegNativeLoader.EnsureLoaded();

        if (!_graphicsReady)
            return;

        var scene = new Scene();
        var playerObjectA = new GameObject("player A");
        var playerA = playerObjectA.AddComponent<VideoPlayer>();
        var playerObjectB = new GameObject("player B");
        var playerB = playerObjectB.AddComponent<VideoPlayer>();
        playerA.AudioOutputMode = VideoAudioOutputMode.None;
        playerB.AudioOutputMode = VideoAudioOutputMode.None;
        playerA.Source = VideoSource.Url;
        playerA.Url = Path.Combine(MediaRoot, "n0-h264-aac.mp4");
        playerB.Source = VideoSource.Url;
        playerB.Url = Path.Combine(MediaRoot, "n0-vp9-opus.webm");
        scene.Add(playerObjectA);
        scene.Add(playerObjectB);

        playerA.Play();
        playerB.Play();

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 10_000 && (!playerA.IsReady || !playerB.IsReady))
        {
            playerA.Update();
            playerB.Update();
            Thread.Yield();
        }

        Assert.True(playerA.IsReady && playerB.IsReady, "both players must prepare");

        double timeA = playerA.Time;
        var pumpSw = Stopwatch.StartNew();
        while (pumpSw.ElapsedMilliseconds < 500)
        {
            playerA.Update();
            playerB.Update();
            Thread.Yield();
        }

        Assert.True(playerA.Time > timeA, "player A time did not advance");

        // Release contract: dispose stops cleanly and stays disposed.
        playerA.OnDispose();
        playerA.Update(); // disposed session: must be a safe no-op

        playerB.Pause();
        Assert.False(playerA.IsPlaying); // disposed session: playback stopped
        Assert.True(playerB.Time >= 0);
        playerB.OnDispose();
    }

    // ---------------------------------------------------------------- helpers

    private static Mesh BuildQuadMesh(float width, float height)
    {
        float halfW = width / 2f, halfH = height / 2f;
        var positions = new Float3[4];
        var normals = new Float3[4];
        var uvs = new Float2[4];
        positions[0] = new Float3(-halfW, -halfH, 0); uvs[0] = new Float2(0, 0);
        positions[1] = new Float3(halfW, -halfH, 0); uvs[1] = new Float2(1, 0);
        positions[2] = new Float3(-halfW, halfH, 0); uvs[2] = new Float2(0, 1);
        positions[3] = new Float3(halfW, halfH, 0); uvs[3] = new Float2(1, 1);
        normals[0] = normals[1] = normals[2] = normals[3] = new Float3(0, 0, 1);

        return new Mesh
        {
            Vertices = positions,
            Normals = normals,
            UV = uvs,
            Indices = [0u, 2u, 1u, 1u, 2u, 3u],
        };
    }

    private static void RenderFrame(Camera camera, RenderTexture target)
    {
        using (var clear = Graphics.GetCommandBuffer("video-acceptance-clear"))
        {
            clear.SetRenderTarget(target.frameBuffer);
            clear.SetViewport(0, 0, (uint)Width, (uint)Height);
            clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.07f, 0.08f, 0.11f, 1f), 1f);
            Graphics.Submit(clear);
        }

        camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
        Graphics.EndFrameAndWait();
    }

    private static int CountForegroundPixels(byte[] rgba)
    {
        int lit = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] + rgba[i + 1] + rgba[i + 2] > 120) lit++;
        }

        return lit;
    }

    private static int CountUniqueColors(byte[] rgba)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            seen.Add((rgba[i] << 16) | (rgba[i + 1] << 8) | rgba[i + 2]);
        }

        return seen.Count;
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        // Delegate to the shared contract so wrapped "backend unavailable" messages (e.g. the
        // Vulkan loader missing on macOS) are recognized here too.
        return GraphicsTestAvailability.IsBackendUnavailable(ex);
    }

    private static string SaveScreenshotPng(string name, byte[] rgba, int width, int height, string dir)
    {
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
            System.Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
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
        return path;
    }

    private static void WritePngChunk(MemoryStream memory, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteBigEndianInt32(length, 0, data.Length);
        memory.Write(length, 0, 4);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        memory.Write(typeBytes, 0, 4);
        memory.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
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

    private static uint[] Crc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint[] table = Crc32Table();
        uint crc = 0xFFFFFFFF;
        foreach (byte t in type)
        {
            crc = table[(crc ^ t) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte t in data)
        {
            crc = table[(crc ^ t) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return current!.FullName;
    }
}
