using XEngine.Editor.UI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.UiPrototype;

/// <summary>N2 correctness fixture. Quill/PaperRenderer are only a visualization bridge;
/// this does not implement or benchmark the N3 incremental rendering cache.</summary>
internal sealed class KernelValidationGame(string destination) : Game
{
    public override void OnGui(Scene? scene, Paper paper)
    {
        try { Validate(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 2; }
        Quit();
    }

    private void Validate()
    {
        Directory.CreateDirectory(destination);
        using var store = new UiNodeStore();
        var nodes = new List<UiNodeHandle>();
        var labels = new Dictionary<UiNodeHandle, string>();
        UiNodeHandle Add(UiNodeHandle parent, UiControlKind kind, string label, UiStyle style)
        {
            var node = store.CreateControl(kind, parent);
            store.SetStyle(node, style with { Focusable = kind is UiControlKind.Button or UiControlKind.Toggle or UiControlKind.Slider });
            nodes.Add(node); labels.Add(node, label);
            return node;
        }
        var root = Add(default, UiControlKind.Container, "", new() { Flow = UiFlow.Row, Padding = 16, Gap = 16 });
        store.SetRootBounds(root, new(0, 0, 800, 450));
        var left = Add(root, UiControlKind.Container, "", new() { Width = UiLength.Stretch(), MaxWidth = 260, Height = UiLength.Stretch(), Padding = 12, Gap = 8 });
        Add(left, UiControlKind.Label, "N2 retained layout kernel", new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(28), HitTestVisible = false });
        var button = Add(left, UiControlKind.Button, "Apply settings", new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(36) });
        var toggle = Add(left, UiControlKind.Toggle, "Enable preview", new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(36) });
        var slider = Add(left, UiControlKind.Slider, "Preview intensity", new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(42) });
        store.SetValue(slider, .35f);
        var right = Add(root, UiControlKind.Container, "", new() { Width = UiLength.Stretch(), Height = UiLength.Stretch(), Padding = 12, Gap = 6 });
        Add(right, UiControlKind.Label, "Scrollable panel / cached clipping", new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(30), HitTestVisible = false });
        for (int i = 0; i < 20; i++) Add(right, UiControlKind.Button, $"Property {i:D2}", new() { Width = UiLength.Stretch(), Height = UiLength.Fixed(30) });
        var badge = Add(root, UiControlKind.Label, "Absolute overlay", new() { Position = UiPosition.Absolute,
            Left = 620, Top = 0, Width = UiLength.Fixed(148), Height = UiLength.Fixed(24), HitTestVisible = false });
        var firstLayout = store.Layout();
        if (store.GetBounds(left).Width != 260 || store.GetBounds(right).Width != 492
            || store.GetBounds(badge).X != 636 || store.GetBounds(badge).Y != 16)
            throw new InvalidOperationException("Independent stretch/absolute rectangle oracle failed.");
        if (store.Layout() != default) throw new InvalidOperationException("Idle layout performed work.");
        var buttonBounds = store.GetBounds(button);
        store.PointerDown(buttonBounds.X + 5, buttonBounds.Y + 5);
        store.Acknowledge(button, UiInvalidation.Paint);
        store.SetCapture(toggle);
        if (!store.GetInvalidation(button).HasFlag(UiInvalidation.Paint))
            throw new InvalidOperationException("Capture transfer did not invalidate pressed visual.");
        store.SetCapture(button);
        if (store.IsPressed(button) || store.PointerUp(buttonBounds.X + 5, buttonBounds.Y + 5).Kind != UiActionKind.None)
            throw new InvalidOperationException("Capture transfer revived a canceled gesture.");
        store.SetCapture(default); store.SetFocus(default);
        var results = new List<object>();
        var font = new FontFile(Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf"));
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            int width = (int)(800 * scale), height = (int)(450 * scale);
            var renderer = new PaperRenderer(); renderer.Initialize(width, height);
            var canvas = new Canvas(new AtlasOwner(renderer), new FontAtlasSettings());
            try
            {
            byte[] Draw(string file)
            {
                using var target = new RenderTexture(width, height, true, [TextureImageFormat.Color4b]);
                renderer.SetRenderTarget(target.frameBuffer);
                try
                {
                    canvas.BeginFrame(800, 450, scale);
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        var node = nodes[i]; var bounds = store.GetBounds(node); var clip = store.GetClip(node);
                        if (clip.Width <= 0 || clip.Height <= 0) continue;
                        canvas.Scissor(clip.X, clip.Y, clip.Width, clip.Height);
                        var kind = store.GetControlKind(node);
                        var color = kind == UiControlKind.Container ? new Color32(30, 34, 42, 255) : new Color32(51, 59, 72, 255);
                        if (kind == UiControlKind.Toggle && store.GetValue(node) > 0) color = new Color32(41, 104, 92, 255);
                        if (store.Focused == node) color = new Color32(50, 89, 137, 255);
                        canvas.RectFilled(bounds.X, bounds.Y, bounds.Width, bounds.Height, color);
                        canvas.DrawText(labels[node], bounds.X + 8, bounds.Y + 6, new Color32(230, 233, 239, 255), 14, font);
                        if (kind == UiControlKind.Slider)
                            canvas.RectFilled(bounds.X + 6, bounds.Y + bounds.Height - 9, (bounds.Width - 12) * store.GetValue(node), 4, new Color32(92, 182, 237, 255));
                    }
                    canvas.ResetScissor();
                    using (var clear = Graphics.GetCommandBuffer("N2 fixture clear"))
                    {
                        clear.SetRenderTarget(target.frameBuffer); clear.SetViewport(0, 0, (uint)width, (uint)height);
                        clear.DisableScissor(); clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f));
                        Graphics.Submit(clear);
                    }
                    canvas.Render();
                    using (var flush = Graphics.GetCommandBuffer("N2 fixture readback")) Graphics.SubmitAndWait(flush);
                    var bytes = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(bytes.AsMemory());
                    using var picture = new ImageMagick.MagickImage();
                    picture.ReadPixels(bytes, new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
                    picture.Write(Path.Combine(destination, file), ImageMagick.MagickFormat.Png32);
                    return bytes;
                }
                finally { renderer.SetRenderTarget(null); }
            }
            store.SetScroll(right, 0, 0); store.SetValue(toggle, 0); store.SetFocus(default); store.Layout();
            Draw($"dpi-{scale}-initial.png");
            var bounds = store.GetBounds(toggle);
            store.PointerDown(bounds.X + 10, bounds.Y + 10);
            var action = store.PointerUp(bounds.X + 10, bounds.Y + 10);
            if (action.Kind != UiActionKind.ValueChanged || action.Value != 1) throw new InvalidOperationException("Toggle replay failed.");
            store.SetScroll(right, 0, 47);
            store.SetFocus(button);
            var changed = store.Layout();
            byte[] incremental = Draw($"dpi-{scale}-incremental.png");
            var full = store.Layout(forceFull: true);
            byte[] reference = Draw($"dpi-{scale}-full.png");
            if (!incremental.AsSpan().SequenceEqual(reference)) throw new InvalidOperationException("Incremental/full pixel mismatch.");
            results.Add(new { Scale = scale, PixelsEqual = true, FirstLayout = firstLayout, Changed = changed, Reference = full, Idle = store.Layout() });
            }
            finally { canvas.Dispose(); }
        }
        File.WriteAllText(Path.Combine(destination, "validation.json"), System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("N2 kernel fixture: four DPI pixel comparisons and control replay passed.");
    }

    private sealed class AtlasOwner(ICanvasRenderer renderer) : ICanvasRenderer
    {
        private readonly List<IDisposable> _textures = new();
        public object CreateTexture(uint width, uint height)
        {
            object texture = renderer.CreateTexture(width, height);
            if (texture is IDisposable disposable) _textures.Add(disposable);
            return texture;
        }
        public Int2 GetTextureSize(object texture) => renderer.GetTextureSize(texture);
        public void SetTextureData(object texture, IntRect bounds, byte[] data) => renderer.SetTextureData(texture, bounds, data);
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) => renderer.RenderCalls(canvas, calls);
        public bool SupportsBackdropBlur => renderer.SupportsBackdropBlur;
        public void Dispose() { foreach (var texture in _textures) texture.Dispose(); _textures.Clear(); renderer.Dispose(); }
    }
}
