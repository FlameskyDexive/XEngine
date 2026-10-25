// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Quill;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.GUI;

public class PaperRenderer : ICanvasRenderer
{
    public UiRenderStats LastUiStats { get; private set; }
    public bool CaptureGpuTimings { get; set; }
    public bool SupportsRenderLayers => true;
    public const int MaximumRenderLayers = 256;
    private readonly IUiCommandLayer?[] _pendingLayers = new IUiCommandLayer[MaximumRenderLayers];
    private bool _rendering;

    private UiRenderStats _currentUiStats;

    private ShaderVariant _shaderProgram;
    private GraphicsVertexArray _vertexArrayObject;
    private GraphicsBuffer _vertexBuffer;
    private GraphicsBuffer _elementBuffer;
    private Texture2D _defaultTexture;

    private Float4x4 _projection;
    private int _fbWidth;
    private int _fbHeight;
    // Vulkan command buffers do not inherit the render target or dynamic viewport from
    // an earlier submission. Keep the destination with this renderer so every UI batch
    // records the complete state it needs.
    private GraphicsFrameBuffer? _renderTarget;
    private PresentationSurfaceId _presentationSurface;

    // The editor deliberately does not sample the framebuffer behind UI elements. Apart from
    // avoiding expensive full-frame copies, this keeps every supported driver on one UI path.
    public bool SupportsBackdropBlur => false;

    private Vertex[] _vertexUpload = Array.Empty<Vertex>();
    private uint[] _indexUpload = Array.Empty<uint>();
    private byte[] _vertexRawUpload = Array.Empty<byte>();
    private UiBrushRecord[] _brushRecordUpload = Array.Empty<UiBrushRecord>();

    // One uniform buffer per brush-record page (96 records * 144 B = 13,824 B, under every
    // backend's 16 KB minimum uniform-buffer size). The default editor layout fits in one
    // page; extra pages only appear past 96 unique draw states per frame.
    private const int BrushPageSizeBytes = Canvas.BrushRecordsPerPage * UiBrushRecord.SizeInBytes;
    private readonly List<GraphicsBuffer> _brushPageBuffers = new();

    public void Initialize(int width, int height)
    {
        InitializeShaders();

        _vertexBuffer = Graphics.CreateBuffer<byte>(BufferType.VertexBuffer, Array.Empty<byte>(), true);
        _elementBuffer = Graphics.CreateBuffer<uint>(BufferType.ElementsBuffer, Array.Empty<uint>(), true);

        // Vertex format matches Quill's Vertex struct (24 bytes):
        //   0: Float2     position     (offset 0)
        //   1: Float2     UV           (offset 8)
        //   2: UByte4     color        (offset 16, normalized)
        //   3: Float1     brush index  (offset 20) — selects a UiBrushRecord in the bound page
        var vertexFormat = new VertexFormat(
        [
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 2, 0),
            new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2, 0),
            new(VertexFormat.VertexSemantic.Color, VertexFormat.VertexType.UnsignedByte, 4, 0, true),
            new(VertexFormat.VertexSemantic.TexCoord1, VertexFormat.VertexType.Float, 1, 0),
        ]);

        _vertexArrayObject = Graphics.CreateVertexArray(vertexFormat, _vertexBuffer, _elementBuffer);

        _defaultTexture = new Texture2D(1, 1);
        _defaultTexture.SetData(new Memory<byte>(new byte[] { 255, 255, 255, 255 }), 0, 0, 1, 1);

        UpdateProjection(width, height);
    }

    public void UpdateProjection(int width, int height)
    {
        _fbWidth = width;
        _fbHeight = height;
        _projection = Float4x4.CreateOrthoOffCenter(0, width, height, 0, -1, 1);
    }

    /// <summary>
    /// Sets the framebuffer that receives this renderer's next UI batches. A null target
    /// selects the window backbuffer.
    /// </summary>
    public void SetRenderTarget(GraphicsFrameBuffer? renderTarget)
    {
        _renderTarget = renderTarget;
    }

    /// <summary>Routes this renderer's command buffers to an active presentation surface.</summary>
    public void SetPresentationSurface(PresentationSurfaceId presentationSurface)
    {
        _presentationSurface = presentationSurface;
    }

    public void Cleanup()
    {
        _vertexBuffer?.Dispose();
        _elementBuffer?.Dispose();
        _vertexArrayObject?.Dispose();
        _shaderProgram?.Dispose();
        _defaultTexture?.Dispose();
        for (int i = 0; i < _brushPageBuffers.Count; i++)
            _brushPageBuffers[i]?.Dispose();
        _brushPageBuffers.Clear();
    }

    private void InitializeShaders()
    {
        var shader = Shader.LoadDefault(DefaultShader.UI);
        if (shader.IsNotValid())
        {
            Debug.LogError("Failed to load UI shader.");
            return;
        }

        Rendering.Shaders.ShaderPass pass = shader.GetPass(0);
        if (!pass.TryGetVariant(null, out _shaderProgram))
            Debug.LogError("Failed to compile UI shader.");
    }

    public object CreateTexture(uint width, uint height)
    {
        var tex = new Texture2D(width, height);

        Graphics.SetTextureFilters(tex.Handle, TextureMin.Linear, TextureMag.Linear);
        Graphics.SetWrapS(tex.Handle, TextureWrap.ClampToEdge);
        Graphics.SetWrapT(tex.Handle, TextureWrap.ClampToEdge);

        return tex;
    }

    public bool SupportsFontAtlasRetirement => true;
    public void RetireFontAtlas(object texture)
    {
        if (texture is not Texture2D atlas) throw new ArgumentException("Invalid font atlas", nameof(texture));
        atlas.Dispose(); // GraphicsTexture.Dispose queues destruction after prior submissions.
    }

    public Int2 GetTextureSize(object texture)
    {
        if (texture is not Texture2D tex) throw new ArgumentException("Invalid texture type");
        return new Int2((int)tex.Width, (int)tex.Height);
    }

    public void SetTextureData(object texture, IntRect bounds, byte[] data)
    {
        if (texture is not Texture2D tex) throw new ArgumentException("Invalid texture type");
        tex.SetData(new Memory<byte>(data), bounds.Min.X, bounds.Min.Y, (uint)bounds.Size.X, (uint)bounds.Size.Y);
    }

    public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls)
    {
        if (_rendering) throw new InvalidOperationException("Paper rendering cannot reenter a pending composition.");
        _rendering = true;
        try { RenderCallsCore(canvas, drawCalls); }
        finally { _rendering = false; }
    }

    private void RenderCallsCore(Canvas canvas, IReadOnlyList<DrawCall> drawCalls)
    {
        _currentUiStats = new UiRenderStats
        {
            FrameWidth = _fbWidth,
            FrameHeight = _fbHeight,
            UiDrawCalls = drawCalls.Count,
        };
        if (drawCalls.Count == 0)
        {
            LastUiStats = _currentUiStats;
            return;
        }

        float dpiScale = canvas.FramebufferScale;

        var state = new RasterizerState
        {
            DepthTest = false,
            DepthWrite = false,
            DoBlend = true,
            BlendSrc = RasterizerState.Blending.One,
            BlendDst = RasterizerState.Blending.OneMinusSrcAlpha,
            Blend = RasterizerState.BlendMode.Add,
            CullFace = RasterizerState.PolyFace.None,
        };

        using var cmd = Graphics.GetCommandBuffer("Paper UI");
        cmd.CaptureGpuTiming = CaptureGpuTimings;
        if (_presentationSurface.IsValid)
            cmd.SetPresentationSurface(_presentationSurface);

        // Do not rely on state from a preceding command buffer. In particular, Vulkan
        // requires viewport state to be set in this command buffer; otherwise its default
        // top-left viewport reverses Paper's top-origin projection on the first UI batch.
        cmd.SetRenderTarget(_renderTarget);
        cmd.SetViewport(0, 0, (uint)_fbWidth, (uint)_fbHeight);
        cmd.DisableScissor();
        cmd.SetRasterState(state);
        cmd.SetShader(_shaderProgram);
        cmd.SetMatrix("projection", in _projection);
        cmd.SetFloat("dpiScale", dpiScale);
        cmd.SetTexture("backdropTexture", _defaultTexture);
        // Frame-constant: this renderer never composites backdrop blur (SupportsBackdropBlur
        // is false), so the flag is cleared once instead of per draw call.
        cmd.SetFloat("backdropBlurAmount", 0f);

        // Upload raw Vertex data (24 bytes per vertex)
        if (canvas.Vertices.Count > 0)
        {
            int vcount = canvas.Vertices.Count;
            EnsureCapacity(ref _vertexUpload, vcount);
            canvas.CopyVerticesTo(_vertexUpload);
            int rawBytes = vcount * Vertex.SizeInBytes;
            EnsureCapacity(ref _vertexRawUpload, rawBytes);
            var handle = GCHandle.Alloc(_vertexUpload, GCHandleType.Pinned);
            try { Marshal.Copy(handle.AddrOfPinnedObject(), _vertexRawUpload, 0, rawBytes); }
            finally { handle.Free(); }
            cmd.UpdateBuffer<byte>(_vertexBuffer, _vertexRawUpload.AsSpan(0, rawBytes));
            _currentUiStats.VertexUploadBytes += rawBytes;
        }

        if (canvas.Indices.Count > 0)
        {
            int icount = canvas.Indices.Count;
            EnsureCapacity(ref _indexUpload, icount);
            canvas.CopyIndicesTo(_indexUpload);
            cmd.UpdateBuffer<uint>(_elementBuffer, _indexUpload.AsSpan(0, icount));
            _currentUiStats.IndexUploadBytes += (long)icount * sizeof(uint);
        }

        // Upload the frame's brush-record pages. Vertices select a record via their brush
        // index; each draw call names the page it addresses, so scissor/brush changes no
        // longer split draw calls (only texture/font-atlas/page switches do).
        int recordCount = canvas.BrushRecordCount;
        if (recordCount > 0)
        {
            EnsureCapacity(ref _brushRecordUpload, recordCount);
            canvas.CopyBrushRecordsTo(_brushRecordUpload);
            int pageCount = (recordCount + Canvas.BrushRecordsPerPage - 1) / Canvas.BrushRecordsPerPage;
            for (int page = 0; page < pageCount; page++)
            {
                while (_brushPageBuffers.Count <= page)
                {
                    // Fixed full-page allocation so partial per-frame updates never resize.
                    _brushPageBuffers.Add(Graphics.CreateBuffer<byte>(
                        BufferType.UniformBuffer, new byte[BrushPageSizeBytes], true));
                }

                int start = page * Canvas.BrushRecordsPerPage;
                int count = Math.Min(Canvas.BrushRecordsPerPage, recordCount - start);
                cmd.UpdateBuffer<UiBrushRecord>(_brushPageBuffers[page], _brushRecordUpload.AsSpan(start, count));
                _currentUiStats.BrushUploadBytes += (long)count * UiBrushRecord.SizeInBytes;
            }
        }

        int indexOffset = 0;
        int boundPage = -1;
        Texture2D? boundTexture = null;
        Texture2D? boundFontTexture = null;
        bool texturesBound = false;
        int pendingCount = 0;
        bool submitted = false;
        try
        {
        for (int drawIndex = 0; drawIndex < drawCalls.Count; drawIndex++)
        {
            DrawCall drawCall = drawCalls[drawIndex];
            if (drawCall.RenderLayer != null)
            {
                if (drawCall.RenderLayer is not IUiCommandLayer layer)
                    throw new NotSupportedException("Paper requires an IUiCommandLayer implementation.");
                if (pendingCount == MaximumRenderLayers)
                    throw new InvalidOperationException("Paper render-layer capacity exceeded.");
                for (int i = 0; i < pendingCount; i++)
                    if (ReferenceEquals(_pendingLayers[i], layer))
                        throw new InvalidOperationException("Duplicate layer in the submitted draw list.");
                var context = new UiCommandLayerContext(_fbWidth, _fbHeight, drawCall.LayerContext);
                layer.Validate(context);
                UiRenderStats stats = layer.Encode(cmd, context);
                _pendingLayers[pendingCount++] = layer;
                _currentUiStats.UiDrawCalls += stats.UiDrawCalls - 1;
                _currentUiStats.VertexUploadBytes += stats.VertexUploadBytes;
                _currentUiStats.IndexUploadBytes += stats.IndexUploadBytes;
                _currentUiStats.BrushUploadBytes += stats.BrushUploadBytes;
                // Layer recording owns its bindings; Paper's cached bindings are now stale.
                cmd.SetRasterState(state); cmd.SetShader(_shaderProgram);
                cmd.SetMatrix("projection", in _projection); cmd.SetFloat("dpiScale", dpiScale);
                cmd.SetTexture("backdropTexture", _defaultTexture); cmd.SetFloat("backdropBlurAmount", 0f);
                texturesBound = false; boundPage = -1; boundTexture = boundFontTexture = null;
                continue;
            }
            if (drawCall.ElementCount == 0) { _currentUiStats.UiDrawCalls--; continue; }

            // Texture. The brush/shape texture goes on texture0; the font atlas is bound separately
            // as a persistent sampler so text batches into the same draw call as surrounding shapes.
            Texture2D? resolvedTexture = new AssetRef<Texture2D>(drawCall.Texture as Texture2D).Res;
            Texture2D texture = resolvedTexture.IsValid() ? resolvedTexture! : _defaultTexture;
            Texture2D? resolvedFont = new AssetRef<Texture2D>(drawCall.FontAtlas as Texture2D).Res;
            Texture2D fontTexture = resolvedFont.IsValid() ? resolvedFont! : _defaultTexture;
            if (!texturesBound || !ReferenceEquals(texture, boundTexture))
            {
                cmd.SetTexture("texture0", texture);
                cmd.SetFloat("texture0SrgbDecode", RequiresSrgbDecode(texture) ? 1f : 0f);
                boundTexture = texture;
            }

            if (!texturesBound || !ReferenceEquals(fontTexture, boundFontTexture))
            {
                cmd.SetTexture("fontTexture", fontTexture);
                boundFontTexture = fontTexture;
            }

            texturesBound = true;

            if (drawCall.BrushPage != boundPage)
            {
                // Binding point 2 keeps clear of the GL convention reserving 0 for GlobalUniforms.
                cmd.SetBuffer("UiBrushes", _brushPageBuffers[drawCall.BrushPage], 2);
                boundPage = drawCall.BrushPage;
            }

            cmd.DrawIndexed(_vertexArrayObject, Topology.Triangles, (uint)drawCall.ElementCount, (uint)indexOffset, 0, true);
            indexOffset += drawCall.ElementCount;
        }

        Graphics.Submit(cmd);
        submitted = true;
        LastUiStats = _currentUiStats;
        }
        finally
        {
            Exception? completionError = null;
            for (int i = 0; i < pendingCount; i++)
            {
                try { _pendingLayers[i]!.Complete(submitted); }
                catch (Exception error) { completionError ??= error; }
                finally { _pendingLayers[i] = null; }
            }
            if (completionError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(completionError).Throw();
        }
    }

    private static void EnsureCapacity<T>(ref T[] storage, int required)
    {
        if (storage.Length >= required) return;
        int capacity = storage.Length == 0 ? 256 : storage.Length;
        while (capacity < required) capacity = checked(capacity * 2);
        Array.Resize(ref storage, capacity);
    }

    private static bool RequiresSrgbDecode(Texture2D texture) => texture.Srgb && texture.ImageFormat is
        TextureImageFormat.Color4b or
        TextureImageFormat.Etc2Rgb8 or
        TextureImageFormat.Etc2Rgba8 or
        TextureImageFormat.Astc4x4;

    public void Dispose()
    {
        Cleanup();
    }
}

public struct UiRenderStats
{
    public int FrameWidth;
    public int FrameHeight;
    public int UiDrawCalls;
    public long UiRenderTargetBytes;
    // Actual UpdateBuffer payload recorded by this renderer. Excludes allocation capacity,
    // font/texture uploads and backend-generated uniform buffers or driver staging copies.
    public long VertexUploadBytes;
    public long IndexUploadBytes;
    public long BrushUploadBytes;
    public readonly long BufferUploadBytes => VertexUploadBytes + IndexUploadBytes + BrushUploadBytes;
}
