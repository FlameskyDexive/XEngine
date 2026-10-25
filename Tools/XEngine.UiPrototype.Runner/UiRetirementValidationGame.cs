using System.Collections;
using System.Reflection;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.UiPrototype;

internal sealed class UiRetirementValidationGame(string destination) : Game
{
    private const int Cases = 16, Width = 160, Height = 96;
    private readonly UiQuillPaintCache?[] _caches = new UiQuillPaintCache[Cases];
    private readonly Texture2D?[] _textures = new Texture2D[Cases];
    private readonly RenderTexture?[] _targets = new RenderTexture[Cases];
    private readonly byte[][] _expected = new byte[Cases][];
    private readonly UiMemoryBudget _budget = new(Cases * UiQuillPaintCache.EstimateCapacityCharge(128, 256, 1, 8));
    private PaperRenderer? _reference;
    private Canvas? _canvas;
    private int _phase;
    private RetirementSnapshot? _beforeWait, _afterWait;
    private sealed record RetirementSnapshot(string DeviceType, Dictionary<string, int> UnsynchronizedDiagnosticCounts);

    public override void OnGui(Scene? scene, Paper paper)
    {
        try
        {
            if (_phase == 0) { Prepare(); _phase = 1; return; }
            if (_phase == 1) { SubmitAndDisposeBurst(); _phase = 2; return; }
            if (_phase == 2) { Verify(); _phase = 3; return; }
            if (_phase < 6) { _phase++; return; }
            Finish(); Cleanup(); Quit();
        }
        catch (Exception error)
        { Console.Error.WriteLine(error); Environment.ExitCode = 2; Cleanup(); Quit(); }
    }

    private void Prepare()
    {
        Directory.CreateDirectory(destination);
        _reference = new PaperRenderer(); _reference.Initialize(Width, Height);
        _canvas = new Canvas(new AtlasOwner(_reference), new FontAtlasSettings { AtlasSize = 128 });
        for (int i = 0; i < Cases; i++)
        {
            _targets[i] = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            var texture = _textures[i] = new Texture2D(1, 1);
            texture.SetData(new Memory<byte>([(byte)(40 + i * 11), (byte)(220 - i * 7), (byte)(60 + i * 9), 255]), 0, 0, 1, 1);
            _caches[i] = new UiQuillPaintCache(128, 256, 1, 8, _budget);
            _canvas.BeginFrame(Width, Height);
            _canvas.DrawImage(texture, 10 + i % 5, 12, 130, 68);
            _caches[i]!.Capture(_canvas);
            Clear(_targets[i]!); _reference.SetRenderTarget(_targets[i]!.frameBuffer); _canvas.Render();
            Wait(); _expected[i] = Read(_targets[i]!);
            if (_expected[i][(48 * Width + 80) * 4 + 3] == 0 ||
                (_expected[i][(48 * Width + 80) * 4] | _expected[i][(48 * Width + 80) * 4 + 1] | _expected[i][(48 * Width + 80) * 4 + 2]) == 0)
                throw new InvalidOperationException("Reference fixture did not draw a colored image.");
            Clear(_targets[i]!);
        }
        Wait(); // All resource creation, texture upload and reference readback precede the burst.
        Console.WriteLine("N3 retirement: sixteen resources prepared; next frame submits/disposes without explicit waits.");
    }

    private void SubmitAndDisposeBurst()
    {
        for (int i = 0; i < Cases; i++)
        {
            var stats = _caches[i]!.Render(_targets[i]!.frameBuffer, Width, Height);
            if (stats.Draws == 0) throw new InvalidOperationException("Retirement fixture submitted no draw.");
            _caches[i]!.Dispose(); _caches[i] = null;
            if (!_textures[i].IsValid()) throw new InvalidOperationException("Cache disposed its borrowed image.");
            _textures[i]!.Dispose(); _textures[i] = null;
        }
        if (_budget.ReservedBytes != 0) throw new InvalidOperationException("Released caches retained admission charges.");
        _beforeWait = SnapshotRetirement();
        Console.WriteLine("N3 retirement: sixteen draws submitted and cache/image owners disposed; readback deferred to next frame.");
    }

    private void Verify()
    {
        Wait();
        for (int i = 0; i < Cases; i++)
        {
            byte[] actual = Read(_targets[i]!);
            if (!actual.AsSpan().SequenceEqual(_expected[i]))
                throw new InvalidOperationException($"Disposed-resource draw {i} differs from reference.");
            using var picture = new ImageMagick.MagickImage();
            picture.ReadPixels(actual, new ImageMagick.PixelReadSettings(Width, Height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
            picture.Write(Path.Combine(destination, $"case-{i:D2}.png"), ImageMagick.MagickFormat.Png32);
        }
        _afterWait = SnapshotRetirement();
        Console.WriteLine("N3 retirement: sixteen deferred draw/readback comparisons passed; awaiting frame-slot reuse.");
    }

    private void Finish()
    {
        Wait();
        var afterReuse = SnapshotRetirement();
        foreach (var count in afterReuse.UnsynchronizedDiagnosticCounts.Values)
            if (count != 0) throw new InvalidOperationException("Native retirement lists did not drain after frame-slot reuse.");
        File.WriteAllText(Path.Combine(destination, "validation.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            Backend = Graphics.Device!.Backend.ToString(), Cases, PixelsEqual = true, ExplicitWaitsDuringBurst = 0,
            AdmissionBytesAfterDispose = _budget.ReservedBytes, BeforeWait = _beforeWait, AfterWait = _afterWait,
            AfterFrameReuse = afterReuse
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("N3 retirement: native retirement lists drained after frame-slot reuse.");
    }

    // Diagnostic reflection only; fields are backend-owned lists, not a portable fence API.
    private static RetirementSnapshot SnapshotRetirement()
    {
        var device = Graphics.BackendDevice!; var type = device.GetType();
        var fields = new Dictionary<string, int>();
        foreach (string name in new[] { "_frameCmdBuffers", "_frameCmdImages", "_pendingRetire", "_pendingTransientResources" })
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device) is ICollection list)
                fields[name] = list.Count;
        if (type.GetField("_frameTransientResources", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device) is Array frames)
        {
            int count = 0; foreach (var frame in frames) if (frame is ICollection list) count += list.Count;
            fields["_frameTransientResourcesTotal"] = count;
        }
        return new(type.FullName!, fields);
    }
    private static void Clear(RenderTexture target)
    {
        using var command = Graphics.GetCommandBuffer("N3 retirement clear");
        command.SetRenderTarget(target.frameBuffer); command.SetViewport(0, 0, Width, Height); command.DisableScissor();
        command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f)); Graphics.Submit(command);
    }
    private static void Wait()
    { using var command = Graphics.GetCommandBuffer("N3 retirement readback wait"); Graphics.SubmitAndWait(command); }
    private static byte[] Read(RenderTexture target)
    {
        var pixels = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(pixels.AsMemory());
        if (Graphics.Device!.Backend == XEngine.Runtime.RHI.GraphicsBackend.OpenGL)
            for (int y = 0; y < Height / 2; y++)
                for (int x = 0; x < Width * 4; x++)
                { int a = y * Width * 4 + x, b = (Height - y - 1) * Width * 4 + x; (pixels[a], pixels[b]) = (pixels[b], pixels[a]); }
        return pixels;
    }
    private void Cleanup()
    {
        for (int i = 0; i < Cases; i++) { _caches[i]?.Dispose(); _textures[i]?.Dispose(); _targets[i]?.Dispose(); }
        _canvas?.Dispose(); _canvas = null; _reference?.Dispose(); _reference = null;
    }
    private sealed class AtlasOwner(PaperRenderer renderer) : ICanvasRenderer
    {
        private readonly List<IDisposable> _textures = new();
        public object CreateTexture(uint width, uint height)
        { var texture = renderer.CreateTexture(width, height); _textures.Add((IDisposable)texture); return texture; }
        public Int2 GetTextureSize(object texture) => renderer.GetTextureSize(texture);
        public void SetTextureData(object texture, IntRect bounds, byte[] data) => renderer.SetTextureData(texture, bounds, data);
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) => renderer.RenderCalls(canvas, calls);
        public void Dispose() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); }
    }
}
