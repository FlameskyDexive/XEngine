using ImGuiNET;
using System.Numerics;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;

namespace XEngine.UiPrototype;

internal sealed class ImGuiAdapter : IPrototypeAdapter
{
    private readonly IntPtr _context;
    private readonly ImGuiRhiRenderer _renderer;
    private readonly PrototypeWorkload _model;
    private Texture2D? _atlas, _viewport;
    private float _scale;
    private bool _disposed;
    private PrototypeInput _input;
    private readonly PrototypeViewportInput _viewportInput = new();
    public long BuildCount { get; private set; }
    public UiRenderStats Stats => _renderer.Stats;
    public void SetTarget(GraphicsFrameBuffer? target) => _renderer.Target = target;
    public void SetGpuTiming(bool enabled) => _renderer.CaptureGpuTiming = enabled;
    public ImGuiAdapter(PrototypeWorkload model)
    {
        _model = model;
        _context = ImGui.CreateContext(); ImGui.SetCurrentContext(_context);
        unsafe { ImGui.GetIO().NativePtr->IniFilename = null; ImGui.GetIO().NativePtr->LogFilename = null; }
        ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        _renderer = new ImGuiRhiRenderer();
    }

    public void SetInput(in PrototypeInput input)
    {
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO(); io.AddMousePosEvent(input.X, input.Y); io.AddMouseButtonEvent(0, input.LeftDown);
        io.AddKeyEvent(ImGuiKey.ModCtrl, (input.Keys & PrototypeKeys.Control) != 0);
        io.AddKeyEvent(ImGuiKey.ModShift, (input.Keys & PrototypeKeys.Shift) != 0);
        FeedKey(io, input.Keys, PrototypeKeys.A, ImGuiKey.A); FeedKey(io, input.Keys, PrototypeKeys.Backspace, ImGuiKey.Backspace);
        FeedKey(io, input.Keys, PrototypeKeys.Delete, ImGuiKey.Delete); FeedKey(io, input.Keys, PrototypeKeys.Left, ImGuiKey.LeftArrow);
        FeedKey(io, input.Keys, PrototypeKeys.Right, ImGuiKey.RightArrow); FeedKey(io, input.Keys, PrototypeKeys.Home, ImGuiKey.Home);
        FeedKey(io, input.Keys, PrototypeKeys.End, ImGuiKey.End); FeedKey(io, input.Keys, PrototypeKeys.Enter, ImGuiKey.Enter);
        FeedKey(io, input.Keys, PrototypeKeys.Escape, ImGuiKey.Escape);
        if (input.Text.Length > 0) io.AddInputCharactersUTF8(input.Text);
        _input = input;
    }
    private static void FeedKey(ImGuiIOPtr io, PrototypeKeys keys, PrototypeKeys flag, ImGuiKey key)
        => io.AddKeyEvent(key, (keys & flag) != 0);

    private unsafe void UpdateFont(float scale)
    {
        if (_scale == scale) return;
        var io = ImGui.GetIO();
        io.Fonts.Clear();
        io.Fonts.AddFontFromFileTTF(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"), 16 * scale);
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        var bytes = new byte[checked(width * height * 4)];
        System.Runtime.InteropServices.Marshal.Copy(pixels, bytes, 0, bytes.Length);
        var atlas = new Texture2D((uint)width, (uint)height);
        atlas.SetData(bytes.AsMemory(), 0, 0, (uint)width, (uint)height);
        _atlas?.Dispose(); _atlas = atlas;
        io.Fonts.SetTexID((IntPtr)1); io.Fonts.ClearTexData();
        io.FontGlobalScale = 1 / scale; _scale = scale;
    }

    public bool Prepare(float width, float height, float scale, object texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _viewportInput.Apply(_input, width, height, _model);
        ImGui.SetCurrentContext(_context); UpdateFont(scale);
        _viewport = (Texture2D)texture;
        var io = ImGui.GetIO(); io.DisplaySize = new Vector2(width, height);
        io.DisplayFramebufferScale = new Vector2(scale); io.DeltaTime = Math.Max(.000001f, _input.DeltaSeconds);
        ImGui.NewFrame();
        ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(220 / 255f, 222 / 255f, 228 / 255f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.Begin("N1", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(Vector2.Zero, new Vector2(width, height), 0xff25201e);
        float left = (float)((width - 12) * _model.SplitRatio), rowsHeight = (height - 88) * .6f;
        draw.AddText(new Vector2(8, 8), 0xffe4dedc, "Hierarchy");
        draw.AddText(new Vector2(left + 20, 8), 0xffe4dedc, "Properties");
        DrawRows(draw, _model.Tree, _model.TreeScroll, 4, 36, left, rowsHeight, true);
        DrawRows(draw, _model.Properties, _model.PropertyScroll, left + 12, 36, width - left - 16, rowsHeight, false);
        float inputY = rowsHeight + 44;
        string value = _model.FieldText;
        draw.AddRectFilled(new Vector2(4, inputY), new Vector2(width - 4, inputY + 28), 0xff36302d);
        ImGui.SetCursorScreenPos(new Vector2(8, inputY + 4)); ImGui.SetNextItemWidth(width - 16);
        if (ImGui.InputText("##input", ref value, 4096)) _model.EditField(value);
        draw.AddImage((IntPtr)2, new Vector2(4, inputY + 36), new Vector2(width - 4, height - 4));
        if (_model.VisualEffects) PrototypeVisualEffects.Draw(draw, width, height);
        ImGui.End(); ImGui.PopStyleColor(2); ImGui.PopStyleVar(3); ImGui.Render(); BuildCount++; return true;
    }

    private void DrawRows(ImDrawListPtr draw, IRowSource source, double scroll, float x, float y, float width, float height, bool tree)
    {
        double offset = Math.Clamp(scroll, 0, Math.Max(0, source.Count * 22d - height));
        int first = (int)(offset / 22), end = Math.Min(source.Count, (int)Math.Ceiling((offset + height) / 22));
        draw.PushClipRect(new Vector2(x, y), new Vector2(x + width, y + height), true);
        ImGui.PushID(tree ? "tree" : "properties");
        for (int i = first; i < end; i++)
        {
            var row = source.GetRow(i); float top = y + (float)(i * 22d - offset);
            float indent = Math.Min(row.Depth * 12f, Math.Max(0, width - 40));
            if (row.Selected) draw.AddRectFilled(new Vector2(x, top), new Vector2(x + width, top + 22), 0xff825a37);
            if (row.HasChildren) draw.AddText(new Vector2(x + indent + 2, top + 2), 0xffe4dedc, row.Expanded ? "-" : "+");
            draw.AddText(new Vector2(x + indent + 16, top + 2), 0xffe4dedc, row.Text);
            ImGui.PushID(row.Identity);
            ImGui.SetCursorScreenPos(new Vector2(x, Math.Max(y, top)));
            float hitHeight = Math.Min(y + height, top + 22) - Math.Max(y, top);
            if (ImGui.InvisibleButton("row", new Vector2(width, hitHeight)))
            {
                if (!tree) _model.SelectProperty(row.Identity);
                else
                {
                    _model.SelectProperty(-1);
                    _model.Apply(new ReplayEvent(0, row.HasChildren && ImGui.GetIO().MousePos.X < x + indent + 16
                        ? ReplayAction.ToggleBranch : ReplayAction.SelectNode, row.Identity));
                }
            }
            ImGui.PopID();
        }
        ImGui.PopID(); draw.PopClipRect();
    }

    public void Render()
    {
        ImGui.SetCurrentContext(_context); _renderer.Render(ImGui.GetDrawData(), _atlas!, _viewport!);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _renderer.Dispose(); _atlas?.Dispose(); ImGui.DestroyContext(_context); _disposed = true;
    }
}
