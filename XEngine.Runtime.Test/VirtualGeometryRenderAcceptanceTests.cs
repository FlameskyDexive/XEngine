// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Rendering.VirtualGeometry;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.VirtualGeometry;
using XEngine.Runtime.VirtualGeometry.Generation;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Pixel-level acceptance for virtual geometry through the REAL rendering path: a scene
/// with a <see cref="VirtualGeometryRenderer"/> rendered by <see cref="UniversalRenderPipeline"/>
/// into a render texture on a real Vulkan device, with the feature data served through an
/// in-memory asset backend (the same sub-asset GUID the importer derives). Saves
/// inspectable PNGs under docs/screenshots/ and asserts LOD traded geometry for screen
/// error at equal silhouette. Skips when no Vulkan device is available.
/// </summary>
public sealed class VirtualGeometryRenderAcceptanceTests : IDisposable
{
    private const int Width = 480;
    private const int Height = 270;

    private readonly GraphicsBackend? _previousBackend;
    private readonly AssetBackendBase? _previousAssetBackend;
    private bool _graphicsReady;

    private sealed class SingleAssetBackend(EngineObject asset) : AssetBackendBase
    {
        protected override EngineObject? LoadFresh(Guid assetId)
        {
            SetLoaded(assetId, asset);
            return asset;
        }
    }

    public VirtualGeometryRenderAcceptanceTests()
    {
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

    [Fact]
    public void Optional_VirtualGeometry_PipelineRendersLod_OrSkip()
    {
        if (!_graphicsReady)
            return;

        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null) return; // Vulkan unavailable on this host — skip

        // ── Bake (M1) on a 7200-triangle height field, served as the importer-derived sub-asset. ──
        Mesh mesh = VirtualGeometryBakeTests.BuildGridMesh(60, 60, flat: false);
        mesh.AssetID = Guid.NewGuid();
        var data = VirtualGeometryBaker.Generate(mesh, VirtualGeometryBakeOptions.Default);
        data.AssetID = VirtualGeometryRenderer.DeriveVirtualGeometryGuid(mesh.AssetID, mesh.Name ?? "Mesh");
        AssetDatabase.Current = new SingleAssetBackend(data);
        try
        {
            Assert.True(data.Levels.Length >= 3, "expected multiple LOD levels");

            var scene = new Scene();
            scene.Fog.Mode = Scene.FogParams.FogMode.Off;
            scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
            scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

            var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            var cameraObject = new GameObject("VG acceptance camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.Target = target;
            camera.ClearFlags = CameraClearFlags.SolidColor;
            camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
            camera.FieldOfView = 52f;
            camera.FarClipPlane = 2000f;
            camera.Pipeline = new UniversalRenderPipeline();
            cameraObject.Transform.Position = new Float3(30, 30, -26);
            scene.Add(cameraObject);

            var material = new Material(Shader.LoadDefault(DefaultShader.Unlit));
            var vgObject = new GameObject("VG heightfield");
            VirtualGeometryRenderer renderer = vgObject.AddComponent<VirtualGeometryRenderer>();
            renderer.Mesh = new AssetRef<Mesh>(mesh);
            renderer.Materials.Add(new AssetRef<Material>(material));
            renderer.PixelError = 1f;
            renderer.UseGpuCulling = false; // CPU reference first; the GPU section flips it
            scene.Add(vgObject);

            scene.Enable();
            try
            {
                // First frame resolves the sub-asset and kicks off the async GPU upload;
                // render until the renderer actually selects clusters.
                for (int warmup = 0; warmup < 64 && renderer.LastVisibleTriangles == 0; warmup++)
                    RenderFrame(camera, target);

                Assert.NotNull(renderer.Data);
                Assert.True(renderer.LastVisibleTriangles > 0,
                    $"virtual geometry renderer never became render-ready (data resolved: {renderer.Data != null})");

                (byte[] fine, int fineTris) = RenderAt(device, camera, target, renderer,
                    new Float3(30, 30, -26), pixelError: 0.25f);
                // Same far view twice: tight threshold forces detail, loose one coarsens.
                // Equal silhouette + fewer triangles is the virtual geometry contract.
                (byte[] farFine, int farFineTris) = RenderAt(device, camera, target, renderer,
                    new Float3(30, 30, -300), pixelError: 0.25f);
                (byte[] farCoarse, int farCoarseTris) = RenderAt(device, camera, target, renderer,
                    new Float3(30, 30, -300), pixelError: 3f);

                SaveScreenshotPng("virtual-geometry-pipeline-fine", fine, Width, Height);
                SaveScreenshotPng("virtual-geometry-pipeline-far-fine", farFine, Width, Height);
                SaveScreenshotPng("virtual-geometry-pipeline-far-coarse", farCoarse, Width, Height);
                string pairScreenshot = SaveScreenshotPng("virtual-geometry-pipeline-pair",
                    JoinSideBySide(farFine, farCoarse), Width * 2, Height);

                int fineLit = CountForegroundPixels(fine);
                Assert.True(fineLit > Width * Height / 20,
                    $"fine render must show the mesh, got {fineLit} lit pixels / {CountUniqueColors(fine)} colors, tris {fineTris}");

                int farFineLit = CountForegroundPixels(farFine);
                int farCoarseLit = CountForegroundPixels(farCoarse);
                Assert.True(farFineLit > Width * Height / 80,
                    $"far fine render must show the mesh, got {farFineLit} lit pixels, tris {farFineTris}");
                float coverageDelta = Math.Abs(farFineLit - farCoarseLit) / (float)Math.Max(1, farFineLit);
                Assert.True(coverageDelta < 0.25f,
                    $"LOD must keep the silhouette at equal view: coverage delta {coverageDelta:P1} (fine {farFineLit} px / {farFineTris} tris, coarse {farCoarseLit} px / {farCoarseTris} tris)");
                Assert.True(farCoarseTris < farFineTris / 2,
                    $"coarse view must cut triangles hard: fine={farFineTris} coarse={farCoarseTris}");

                // ── M4: GPU compute cull parity — same views through the compute path must
                // match the CPU selector's pixels closely (identical decision formula). ──
                Assert.True(VirtualGeometryGpuCulling.IsSupported, "Vulkan device must support compute");
                renderer.UseGpuCulling = true;
                for (int warmup = 0; warmup < 8; warmup++)
                    RenderFrame(camera, target); // upload cluster SSBOs + first dispatches

                (byte[] gpuFarFine, _) = RenderAt(device, camera, target, renderer,
                    new Float3(30, 30, -300), pixelError: 0.25f);
                (byte[] gpuFarCoarse, _) = RenderAt(device, camera, target, renderer,
                    new Float3(30, 30, -300), pixelError: 3f);

                SaveScreenshotPng("virtual-geometry-gpu-cull-far-fine", gpuFarFine, Width, Height);
                SaveScreenshotPng("virtual-geometry-gpu-cull-far-coarse", gpuFarCoarse, Width, Height);
                SaveScreenshotPng("virtual-geometry-gpu-cpu-pair",
                    JoinSideBySide(farFine, gpuFarFine), Width * 2, Height);

                int gpuFineLit = CountForegroundPixels(gpuFarFine);
                int gpuCoarseLit = CountForegroundPixels(gpuFarCoarse);
                Assert.True(gpuFineLit > Width * Height / 80,
                    $"GPU cull far-fine render must show the mesh, got {gpuFineLit} lit pixels / {CountUniqueColors(gpuFarFine)} colors");
                float gpuCoverageDelta = Math.Abs(gpuFineLit - gpuCoarseLit) / (float)Math.Max(1, gpuFineLit);
                Assert.True(gpuCoverageDelta < 0.25f,
                    $"GPU LOD must keep the silhouette: coverage delta {gpuCoverageDelta:P1} (fine {gpuFineLit} px, coarse {gpuCoarseLit} px)");

                float fineMismatch = PixelMismatchRatio(farFine, gpuFarFine);
                float coarseMismatch = PixelMismatchRatio(farCoarse, gpuFarCoarse);
                Assert.True(fineMismatch < 0.10f,
                    $"GPU cull must match the CPU selector's selection (far-fine mismatch {fineMismatch:P1})");
                Assert.True(coarseMismatch < 0.10f,
                    $"GPU cull must match the CPU selector's selection (far-coarse mismatch {coarseMismatch:P1})");

                var lodTriangles = new int[data.Levels.Length];
                for (int level = 0; level < lodTriangles.Length; level++)
                    lodTriangles[level] = data.Levels[level].TriangleCount;
                File.WriteAllText(Path.ChangeExtension(pairScreenshot, ".json"),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        width = Width, height = Height, sourceTriangles = data.SourceTriangleCount,
                        lodTriangles, fineTris, farFineTris, farCoarseTris,
                        fineLit, farFineLit, farCoarseLit, coverageDelta,
                        gpuFineLit, gpuCoarseLit, gpuCoverageDelta, fineMismatch, coarseMismatch,
                    }));
            }
            finally
            {
                scene.Dispose();
            }
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            AssetDatabase.Forget(data.AssetID);
        }
    }

    private static void RenderFrame(Camera camera, RenderTexture target)
    {
        using (var clear = Graphics.GetCommandBuffer("vg-acceptance-clear"))
        {
            clear.SetRenderTarget(target.frameBuffer);
            clear.SetViewport(0, 0, (uint)Width, (uint)Height);
            clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.07f, 0.08f, 0.11f, 1f), 1f);
            Graphics.Submit(clear);
        }
        camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
        Graphics.EndFrameAndWait();
    }

    private (byte[] Pixels, int Triangles) RenderAt(
        VulkanGraphicsDevice device, Camera camera, RenderTexture target, VirtualGeometryRenderer renderer,
        in Float3 cameraPosition, float pixelError)
    {
        camera.Transform.Position = cameraPosition;
        renderer.PixelError = pixelError;
        RenderFrame(camera, target);
        device.WaitIdle();
        return (device.ReadTexture2D(device.Images[target.MainTexture.Handle.Handle], 4), renderer.LastVisibleTriangles);
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
            seen.Add((rgba[i] << 16) | (rgba[i + 1] << 8) | rgba[i + 2]);
        return seen.Count;
    }

    private static float PixelMismatchRatio(byte[] a, byte[] b)
    {
        long differing = 0;
        long pixels = a.Length / 4;
        for (int i = 0; i < a.Length; i += 4)
        {
            int delta = Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            if (delta > 12) differing++;
        }
        return differing / (float)pixels;
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        // Delegate to the shared contract so wrapped "backend unavailable" messages are recognized.
        return GraphicsTestAvailability.IsBackendUnavailable(ex);
    }

    // ── PNG helpers (no third-party dependencies; mirrors the RhiContractTests writer) ──

    private static string SaveScreenshotPng(string name, byte[] rgba, int width, int height)
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable("XENGINE_TEST_SCREENSHOT_DIR");
        string dir = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "docs", "screenshots"))
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
        return Path.GetFullPath(path);
    }

    private static byte[] JoinSideBySide(byte[] left, byte[] right)
    {
        int rowBytes = Width * 4;
        byte[] joined = new byte[rowBytes * 2 * Height];
        for (int y = 0; y < Height; y++)
        {
            System.Buffer.BlockCopy(left, y * rowBytes, joined, y * rowBytes * 2, rowBytes);
            System.Buffer.BlockCopy(right, y * rowBytes, joined, y * rowBytes * 2 + rowBytes, rowBytes);
        }
        return joined;
    }

    private static void WritePngChunk(MemoryStream memory, string type, byte[] data)
    {
        byte[] lengthBytes = new byte[4];
        WriteBigEndianInt32(lengthBytes, 0, data.Length);
        memory.Write(lengthBytes, 0, 4);
        foreach (char c in type) memory.WriteByte((byte)c);
        memory.Write(data, 0, data.Length);
        uint crc = Crc32(type, data);
        byte[] crcBytes = new byte[4];
        WriteBigEndianInt32(crcBytes, 0, (int)crc);
        memory.Write(crcBytes, 0, 4);
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset + 0] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(string type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (char c in type)
            crc = CrcTable[(crc ^ c) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
