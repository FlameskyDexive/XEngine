// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Local repro for the "dark GenshinCombat scene" issue: renders a grey Standard ground + cube
/// through <see cref="UniversalRenderPipeline"/> on a real Vulkan device with different directional
/// light configurations, reads back pixels, and asserts the directional light actually
/// brightens the scene. Saves inspectable PNGs under docs/superpowers/evidence/genshin/.
/// </summary>
public sealed class GenshinLightingProbeTests : IDisposable
{
    private const int Width = 128;
    private const int Height = 128;

    private readonly GraphicsBackend? _previousBackend;
    private readonly bool _previousPlaying;

    public GenshinLightingProbeTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        _previousPlaying = Application.IsPlaying;
        Application.IsPlaying = true;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
        }
        catch (Exception ex) when (ex.Message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
                                    || ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase))
        {
            GraphicsBackendSelection.Preferred = _previousBackend;
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
        Application.IsPlaying = _previousPlaying;
    }

    [Fact]
    public void Optional_StandardMaterial_DirectionalLight_BrightensScene()
    {
        var device = Graphics.Device as Backends.Vulkan.VulkanGraphicsDevice;
        if (device == null) return; // Vulkan unavailable — skip

        float ambient = RenderAndMeasure(lightEuler: null, "lighting-probe-ambient");
        float mine = RenderAndMeasure(lightEuler: new Float3(50f, -30f, 0f), "lighting-probe-menu");
        float fixedEuler = RenderAndMeasure(lightEuler: new Float3(-50f, 30f, 0f), "lighting-probe-fixed");
        float template = RenderAndMeasure(lightEuler: new Float3(-45f, 45f, 0f), "lighting-probe-template");

        // Measures the cube band (top-left quadrant), which the visible faces occupy.
        Assert.True(ambient < 250f, $"ambient-only brightness {ambient:F1} unexpectedly high");
        Assert.True(fixedEuler > ambient + 40f, $"fixed euler {fixedEuler:F1} must beat ambient {ambient:F1}");
        Assert.True(template > ambient + 40f, $"template light {template:F1} must beat ambient {ambient:F1}");
    }

    private static float RenderAndMeasure(Float3? lightEuler, string evidenceName)
    {
        var device = (Backends.Vulkan.VulkanGraphicsDevice)Graphics.Device!;

        using var target = new RenderTexture(Width, Height, true, new[] { TextureImageFormat.Color4b });
        using var pipeline = new UniversalRenderPipeline();
        using var scene = new Scene();
        using var mesh = Mesh.CreateCube(Float3.One);
        using var material = new Material(Shader.LoadDefault(DefaultShader.Standard));

        var cameraGo = new GameObject("probe cam");
        var camera = cameraGo.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.32f, 0.47f, 0.65f, 1f);
        camera.Pipeline = pipeline;
        cameraGo.Transform.Position = new Float3(0f, 2f, -12f);
        scene.Add(cameraGo);

        if (lightEuler.HasValue)
        {
            var lightGo = new GameObject("sun");
            lightGo.Transform.LocalEulerAngles = lightEuler.Value;
            var light = lightGo.AddComponent<DirectionalLight>();
            light.Intensity = 1.15f;
            light.Color = new Color(1f, 0.96f, 0.9f, 1f);
            scene.Add(lightGo);
        }

        var ground = new GameObject("ground");
        var groundRenderer = ground.AddComponent<MeshRenderer>();
        groundRenderer.Mesh = mesh;
        groundRenderer.Material = material;
        ground.Transform.LocalScale = new Float3(20f, 0.05f, 20f);
        ground.Transform.Position = new Float3(0f, -0.5f, 0f);
        scene.Add(ground);

        var cube = new GameObject("cube");
        var cubeRenderer = cube.AddComponent<MeshRenderer>();
        cubeRenderer.Mesh = mesh;
        cubeRenderer.Material = material;
        cube.Transform.Position = new Float3(0f, 0.5f, 0f);
        scene.Add(cube);

        scene.Enable();
        try
        {
            var data = new RenderingData { SkipUI = true };
            pipeline.Render(camera, in data);
            Graphics.EndFrameAndWait();

            byte[] pixels = device.ReadTexture2D(device.Images[target.InternalTextures[0].Handle.Handle], 4);
            SaveScreenshotPng(evidenceName, pixels);

            // Average brightness of the top-left quadrant (where the cube projects at this framing).
            long sum = 0;
            int count = 0;
            for (int y = 8; y < Height / 2; y++)
            {
                for (int x = 8; x < Width / 2; x++)
                {
                    int i = (y * Width + x) * 4;
                    sum += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
                    count++;
                }
            }
            return count == 0 ? 0 : sum / (float)count;
        }
        finally
        {
            scene.Disable();
        }
    }

    private static void SaveScreenshotPng(string name, byte[] rgba)
    {
        string dir = FindEvidenceDir();
        string path = Path.Combine(dir, name + ".png");
        using var memory = new MemoryStream();
        memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, Width);
        WriteInt(ihdr, 4, Height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);
        byte[] raw = new byte[(Width * 4 + 1) * Height];
        int srcRow = Width * 4;
        for (int y = 0; y < Height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new System.IO.Compression.DeflateStream(deflate, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        byte[] adler = new byte[4];
        WriteInt(adler, 0, (int)Adler32(raw));
        deflate.Write(adler, 0, 4);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        File.WriteAllBytes(path, memory.ToArray());
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
        stream.Write(crcBytes);
    }

    private static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int k = 0; k < 8; k++)
            crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ 0xEDB88320u : crc << 1;
        return crc;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static string FindEvidenceDir()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
            {
                string dir = Path.Combine(current.FullName, "docs", "superpowers", "evidence", "genshin");
                Directory.CreateDirectory(dir);
                return dir;
            }
            current = current.Parent;
        }
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "xengine-genshin"));
        return Path.Combine(Path.GetTempPath(), "xengine-genshin");
    }
}
