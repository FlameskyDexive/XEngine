// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Pixel-level acceptance for the realtime GI screen-space chain (Surface Cache GI, SSGI tier)
/// on a real Vulkan device: compiles the SSGI shader's Gather and Composite passes, renders a
/// deterministic two-half scene (uniform lit wall / sky) through both passes, and asserts the
/// bounce math (scene + irradiance * albedo * (1-metallic)) as well as the sky no-op. Saves
/// inspectable PNGs under docs/screenshots/. Skips when no Vulkan device is available.
/// </summary>
public sealed class RealtimeGiRenderAcceptanceTests : IDisposable
{
    private const int Width = 256;
    private const int Height = 128;

    private readonly GraphicsBackend? _previousBackend;

    public RealtimeGiRenderAcceptanceTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
        }
        catch (Exception ex) when (IsExpectedGpuUnavailable(ex))
        {
            GraphicsBackendSelection.Preferred = _previousBackend;
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void Optional_RealtimeGi_SsgiChain_RendersBounce_OrSkip()
    {
        var device = Graphics.Device as Backends.Vulkan.VulkanGraphicsDevice;
        if (device == null) return; // Vulkan unavailable on this host — skip

        Shader shader = Shader.LoadDefault(DefaultShader.SSGI);
        Rendering.Shaders.ShaderPass gatherPass = shader.GetPass("Gather");
        Rendering.Shaders.ShaderPass compositePass = shader.GetPass("Composite");

        using var gatherMaterial = new Material(shader);
        gatherMaterial.SetFloat("_NumSteps", 32f);
        gatherMaterial.SetFloat("_Radius", 8f);
        gatherMaterial.SetInt("_GiRays", 2);
        gatherMaterial.SetVector("_NoiseScale", new Float2(1f, 1f));
        gatherMaterial.SetVector("_JitterOffset", new Float2(0f, 0f));
        Assert.True(gatherPass.TryGetMaterialVariant(gatherMaterial, out RHI.Shaders.ShaderVariant? gatherVariant)
                    && gatherVariant != null, "SSGI Gather must compile for Vulkan");

        using var compositeMaterial = new Material(shader);
        compositeMaterial.SetFloat("_Intensity", 1f);
        compositeMaterial.SetInt("_GiDebugView", 0);
        Assert.True(compositePass.TryGetMaterialVariant(compositeMaterial, out RHI.Shaders.ShaderVariant? compositeVariant)
                    && compositeVariant != null, "SSGI Composite must compile for Vulkan");

        // ── Deterministic inputs: left half = uniform lit wall (depth 0.55, -Z view normal),
        //    right half = sky (depth 1). A 1x1 mid-grey noise tile makes every ray sample the
        //    same Xi, and the uniform-depth wall makes every left-half ray an instant hit that
        //    samples its own half's colour, so irradiance == wall colour exactly.
        byte[] wall = [40, 80, 120, 255];
        byte[] sky = [10, 20, 30, 255];
        byte[] depthWall = [(byte)(0.55f * 255), 0, 0, 255];
        byte[] depthSky = [255, 0, 0, 255];
        byte[] normalMinusZ = [128, 128, 0, 255]; // view normal (0,0,-1) encoded *0.5+0.5
        byte[] motionZeroMetal = [128, 128, 0, 0]; // .a metallic = 0
        byte[] albedoWhite = [255, 255, 255, 255]; // albedo 1, AO 1

        using var sceneColor = MakeHalfTexture(wall, sky);
        using var depth = MakeHalfTexture(depthWall, depthSky);
        using var normals = MakeHalfTexture(normalMinusZ, normalMinusZ);
        using var motion = MakeHalfTexture(motionZeroMetal, motionZeroMetal);
        using var albedo = MakeHalfTexture(albedoWhite, albedoWhite);
        using var noise = MakeSolidTexture([128, 128, 128, 255]);
        using var irradiance = MakeRenderTarget();
        using var finalColor = MakeRenderTarget();
        UploadTextures(sceneColor, depth, normals, motion, albedo, noise, irradiance, finalColor);

        var gatherBuffer = MakeFramebuffer(irradiance);
        var finalBuffer = MakeFramebuffer(finalColor);
        using var vao = MakeFullscreenTriangleVao();

        Float4x4 view = Float4x4.CreateLookAt(new Float3(0, 0, -3), new Float3(0, 0, 0), new Float3(0, 1, 0));
        float aspect = Width / (float)Height;
        Float4x4 projection = Float4x4.CreatePerspectiveFov(0.9f, aspect, 0.1f, 100f);
        UploadGlobalUniforms(view, projection, new Float3(0, 0, -3));

        using (CommandBuffer cmd = Graphics.GetCommandBuffer("gi-acceptance-gather"))
        {
            cmd.SetRenderTarget(gatherBuffer);
            cmd.SetViewport(0, 0, Width, Height);
            cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0f, 0f, 0f, 0f), 1f);
            cmd.SetShader(gatherVariant!);
            cmd.SetRasterState(gatherPass.State);
            cmd.SetMaterialProperties(gatherMaterial);
            cmd.SetGlobalTexture("_CameraDepthTexture", depth);
            cmd.SetGlobalTexture("_CameraNormalsTexture", normals);
            cmd.SetTexture("_MainTex", sceneColor);
            cmd.SetTexture("_Noise", noise);
            cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
            Graphics.Submit(cmd);
        }
        Graphics.EndFrameAndWait();
        byte[] irradiancePixels = device.ReadTexture2D(device.Images[irradiance.Handle.Handle], 4);

        using (CommandBuffer cmd = Graphics.GetCommandBuffer("gi-acceptance-composite"))
        {
            cmd.SetRenderTarget(finalBuffer);
            cmd.SetViewport(0, 0, Width, Height);
            cmd.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0f, 0f, 0f, 0f), 1f);
            cmd.SetShader(compositeVariant!);
            cmd.SetRasterState(compositePass.State);
            cmd.SetMaterialProperties(compositeMaterial);
            cmd.SetGlobalTexture("_CameraDepthTexture", depth);
            cmd.SetGlobalTexture("_CameraMotionVectorsTexture", motion);
            cmd.SetGlobalTexture("_GiAlbedoTexture", albedo);
            cmd.SetTexture("_MainTex", sceneColor);
            cmd.SetTexture("_GiTex", irradiance);
            cmd.DrawArrays(vao, Topology.Triangles, 0, 3);
            Graphics.Submit(cmd);
        }
        Graphics.EndFrameAndWait();
        byte[] finalPixels = device.ReadTexture2D(device.Images[finalColor.Handle.Handle], 4);

        // Save before asserting so failures leave inspectable output.
        SaveScreenshotPng("realtime-gi-ssgi-acceptance-final", finalPixels, Width, Height);
        SaveScreenshotPng("realtime-gi-ssgi-acceptance-irradiance", irradiancePixels, Width, Height);
        SaveScreenshotPng(
            "realtime-gi-ssgi-acceptance-triplet",
            JoinSideBySide(JoinSideBySide(ReadPixels(device, sceneColor), irradiancePixels), finalPixels),
            Width * 3,
            Height);

        // ── Acceptance ──
        // Left half: every ray self-hits the uniform wall, so irradiance ~= wall colour and the
        // composite adds it back modulated by white albedo / zero metallic: roughly 2x wall.
        int left = PixelIndex(Width / 4, Height / 2);
        Assert.InRange(irradiancePixels[left], (byte)30, (byte)50);
        Assert.InRange(irradiancePixels[left + 1], (byte)70, (byte)90);
        Assert.InRange(irradiancePixels[left + 2], (byte)110, (byte)130);
        Assert.InRange(finalPixels[left], (byte)75, (byte)105);
        Assert.InRange(finalPixels[left + 1], (byte)150, (byte)185);
        Assert.InRange(finalPixels[left + 2], (byte)230, (byte)255);

        // Right half: sky pixels early-out (depth >= 1) — no irradiance, no bounce.
        int right = PixelIndex(3 * Width / 4, Height / 2);
        Assert.InRange(irradiancePixels[right], (byte)0, (byte)8);
        Assert.InRange(finalPixels[right], (byte)5, (byte)15);
        Assert.InRange(finalPixels[right + 1], (byte)15, (byte)25);
        Assert.InRange(finalPixels[right + 2], (byte)25, (byte)35);
    }

    private static int PixelIndex(int x, int y) => (y * Width + x) * 4;

    private static byte[] ReadPixels(Backends.Vulkan.VulkanGraphicsDevice device, Texture2D texture)
        => device.ReadTexture2D(device.Images[texture.Handle.Handle], 4);

    private static void UploadGlobalUniforms(in Float4x4 view, in Float4x4 projection, in Float3 cameraPosition)
    {
        Float4x4 viewProj = projection * view;
        GlobalUniforms.SetMatrixV(view);
        GlobalUniforms.SetMatrixIV(view.Invert());
        GlobalUniforms.SetMatrixP(projection);
        GlobalUniforms.SetMatrixVP(viewProj);
        GlobalUniforms.SetMatrixIP(projection.Invert());
        GlobalUniforms.SetMatrixIVP(viewProj.Invert());
        GlobalUniforms.SetWorldSpaceCameraPos(cameraPosition);
        GlobalUniforms.SetProjectionParams(new Float4(1f, 0.1f, 100f, 1f / 100f));
        GlobalUniforms.SetScreenParams(new Float4(Width, Height, 1f + 1f / Width, 1f + 1f / Height));
        GlobalUniforms.Upload();
    }

    private static Texture2D MakeHalfTexture(byte[] left, byte[] right)
    {
        byte[] pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            byte[] source = x < Width / 2 ? left : right;
            int i = (y * Width + x) * 4;
            pixels[i] = source[0];
            pixels[i + 1] = source[1];
            pixels[i + 2] = source[2];
            pixels[i + 3] = source[3];
        }
        var texture = new Texture2D();
        using (CommandBuffer cmd = Graphics.GetCommandBuffer("gi-tex-half"))
        {
            cmd.EncodeCreateTexture(texture.Handle);
            cmd.EncodeAllocateTexture2D(texture.Handle, 0, (uint)Width, (uint)Height, 0, pixels);
            Graphics.Submit(cmd);
        }
        return texture;
    }

    private static Texture2D MakeSolidTexture(byte[] color)
    {
        var texture = new Texture2D();
        using (CommandBuffer cmd = Graphics.GetCommandBuffer("gi-tex-solid"))
        {
            cmd.EncodeCreateTexture(texture.Handle);
            cmd.EncodeAllocateTexture2D(texture.Handle, 0, 1, 1, 0, color);
            Graphics.Submit(cmd);
        }
        return texture;
    }

    private static Texture2D MakeRenderTarget()
    {
        var texture = new Texture2D();
        using (CommandBuffer cmd = Graphics.GetCommandBuffer("gi-tex-rt"))
        {
            cmd.EncodeCreateTexture(texture.Handle);
            cmd.EncodeAllocateTexture2D(texture.Handle, 0, (uint)Width, (uint)Height, 0, ReadOnlySpan<byte>.Empty);
            Graphics.Submit(cmd);
        }
        return texture;
    }

    private static void UploadTextures(params Texture2D[] textures)
    {
        // All texture uploads already submitted inside their factory command buffers; this hook
        // exists to keep the upload order explicit and cheap to extend.
    }

    private static GraphicsFrameBuffer MakeFramebuffer(Texture2D color)
    {
        var depth = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Depth32f);
        unsafe
        {
            Graphics.TexImage2D(depth, 0, Width, Height, 0, null);
        }
        return Graphics.CreateFramebuffer(
        [
            new GraphicsFrameBuffer.Attachment { Texture = color.Handle },
            new GraphicsFrameBuffer.Attachment { Texture = depth, IsDepth = true },
        ], (uint)Width, (uint)Height);
    }

    /// <summary>Covers the viewport with one oversized triangle (same vertex layout the
    /// standard lighting contract uses: pos3 + uv0 + uv2 + normal3 + tangent4).</summary>
    private static GraphicsVertexArray MakeFullscreenTriangleVao()
    {
        float[] vertices =
        [
            -1f, -1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 1f,
            3f, -1f, 0f, 2f, 0f, 2f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 1f,
            -1f, 3f, 0f, 0f, 2f, 0f, 2f, 0f, 0f, 1f, 1f, 0f, 0f, 1f,
        ];
        ReadOnlySpan<byte> vertexBytes;
        unsafe
        {
            fixed (float* ptr = vertices)
            {
                vertexBytes = new ReadOnlySpan<byte>(ptr, vertices.Length * sizeof(float));
            }
        }
        var vertexBuffer = new GraphicsBuffer(BufferType.VertexBuffer, vertexBytes, true);
        using (CommandBuffer create = Graphics.GetCommandBuffer("gi-acceptance-vao"))
        {
            create.EncodeCreateBuffer(vertexBuffer, true, vertexBytes);
            Graphics.Submit(create);
        }
        return Graphics.CreateVertexArray(new VertexFormat([
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3),
            new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2),
            new(VertexFormat.VertexSemantic.TexCoord1, VertexFormat.VertexType.Float, 2),
            new(VertexFormat.VertexSemantic.Normal, VertexFormat.VertexType.Float, 3),
            new(VertexFormat.VertexSemantic.Tangent, VertexFormat.VertexType.Float, 4),
        ]), vertexBuffer, null);
    }

    private static byte[] JoinSideBySide(byte[] left, byte[] right)
    {
        // left may already be a multi-image strip; right is always one image wide.
        int leftRowBytes = left.Length / Height;
        byte[] joined = new byte[left.Length + right.Length];
        int rightRowBytes = Width * 4;
        for (int y = 0; y < Height; y++)
        {
            int dstRow = y * (leftRowBytes + rightRowBytes);
            Array.Copy(left, y * leftRowBytes, joined, dstRow, leftRowBytes);
            Array.Copy(right, y * rightRowBytes, joined, dstRow + leftRowBytes, rightRowBytes);
        }
        return joined;
    }

    private static void SaveScreenshotPng(string name, byte[] rgba, int width, int height)
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
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        byte[] idat;
        using (var deflate = new MemoryStream())
        {
            deflate.WriteByte(0x78);
            deflate.WriteByte(0x9C);
            using (var compressor = new System.IO.Compression.DeflateStream(deflate, System.IO.Compression.CompressionLevel.Optimal, true))
            {
                compressor.Write(raw, 0, raw.Length);
            }
            uint adler = Adler32(raw);
            byte[] adlerBytes = new byte[4];
            WriteBigEndianInt32(adlerBytes, 0, (int)adler);
            deflate.Write(adlerBytes, 0, 4);
            idat = deflate.ToArray();
        }
        WritePngChunk(memory, "IDAT", idat);
        WritePngChunk(memory, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, memory.ToArray());
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset + 0] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        byte[] lengthBytes = new byte[4];
        WriteBigEndianInt32(lengthBytes, 0, data.Length);
        stream.Write(lengthBytes, 0, 4);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        if (data.Length > 0)
            stream.Write(data, 0, data.Length);
        uint crc = Crc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteBigEndianInt32(crcBytes, 0, (int)crc);
        stream.Write(crcBytes, 0, 4);
    }

    private static readonly uint[] s_pngCrcTable = BuildPngCrcTable();
    private static uint[] BuildPngCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1u) != 0u ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++) crc = (crc >> 8) ^ s_pngCrcTable[(crc ^ type[i]) & 0xFF];
        for (int i = 0; i < data.Length; i++) crc = (crc >> 8) ^ s_pngCrcTable[(crc ^ data[i]) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Adler32(byte[] data)
    {
        const uint modulus = 65521u;
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }

    private static bool IsExpectedGpuUnavailable(Exception ex) =>
        ex.Message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("adapter", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("device", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("driver", StringComparison.OrdinalIgnoreCase);
}
