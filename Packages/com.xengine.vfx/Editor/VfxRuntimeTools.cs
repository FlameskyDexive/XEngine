using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

using ModelContextProtocol.Server;
using XEngine.Ai.Server;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.VFX;
using XEngine.Vector;

namespace XEngine.VFX.Editor;

/// <summary>
/// In-process MCP tools for live Visual Effect Graph playback. Registered when
/// <c>com.xengine.vfx</c> Editor is loaded; <see cref="McpToolAssemblyScan"/> picks this assembly up.
/// </summary>
[McpServerToolType]
public static class VfxRuntimeTools
{
    [McpServerTool(Name = "runtime_vfx_state", UseStructuredContent = true)]
    [Description("Live VisualEffect snapshot: scene name, effect count, playing/capacity/spawn/burst per instance.")]
    public static Task<VfxLiveStateResult> State(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(CollectState);

    [McpServerTool(Name = "runtime_vfx_preview", UseStructuredContent = true)]
        [Description("Play a live VisualEffect and write preview PNGs (GPU RT when compute is available, otherwise CPU stand-in; optional GameView capture). identifier is a GameObject Guid; omit to use the current selection. frameCount captures multiple preview frames.")]
    public static Task<VfxLivePreviewResult> Preview(
        MainThreadDispatcher dispatcher,
        [Description("Optional GameObject Identifier (Guid). When omitted, uses the active selection or the first VisualEffect.")] string? identifier = null,
        [Description("Requested playback window in seconds (recorded; GPU frames still need runtime_screenshot over time).")] float seconds = 1f,
        [Description("Number of CPU preview PNGs to write (1-8).")] int frameCount = 1)
        => dispatcher.InvokeOnMainThreadAsync(() => CapturePreview(identifier, seconds, frameCount));

    public static VfxLiveStateResult CollectState()
    {
        Scene? scene = Scene.Current;
        if (scene == null)
            return new VfxLiveStateResult("(none)", 0, Array.Empty<VfxLiveEffect>(), null);

        var effects = new List<VfxLiveEffect>();
        foreach (GameObject go in scene.AllObjects)
        {
            VisualEffect? vfx = go.GetComponent<VisualEffect>();
            if (vfx == null)
                continue;
            effects.Add(new VfxLiveEffect(
                go.Identifier.ToString(),
                go.Name,
                vfx.IsPlaying,
                vfx.Capacity,
                vfx.SpawnRate,
                vfx.BurstCount,
                vfx.UsesGpuParticles));
        }

        return new VfxLiveStateResult(scene.Name ?? "(none)", effects.Count, effects, null);
    }

    internal static VfxLivePreviewResult CapturePreview(string? identifier, float seconds, int frameCount)
    {
        VisualEffect? vfx = FindVisualEffect(identifier, out string? err, out string? goName);
        if (vfx == null)
            return new VfxLivePreviewResult(false, goName, false, 0, 0f, 0f, seconds, Array.Empty<string>(), err);

        vfx.ApplyGraph();
        vfx.Play();

        int frames = Math.Clamp(frameCount, 1, 8);
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".xengine", "screenshots");
        Directory.CreateDirectory(dir);

        var settings = new VfxCompiledSettings
        {
            Capacity = vfx.Capacity,
            SpawnRate = vfx.SpawnRate,
            BurstCount = vfx.BurstCount,
            Lifetime = vfx.Lifetime,
            StartSpeed = vfx.StartSpeed,
            StartSize = vfx.StartSize,
            Gravity = vfx.Gravity,
            StartColor = vfx.StartColor,
        };

        var paths = new List<string>(frames + 1);
        const int w = 256;
        const int h = 144;
        VfxGraphAsset? graph = vfx.Graph.Res;
        for (int i = 0; i < frames; i++)
        {
            var rgba = new byte[w * h * 4];
            VfxGraphWindow.TryFillPreviewRgba(rgba, w, h, settings, graph, out _);
            string path = Path.Combine(dir, $"vfx-preview-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{i}.png");
            File.WriteAllBytes(path, EncodePng(rgba, w, h));
            paths.Add(path);
        }

        var panel = GameViewPanel.ActiveInstance;
        var capture = panel?.TryCapture();
        if (capture is { } gpu)
        {
            (byte[] rgba, int width, int height) = gpu;
            FlipVertical(rgba, width, height);
            string path = Path.Combine(dir, $"vfx-gameview-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.png");
            File.WriteAllBytes(path, EncodePng(rgba, width, height));
            paths.Add(path);
        }

        return new VfxLivePreviewResult(
            true, goName, vfx.IsPlaying, vfx.Capacity, vfx.SpawnRate, vfx.BurstCount,
            seconds, paths, null);
    }

    static VisualEffect? FindVisualEffect(string? identifier, out string? error, out string? goName)
    {
        goName = null;
        GameObject? go = null;
        if (!string.IsNullOrEmpty(identifier))
        {
            if (!Guid.TryParse(identifier, out Guid guid))
            {
                error = $"Invalid identifier: {identifier}";
                return null;
            }

            go = Scene.Current?.FindObjectByIdentifier<GameObject>(guid);
            if (go is null)
            {
                error = $"GameObject '{identifier}' not found in the current scene.";
                return null;
            }
        }
        else if (Selection.ActiveObject is GameObject selected)
            go = selected;
        else if (Scene.Current != null)
        {
            foreach (GameObject candidate in Scene.Current.AllObjects)
            {
                if (candidate.GetComponent<VisualEffect>() != null)
                {
                    go = candidate;
                    break;
                }
            }
        }

        if (go is null)
        {
            error = "No GameObject: pass identifier, select one, or add a VisualEffect to the scene.";
            return null;
        }

        goName = go.Name;
        VisualEffect? vfx = go.GetComponent<VisualEffect>();
        if (vfx == null)
        {
            error = $"GameObject '{go.Name}' has no VisualEffect.";
            return null;
        }

        error = null;
        return vfx;
    }

    static void FlipVertical(byte[] rgba, int width, int height)
    {
        int stride = width * 4;
        var row = new byte[stride];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            Buffer.BlockCopy(rgba, top, row, 0, stride);
            Buffer.BlockCopy(rgba, bottom, rgba, top, stride);
            Buffer.BlockCopy(row, 0, rgba, bottom, stride);
        }
    }

    internal static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);
        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        byte[] adler = new byte[4];
        WriteInt(adler, 0, (int)Adler32(raw));
        deflate.Write(adler, 0, 4);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        return memory.ToArray();
    }

    static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        crc ^= 0xFFFFFFFF;
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)crc);
        stream.Write(crcBytes);
    }

    static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    public sealed record VfxLiveEffect(
        string Identifier, string Name, bool Playing, int Capacity, float SpawnRate, float BurstCount, bool UsesGpu);

    public sealed record VfxLiveStateResult(
        string SceneName, int EffectCount, IReadOnlyList<VfxLiveEffect> Effects, string? Error);

    public sealed record VfxLivePreviewResult(
        bool Succeeded, string? ObjectName, bool Playing, int Capacity, float SpawnRate, float BurstCount,
        float Seconds, IReadOnlyList<string> ScreenshotPaths, string? Error);
}
