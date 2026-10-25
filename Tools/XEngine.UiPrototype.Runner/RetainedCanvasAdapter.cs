using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.RHI;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;

namespace XEngine.UiPrototype;

/// <summary>N1 whole-canvas cache. Uses the engine's existing Quill text/geometry and
/// PaperRenderer RHI backend. Replaying a canvas still uploads all geometry; partial
/// GPU uploads and subtree caches are explicitly outside this prototype.</summary>
internal sealed class RetainedCanvasAdapter : IPrototypeAdapter
{
    private readonly PaperRenderer _renderer;
    private readonly UiQuillPaintIsland? _gpuCache;
    private UiRenderStats _gpuStats;
    private GraphicsFrameBuffer? _gpuTarget;
    private bool _captureGpuTiming;
    private PresentationSurfaceId _surface;
    public UiRenderStats Stats => _gpuCache == null ? _renderer.LastUiStats : _gpuStats;
    public void SetTarget(GraphicsFrameBuffer? target) { _gpuTarget = target; _renderer.SetRenderTarget(target); }
    public void SetGpuTiming(bool enabled)
    {
        _renderer.CaptureGpuTimings = enabled;
        _captureGpuTiming = enabled;
        if (Graphics.TryGetPrimaryPresentationSurface(out var surface)) { _surface = surface; _renderer.SetPresentationSurface(surface); }
    }
    private readonly Canvas _canvas;
    private readonly FontFile _font;
    private readonly PrototypeWorkload _model;
    private readonly RetainedRowViewport _tree, _properties;
    private long _treeVersion = -1, _propertyVersion = -1;
    private string? _text;
    private double _split = -1, _treeScroll = -1, _propertyScroll = -1;
    private float _width, _height, _scale;
    private object? _texture;
    private bool _disposed;
    private bool _effects;
    private PrototypeInput _input, _previousInput;
    private readonly PrototypeViewportInput _viewportInput = new();
    private readonly PrototypeTextEditor _editor = new();
    private bool _fieldFocused;
    private int _pressedRegion, _pressedIdentity = -1;
    public void SetInput(in PrototypeInput input) => _input = input;
    public long BuildCount { get; private set; }

    public RetainedCanvasAdapter(PaperRenderer renderer, FontFile font, PrototypeWorkload model, bool cacheGpu = false)
    {
        _renderer = renderer;
        _font = font; _model = model;
        _canvas = new Canvas(new OwnedAtlasRenderer(renderer), new FontAtlasSettings());
        if (cacheGpu) _gpuCache = new UiQuillPaintIsland(new UiMemoryBudget(32 * 1024 * 1024), 131072, 262144, 32, 256);
        _tree = new RetainedRowViewport(model.Tree, 4096);
        _properties = new RetainedRowViewport(model.Properties, 4096);
    }

    public bool Prepare(float width, float height, float scale, object texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(width) || width < 160 || !float.IsFinite(height) || height < 160
            || !float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        ArgumentNullException.ThrowIfNull(texture);
        _viewportInput.Apply(_input, width, height, _model);
        bool inputChanged = HandleInput(width, height);
        _renderer.UpdateProjection((int)(width * scale), (int)(height * scale));
        // Texture content changes do not alter image geometry. Replacement does.
        bool changed = inputChanged || _effects != _model.VisualEffects || _treeVersion != _model.Tree.Version || _propertyVersion != _model.Properties.Version
            || _text != _model.FieldText || _split != _model.SplitRatio
            || _treeScroll != _model.TreeScroll || _propertyScroll != _model.PropertyScroll
            || _width != width || _height != height || _scale != scale || !ReferenceEquals(_texture, texture);
        if (!changed) return false;
        float left = (float)((width - 12) * _model.SplitRatio);
        float rowsHeight = (height - 88) * .6f;
        _tree.Prepare(_model.TreeScroll, left, rowsHeight, 22);
        _properties.Prepare(_model.PropertyScroll, width - left - 12, rowsHeight, 22);
        _canvas.BeginFrame(width, height, scale);
        _canvas.RectFilled(0, 0, width, height, new Color32(30, 32, 37, 255));
        Text("Hierarchy", 8, 8);
        Text("Properties", left + 20, 8);
        DrawRows(_tree, 4, 36, left, rowsHeight);
        DrawRows(_properties, left + 12, 36, width - left - 16, rowsHeight);
        float inputY = rowsHeight + 44;
        _canvas.RectFilled(4, inputY, width - 8, 28, new Color32(45, 48, 54, 255));
        _canvas.Scissor(4, inputY, width - 8, 28);
        if (_fieldFocused)
        {
            float start = _canvas.MeasureText(_editor.Text[.._editor.SelectionStart], 16, _font).X;
            float end = _canvas.MeasureText(_editor.Text[..(_editor.SelectionStart + _editor.SelectionLength)], 16, _font).X;
            if (_editor.SelectionLength > 0) _canvas.RectFilled(8 + start, inputY + 4, end - start, 20, new Color32(55, 90, 130, 255));
            float caret = _canvas.MeasureText(_editor.Text[.._editor.Cursor], 16, _font).X;
            _canvas.RectFilled(8 + caret, inputY + 4, 1, 20, new Color32(220, 222, 228, 255));
        }
        Text(_model.FieldText, 8, inputY + 4);
        _canvas.ResetScissor();
        _canvas.DrawImage(texture, 4, inputY + 36, width - 8, height - inputY - 40);
        if (_model.VisualEffects) PrototypeVisualEffects.Draw(_canvas, width, height);
        _gpuCache?.Capture(_canvas);
        _effects = _model.VisualEffects;
        _treeVersion = _model.Tree.Version; _propertyVersion = _model.Properties.Version;
        _text = _model.FieldText; _split = _model.SplitRatio;
        _treeScroll = _model.TreeScroll; _propertyScroll = _model.PropertyScroll;
        _width = width; _height = height; _scale = scale; _texture = texture;
        BuildCount++;
        return true;
    }

    private bool HandleInput(float width, float height)
    {
        float left = (float)((width - 12) * _model.SplitRatio), rowsHeight = (height - 88) * .6f;
        bool oldFocus = _fieldFocused; int oldCursor = _editor.Cursor, oldAnchor = _editor.Anchor;
        _editor.SetText(_model.FieldText);
        int region = 0, identity = -1; bool branch = false;
        if (_input.Y >= 36 && _input.Y < 36 + rowsHeight)
        {
            bool tree = _input.X >= 4 && _input.X < left + 4;
            bool props = _input.X >= left + 12 && _input.X < width - 4;
            if (tree || props)
            {
                IRowSource source = tree ? _model.Tree : _model.Properties;
                double scroll = Math.Clamp(tree ? _model.TreeScroll : _model.PropertyScroll, 0, Math.Max(0, source.Count * 22d - rowsHeight));
                int rank = (int)((_input.Y - 36 + scroll) / 22);
                if (rank < source.Count)
                {
                    var row = source.GetRow(rank); identity = row.Identity; region = tree ? 1 : 2;
                    float indent = Math.Min(row.Depth * 12f, Math.Max(0, left - 40));
                    branch = tree && row.HasChildren && _input.X < 4 + indent + 16;
                }
            }
        }
        else if (_input.X >= 4 && _input.X < width - 4 && _input.Y >= rowsHeight + 44 && _input.Y < rowsHeight + 72) region = 3;
        if (_input.LeftDown && !_previousInput.LeftDown)
        {
            _pressedRegion = region; _pressedIdentity = identity;
            _fieldFocused = region == 3;
            if (_fieldFocused) _editor.MoveToEnd();
        }
        if (!_input.LeftDown && _previousInput.LeftDown && _pressedRegion == region && _pressedIdentity == identity)
        {
            if (region == 1)
            {
                _model.SelectProperty(-1);
                _model.Apply(new ReplayEvent(0, branch ? ReplayAction.ToggleBranch : ReplayAction.SelectNode, identity));
            }
            else if (region == 2) _model.SelectProperty(identity);
        }
        PrototypeKeys pressed = _input.Keys & ~_previousInput.Keys;
        if (_fieldFocused)
        {
            _editor.Apply(pressed, _input.Keys, _input.Text);
            _model.EditField(_editor.Text);
            if ((pressed & (PrototypeKeys.Enter | PrototypeKeys.Escape)) != 0) _fieldFocused = false;
        }
        _previousInput = _input;
        return oldFocus != _fieldFocused || oldCursor != _editor.Cursor || oldAnchor != _editor.Anchor;
    }

    private void DrawRows(RetainedRowViewport rows, float x, float y, float width, float height)
    {
        _canvas.Scissor(x, y, width, height);
        for (int i = 0; i < rows.VisibleCount; i++)
        {
            RowVisual row = rows.GetVisibleRow(i);
            float top = y + (float)rows.GetRowTop(i);
            if (row.Selected) _canvas.RectFilled(x, top, width, 22, new Color32(55, 90, 130, 255));
            float indent = Math.Min(row.Depth * 12f, Math.Max(0, width - 40));
            if (row.HasChildren) Text(row.Expanded ? "-" : "+", x + indent + 2, top + 2);
            Text(row.Text, x + indent + 16, top + 2);
        }
        _canvas.ResetScissor();
    }

    private void Text(string text, float x, float y)
        => _canvas.DrawText(text, x, y, new Color32(220, 222, 228, 255), 16, _font);

    public void Render()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (BuildCount == 0) throw new InvalidOperationException("Prepare before rendering.");
        if (_gpuCache == null) _canvas.Render();
        else
        {
            int width = (int)(_width * _scale), height = (int)(_height * _scale);
            var stats = _gpuCache.Render(_gpuTarget, width, height, captureGpuTiming: _captureGpuTiming, presentationSurface: _surface);
            _gpuStats = new UiRenderStats { FrameWidth = width, FrameHeight = height, UiDrawCalls = stats.Draws,
                VertexUploadBytes = stats.VertexBytes, IndexUploadBytes = stats.IndexBytes, BrushUploadBytes = stats.BrushBytes };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gpuCache?.Dispose(); _tree.Dispose(); _properties.Dispose(); _canvas.Dispose();
        _texture = null; _text = null; _disposed = true;
    }

    // Canvas.Dispose only disposes its renderer. Account for font atlases created
    // through the renderer, while leaving the caller's viewport texture borrowed.
    private sealed class OwnedAtlasRenderer(ICanvasRenderer renderer) : ICanvasRenderer
    {
        private readonly List<IDisposable> _atlases = new();
        public object CreateTexture(uint width, uint height)
        {
            object texture = renderer.CreateTexture(width, height);
            if (texture is IDisposable disposable) _atlases.Add(disposable);
            return texture;
        }
        public Int2 GetTextureSize(object texture) => renderer.GetTextureSize(texture);
        public void SetTextureData(object texture, IntRect bounds, byte[] data) => renderer.SetTextureData(texture, bounds, data);
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) => renderer.RenderCalls(canvas, calls);
        public bool SupportsBackdropBlur => renderer.SupportsBackdropBlur;
        public void Dispose()
        {
            for (int i = 0; i < _atlases.Count; i++) _atlases[i].Dispose();
            _atlases.Clear(); renderer.Dispose();
        }
    }
}
