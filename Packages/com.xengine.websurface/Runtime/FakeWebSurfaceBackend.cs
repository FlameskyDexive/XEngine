using System;

namespace XEngine.WebSurface;

/// <summary>
/// Deterministic in-memory backend for B1 tests and tooling. Produces "four-color corner mark"
/// frames — RGBA8 with distinct colors in the four quadrants plus a per-frame corner stamp — so
/// present-path tests can verify orientation, transparency and stride handling without a GPU.
/// Tests inject faults (out-of-order commits, stale generations, dropped frames) through the
/// same public surface the real backends will use.
/// </summary>
public sealed class FakeWebSurfaceBackend : IWebSurfaceBackend
{
    private IWebSurfaceFrameSink? _sink;
    private WebSurfaceAsset? _asset;
    private bool _loaded;
    private ulong _nextFrame;
    private int _width;
    private int _height;
    private bool _transparent;

    public WebSurfaceCapabilities Capabilities { get; } =
        WebSurfaceCapabilities.CpuBaseline("fake");

    public event Action<string>? Failed;
    public event Action? Ready;

    public bool IsLoaded => _loaded;

    public void Load(WebSurfaceAsset asset, IWebSurfaceFrameSink sink)
    {
        _asset = asset;
        _sink = sink;
        _width = asset.Quality.Width;
        _height = asset.Quality.Height;
        _transparent = asset.TransparentBackground;
        _loaded = true;
    }

    /// <summary>Produces and commits one frame at the current size. Returns the frame number.</summary>
    public ulong ProduceFrame()
    {
        if (!_loaded || _sink is null)
            throw new InvalidOperationException("Load must run before producing frames.");

        int stride = _width * 4;
        var frame = _sink.BeginFrame(_nextFrame, _width, _height, stride,
            _transparent ? WebSurfaceAlphaMode.Straight : WebSurfaceAlphaMode.Opaque)
            ?? throw new InvalidOperationException("A production request is already in flight.");

        PaintFourColorCorners(frame.Pixels, _width, _height, stride, _transparent, _nextFrame);

        if (!_sink.Commit(frame))
            frame.Dispose();
        return _nextFrame++;
    }

    /// <summary>
    /// Begins a frame at an arbitrary (stale) generation supplied by the test, then tries to
    /// commit it. Used to pin the pool's stale-generation drop path.
    /// </summary>
    public bool CommitStaleGenerationFrame(int generation, int width, int height)
    {
        if (_sink is null)
            return false;
        // The sink API always stamps the current generation; the stale path is exercised by
        // resizing between Begin and Commit (simulating a resize racing the producer).
        var frame = _sink.BeginFrame(_nextFrame, width, height, width * 4, WebSurfaceAlphaMode.Opaque);
        if (frame is null)
            return false;
        bool committed = _sink.Commit(frame);
        if (!committed)
            frame.Dispose();
        return committed;
    }

    public void SimulateResizeBetweenBeginAndCommit(out WebSurfaceFrame frame, Action resize)
    {
        if (_sink is null)
            throw new InvalidOperationException("Load must run first.");
        frame = _sink.BeginFrame(_nextFrame, _width, _height, _width * 4, WebSurfaceAlphaMode.Opaque)
            ?? throw new InvalidOperationException("A production request is already in flight.");
        resize();
        bool committed = _sink.Commit(frame);
        if (!committed)
            frame.Dispose();
    }

    public void SimulateFailure(string message) => Failed?.Invoke(message);

    /// <summary>RGBA8 four-color corner marks. TL=red, TR=green, BL=blue, BR=yellow; a stamp
    /// byte in the top-left pixel encodes the low bits of the frame number. Transparent mode
    /// clears everything outside the center band to alpha 0.</summary>
    public static void PaintFourColorCorners(byte[] pixels, int width, int height, int stride, bool transparent, ulong frameNumber)
    {
        int halfW = width / 2;
        int halfH = height / 2;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int o = rowStart + x * 4;
                byte r, g, b;
                if (x < halfW && y < halfH) { r = 255; g = 0; b = 0; }
                else if (x >= halfW && y < halfH) { r = 0; g = 255; b = 0; }
                else if (x < halfW && y >= halfH) { r = 0; g = 0; b = 255; }
                else { r = 255; g = 255; b = 0; }

                byte a = (byte)(transparent ? 255 : 255);
                pixels[o] = r;
                pixels[o + 1] = g;
                pixels[o + 2] = b;
                pixels[o + 3] = a;
            }
        }

        // Frame stamp: low 8 bits of the frame number in the (0,0) pixel's red channel.
        pixels[0] = (byte)(frameNumber & 0xFF);
    }

    public void Pause() { }
    public void Resume() { }
    public void Reload() => _loaded = _loaded; // reload re-navigates in real backends; fake keeps state

    public bool RequestFrame()
    {
        if (!_loaded)
            return false;
        ProduceFrame();
        return true;
    }

    public void Dispose() => _loaded = false;
}
