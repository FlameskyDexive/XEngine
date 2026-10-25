using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.RHI;
using XEngine.Vector;
using System.Runtime.CompilerServices;

namespace XEngine.Editor.UI.Rendering;

public readonly record struct UiPaintSubmission(int Draws, long VertexBytes, long IndexBytes, long BrushBytes);

/// <summary>One outstanding encoding owned by a cache. Complete only after the host submits
/// or discards its entire command buffer. This is not a GPU completion fence.</summary>
public readonly record struct UiPaintEncoding
{
    internal UiPaintEncoding(object owner, ulong serial, UiPaintSubmission stats)
    { Owner = owner; Serial = serial; Stats = stats; }
    internal object? Owner { get; }
    internal ulong Serial { get; }
    public UiPaintSubmission Stats { get; }
    public bool IsValid => Owner != null;
}

/// <summary>
/// Bounded retained Quill paint island. Capture is called by a dirty-paint owner, not every
/// frame. Render reuses geometry and text atlas references, uploading only dirty pages.
/// Borrowed canvas textures must outlive the cache or be replaced by a new capture.
/// This backend does not decide node invalidation or manage an island's text layouts.
/// </summary>
public sealed class UiQuillPaintCache : IDisposable
{
    private readonly object _device = Graphics.Device ?? throw new InvalidOperationException("Initialize graphics before creating paint caches.");
    private UiRhiUploadBuffer<Vertex> _vertices = null!;
    private UiRhiUploadBuffer<uint> _indices = null!;
    private readonly UiRhiUploadBuffer<UiBrushRecord>?[] _brushes = [];
    private readonly UiUploadTicket[] _brushTickets = [];
    private readonly UiMemoryLease? _budgetLease;
    private GraphicsVertexArray _vao = null!;
    private Texture2D _white = null!;
    private readonly ShaderVariant _shader;
    private UiBrushRecord[] _brushScratch = [];
    private DrawCall[] _calls = [];
    private int _callCount, _pageCount;
    private float _dpi;
    private bool _disposed;
    private bool _encodingOpen;
    private ulong _encodingSerial;
    private UiUploadTicket _vertexTicket, _indexTicket;
    private Canvas? _textSource;
    private int _fontAtlasRevision;
    /// <summary>The owner must invalidate its node and rebuild text geometry before rendering again.</summary>
    public bool NeedsRecapture => _textSource != null && _textSource.FontAtlasRevision != _fontAtlasRevision;
    public bool HasPendingEncoding => _encodingOpen;

    public UiQuillPaintCache(int vertexCapacity, int indexCapacity, int brushPages, int drawCapacity, UiMemoryBudget? budget = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(vertexCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(indexCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(brushPages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(drawCapacity, 1);
        _budgetLease = budget?.Reserve(EstimateCapacityCharge(vertexCapacity, indexCapacity, brushPages, drawCapacity));
        try
        {
            _brushScratch = new UiBrushRecord[checked(brushPages * Canvas.BrushRecordsPerPage)];
            _calls = new DrawCall[drawCapacity]; _brushes = new UiRhiUploadBuffer<UiBrushRecord>[brushPages];
            _brushTickets = new UiUploadTicket[brushPages];
            var shader = Shader.LoadDefault(DefaultShader.UI);
            if (shader == null || !shader.GetPass(0).TryGetVariant(null, out _shader))
                throw new InvalidOperationException("Default UI shader is unavailable.");
            _vertices = new(BufferType.VertexBuffer, vertexCapacity, 64);
            _indices = new(BufferType.ElementsBuffer, indexCapacity, 96);
            _vao = Graphics.CreateVertexArray(new VertexFormat([
                new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 2, 0),
                new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2, 0),
                new(VertexFormat.VertexSemantic.Color, VertexFormat.VertexType.UnsignedByte, 4, 0, true),
                new(VertexFormat.VertexSemantic.TexCoord1, VertexFormat.VertexType.Float, 1, 0)]), _vertices.Buffer, _indices.Buffer);
            _white = new Texture2D(1, 1);
            _white.SetData(new Memory<byte>([255, 255, 255, 255]), 0, 0, 1, 1);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Admission charge for maximum scratch/mirror/GPU-buffer payload plus metadata allowance.
    /// Excludes canvas/text/font/atlas ownership, driver alignment/staging and shader resources.</summary>
    public static long EstimateCapacityCharge(int vertices, int indices, int brushPages, int draws)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(vertices, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(indices, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(brushPages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(draws, 1);
        return checked(4096L + (long)vertices * Unsafe.SizeOf<Vertex>() * 2 + (long)indices * sizeof(uint) * 2
            + (long)brushPages * Canvas.BrushRecordsPerPage * UiBrushRecord.SizeInBytes * 3
            + (long)draws * Unsafe.SizeOf<DrawCall>()
            + (((long)vertices + 63) / 64 + ((long)indices + 95) / 96 + (long)brushPages * Canvas.BrushRecordsPerPage) * 64
            + (long)brushPages * 512);
    }

    public void Capture(Canvas canvas)
    {
        CheckAlive(); ArgumentNullException.ThrowIfNull(canvas);
        RequireNoEncoding();
        int vertices = canvas.VertexCount, indices = canvas.IndexCount, records = canvas.BrushRecordCount;
        int pages = (records + Canvas.BrushRecordsPerPage - 1) / Canvas.BrushRecordsPerPage;
        var calls = canvas.DrawCalls;
        bool hasText = false;
        if (vertices > _vertices.Capacity || indices > _indices.Capacity || records > _brushScratch.Length || calls.Count > _calls.Length)
            throw new InvalidOperationException("Paint island exceeds its configured capacity; split or recreate it.");
        long usedIndices = 0;
        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            if (call.RenderLayer != null)
                throw new NotSupportedException("A paint island cannot capture nested render-layer markers.");
            if (call.FontAtlas != null)
            {
                hasText = true;
                if (!canvas.IsFontAtlasStableForFrame || !ReferenceEquals(call.FontAtlas, canvas.FontAtlasTexture))
                    throw new InvalidOperationException("Font atlas changed during paint; rebuild the canvas before capture.");
            }
            if (call.Shader != null || call.ShaderUniforms != null)
                throw new NotSupportedException("Custom shader paint needs an explicit material adapter.");
            if (call.ElementCount < 0 || call.BrushPage < 0 || call.BrushPage >= pages)
                throw new ArgumentException("Invalid Quill draw range.", nameof(canvas));
            usedIndices += call.ElementCount;
        }
        if (usedIndices != indices) throw new ArgumentException("Quill index/draw counts differ.", nameof(canvas));
        // Allocate missing pages before changing live geometry. The maximum page count is fixed.
        for (int page = 0; page < pages; page++)
            _brushes[page] ??= new(BufferType.UniformBuffer, Canvas.BrushRecordsPerPage, 1);
        // Borrow only for the synchronous copy into the owned mirror, never across submission.
        canvas.CopyBrushRecordsTo(_brushScratch);
        _vertices.Write(0, canvas.GetVertexSpan());
        _indices.Write(0, canvas.GetIndexSpan());
        for (int page = 0; page < pages; page++)
        {
            int offset = page * Canvas.BrushRecordsPerPage;
            _brushes[page]!.Write(0, _brushScratch.AsSpan(offset, Math.Min(records - offset, Canvas.BrushRecordsPerPage)));
        }
        for (int i = 0; i < calls.Count; i++) _calls[i] = calls[i];
        if (_callCount > calls.Count) Array.Clear(_calls, calls.Count, _callCount - calls.Count);
        _callCount = calls.Count; _pageCount = pages; _dpi = canvas.FramebufferScale;
        _textSource = hasText ? canvas : null; _fontAtlasRevision = canvas.FontAtlasRevision;
    }

    public UiPaintSubmission Render(GraphicsFrameBuffer? target, int width, int height, bool visible = true,
        bool captureGpuTiming = false, PresentationSurfaceId presentationSurface = default)
    {
        CheckAlive();
        // No command rental, upload, clear, or submission for a hidden/empty island.
        if (!visible || _callCount == 0 || width <= 0 || height <= 0) return default;
        if (NeedsRecapture) throw new InvalidOperationException("Retained text atlas changed; repaint the owning node before rendering.");
        using var command = Graphics.GetCommandBuffer("N3 retained Quill paint");
        command.CaptureGpuTiming = captureGpuTiming;
        if (presentationSurface.IsValid) command.SetPresentationSurface(presentationSurface);
        command.SetRenderTarget(target); command.SetViewport(0, 0, (uint)width, (uint)height); command.DisableScissor();
        UiPaintEncoding encoding = default;
        bool succeeded = false;
        try
        {
            encoding = Encode(command, width, height);
            Graphics.Submit(command); succeeded = true;
            return encoding.Stats;
        }
        finally { if (encoding.IsValid) CompleteEncoding(encoding, succeeded); }
    }

    /// <summary>Append uploads and draws to the host's command stream without changing its
    /// target, viewport or hardware scissor and without submitting. Geometry/brush clips
    /// must already be captured in the correct canvas coordinates and DPI. UI shader,
    /// textures, raster state and bindings are changed; the host restores its own state
    /// before later foreign draws. On failure discard the whole command buffer.
    /// Do not mutate borrowed resources between encoding and submission.</summary>
    public UiPaintEncoding Encode(CommandBuffer command, int width, int height, bool visible = true)
    {
        CheckAlive(); ArgumentNullException.ThrowIfNull(command);
        if (!visible || _callCount == 0 || width <= 0 || height <= 0) return default;
        RequireNoEncoding();
        if (NeedsRecapture) throw new InvalidOperationException("Retained text atlas changed; repaint before encoding.");
        _encodingSerial = checked(_encodingSerial + 1); _encodingOpen = true;
        long brushBytes = 0;
        try
        {
            _vertexTicket = _vertices.EncodePending(command); _indexTicket = _indices.EncodePending(command);
            for (int page = 0; page < _pageCount; page++)
            { _brushTickets[page] = _brushes[page]!.EncodePending(command); brushBytes += _brushTickets[page].Bytes; }
            command.SetRasterState(new RasterizerState { DepthTest = false, DepthWrite = false, DoBlend = true,
                BlendSrc = RasterizerState.Blending.One, BlendDst = RasterizerState.Blending.OneMinusSrcAlpha,
                Blend = RasterizerState.BlendMode.Add, CullFace = RasterizerState.PolyFace.None });
            command.SetShader(_shader);
            var projection = Float4x4.CreateOrthoOffCenter(0, width, height, 0, -1, 1);
            command.SetMatrix("projection", in projection); command.SetFloat("dpiScale", _dpi);
            command.SetTexture("backdropTexture", _white); command.SetFloat("backdropBlurAmount", 0);
            uint indexOffset = 0;
            for (int i = 0; i < _callCount; i++)
            {
                var call = _calls[i];
                command.SetTexture("texture0", Resolve(call.Texture)); command.SetTexture("fontTexture", Resolve(call.FontAtlas));
                command.SetBuffer("UiBrushes", _brushes[call.BrushPage]!.Buffer, 2);
                command.DrawIndexed(_vao, Topology.Triangles, (uint)call.ElementCount, indexOffset, 0, true);
                indexOffset += (uint)call.ElementCount;
            }
            return new(this, _encodingSerial, new(_callCount, _vertexTicket.Bytes, _indexTicket.Bytes, brushBytes));
        }
        catch { CompleteEncodingCore(false); throw; }
    }

    /// <summary>Confirm all encoded copies only after successful host submission, or cancel
    /// after discarding the unsubmitted command. A stale or foreign ticket is rejected.</summary>
    public void CompleteEncoding(UiPaintEncoding encoding, bool submitted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_encodingOpen || !ReferenceEquals(encoding.Owner, this) || encoding.Serial != _encodingSerial)
            throw new ArgumentException("Stale, null or foreign paint encoding.", nameof(encoding));
        CompleteEncodingCore(submitted);
    }

    private void CompleteEncodingCore(bool submitted)
    {
        try
        {
            if (_vertexTicket.IsValid) _vertices.CompleteSubmission(_vertexTicket, submitted);
            if (_indexTicket.IsValid) _indices.CompleteSubmission(_indexTicket, submitted);
            for (int page = 0; page < _pageCount; page++)
            {
                if (_brushTickets[page].IsValid) _brushes[page]!.CompleteSubmission(_brushTickets[page], submitted);
            }
        }
        finally
        {
            _vertexTicket = _indexTicket = default;
            Array.Clear(_brushTickets, 0, _pageCount); _encodingOpen = false;
        }
    }

    private Texture2D Resolve(object? borrowed)
    {
        Texture2D? texture = new AssetRef<Texture2D>(borrowed as Texture2D).Res;
        return texture.IsValid() ? texture! : _white;
    }
    public void Clear()
    { CheckAlive(); RequireNoEncoding(); Array.Clear(_calls, 0, _callCount); _callCount = _pageCount = 0; _textSource = null; }
    private void RequireNoEncoding()
    { if (_encodingOpen) throw new InvalidOperationException("Complete or cancel the outstanding paint encoding first."); }
    private void CheckAlive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_device, Graphics.Device) || Graphics.IsDeviceLost)
            throw new InvalidOperationException("Graphics device changed or was lost; recreate the paint cache.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        RequireNoEncoding();
        _disposed = true;
        _vao?.Dispose(); _vertices?.Dispose(); _indices?.Dispose(); _white?.Dispose();
        for (int page = 0; page < _brushes.Length; page++) { _brushes[page]?.Dispose(); _brushes[page] = null; }
        _calls = []; _brushScratch = []; _callCount = _pageCount = 0;
        _textSource = null;
        _budgetLease?.Dispose();
    }
}
