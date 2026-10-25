using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.PaperUI.Events;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.UiPrototype;

internal sealed class PaperAdapter : IPrototypeAdapter
{
    private readonly Paper _paper;
    private readonly PaperRenderer _renderer;
    private readonly PrototypeWorkload _model;
    private readonly FontFile _font;
    private readonly Action<string> _inputChanged;
    private bool _disposed;
    private PrototypeInput _input;
    private readonly PrototypeViewportInput _viewportInput = new();
    public long BuildCount { get; private set; }
    public UiRenderStats Stats => _renderer.LastUiStats;
    public void SetTarget(GraphicsFrameBuffer? target) => _renderer.SetRenderTarget(target);
    public void SetGpuTiming(bool enabled)
    {
        _renderer.CaptureGpuTimings = enabled;
        if (Graphics.TryGetPrimaryPresentationSurface(out var surface)) _renderer.SetPresentationSurface(surface);
    }
    public PaperAdapter(PaperRenderer renderer, FontFile font, PrototypeWorkload model)
    {
        _renderer = renderer; _font = font; _model = model;
        _paper = new Paper(new DeferredRenderer(renderer), 1280, 720, new FontAtlasSettings());
        _paper.DevTools.Enabled = false;
        _inputChanged = _model.EditField;
    }

    public void SetInput(in PrototypeInput input)
    {
        _paper.SetPointerPosition(input.X, input.Y);
        if (input.LeftDown != _input.LeftDown) _paper.SetPointerState(PaperMouseBtn.Left, input.X, input.Y, input.LeftDown, false);
        SetKey(input, PrototypeKeys.Control, PaperKey.LeftControl); SetKey(input, PrototypeKeys.Shift, PaperKey.LeftShift);
        SetKey(input, PrototypeKeys.A, PaperKey.A); SetKey(input, PrototypeKeys.Backspace, PaperKey.Backspace);
        SetKey(input, PrototypeKeys.Delete, PaperKey.Delete); SetKey(input, PrototypeKeys.Left, PaperKey.Left);
        SetKey(input, PrototypeKeys.Right, PaperKey.Right); SetKey(input, PrototypeKeys.Home, PaperKey.Home);
        SetKey(input, PrototypeKeys.End, PaperKey.End); SetKey(input, PrototypeKeys.Enter, PaperKey.Enter);
        SetKey(input, PrototypeKeys.Escape, PaperKey.Escape);
        _paper.AddInputCharacter(input.Text); _input = input;
    }
    private void SetKey(in PrototypeInput input, PrototypeKeys flag, PaperKey key)
    {
        if ((input.Keys & flag) != (_input.Keys & flag)) _paper.SetKeyState(key, (input.Keys & flag) != 0);
    }

    private ElementBuilder Box(string id, int index, float x, float y, float width, float height)
        => _paper.Box(id, index).PositionType(PositionType.SelfDirected).Position(x, y).Size(width, height);

    private void Text(string id, int index, string text, float x, float y, float width)
        => Box(id, index, x, y, width, 22).Text(text, _font).FontSize(16)
            .TextColor(new Color32(220, 222, 228, 255)).Alignment(XEngine.PaperUI.TextAlignment.Left).IsNotInteractable();

    public bool Prepare(float width, float height, float scale, object texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewportInput.Apply(_input, width, height, _model);
        if (_viewportInput.Dragging) _paper.ClearFocus();
        _renderer.UpdateProjection((int)(width * scale), (int)(height * scale));
        _paper.SetResolution(width, height); _paper.BeginFrame(Math.Max(.000001f, _input.DeltaSeconds), scale);
        Box("background", 0, 0, 0, width, height).BackgroundColor(new Color32(30, 32, 37, 255)).IsNotInteractable();
        float left = (float)((width - 12) * _model.SplitRatio), rowsHeight = (height - 88) * .6f;
        Text("heading", 0, "Hierarchy", 8, 8, left - 8);
        Text("heading", 1, "Properties", left + 20, 8, width - left - 20);
        DrawRows(_model.Tree, _model.TreeScroll, 4, 36, left, rowsHeight, true);
        DrawRows(_model.Properties, _model.PropertyScroll, left + 12, 36, width - left - 16, rowsHeight, false);
        float inputY = rowsHeight + 44;
        using (Box("inputBackground", 0, 4, inputY, width - 8, 28).BackgroundColor(new Color32(45, 48, 54, 255)).Enter())
            Box("input", 0, 4, 4, width - 16, 20).FontSize(16).TextField(_model.FieldText, _font, _inputChanged,
                textColor: new Color32(220, 222, 228, 255));
        Box("viewport", 0, 4, inputY + 36, width - 8, height - inputY - 40).BackgroundImage(texture).IsNotInteractable();
        if (_model.VisualEffects) PrototypeVisualEffects.Draw(_paper, width, height);
        _paper.EndFrame(); BuildCount++; return true;
    }

    private void DrawRows(IRowSource source, double scroll, float x, float y, float width, float height, bool tree)
    {
        double offset = Math.Clamp(scroll, 0, Math.Max(0, source.Count * 22d - height));
        int first = (int)(offset / 22), end = Math.Min(source.Count, (int)Math.Ceiling((offset + height) / 22));
        using (Box("rows", tree ? 0 : 1, x, y, width, height).Clip().Enter())
        {
            for (int i = first; i < end; i++)
            {
                var row = source.GetRow(i); float top = (float)(i * 22d - offset);
                float indent = Math.Min(row.Depth * 12f, Math.Max(0, width - 40));
                var element = Box("row", row.Identity, 0, top, width, 22);
                element.OnClick((_model, tree, row.Identity, row.HasChildren, indent), static (state, click) =>
                {
                    if (click.Phase != ClickPhase.Click || click.Button != PaperMouseBtn.Left) return;
                    if (!state.tree) state._model.SelectProperty(state.Identity);
                    else
                    {
                        state._model.SelectProperty(-1);
                        state._model.Apply(new ReplayEvent(0, state.HasChildren && click.RelativePosition.X < state.indent + 16
                            ? ReplayAction.ToggleBranch : ReplayAction.SelectNode, state.Identity));
                    }
                    click.StopPropagation();
                });
                if (row.Selected) element.BackgroundColor(new Color32(55, 90, 130, 255));
                Text("label", row.Identity, row.Text, indent + 16, top + 2, Math.Max(1, width - indent - 16));
                if (row.HasChildren) Text("branch", row.Identity, row.Expanded ? "-" : "+", indent + 2, top + 2, 14);
            }
        }
    }

    public void Render() => _renderer.RenderCalls(_paper.Canvas, _paper.Canvas.DrawCalls);
    public void Dispose()
    {
        if (_disposed) return;
        _paper.Canvas.Dispose(); _disposed = true;
    }

    // Separates Paper's build/layout/geometry from RHI submission, so each adapter
    // uses the same Prepare/Render timing boundaries. Atlas uploads remain in Prepare.
    private sealed class DeferredRenderer(PaperRenderer renderer) : ICanvasRenderer
    {
        private readonly List<IDisposable> _atlases = new();
        public object CreateTexture(uint w, uint h)
        {
            object texture = renderer.CreateTexture(w, h);
            if (texture is IDisposable disposable) _atlases.Add(disposable);
            return texture;
        }
        public Int2 GetTextureSize(object texture) => renderer.GetTextureSize(texture);
        public void SetTextureData(object texture, IntRect bounds, byte[] data) => renderer.SetTextureData(texture, bounds, data);
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
        public void Dispose()
        {
            for (int i = 0; i < _atlases.Count; i++) _atlases[i].Dispose();
            _atlases.Clear(); renderer.Dispose();
        }
    }
}
