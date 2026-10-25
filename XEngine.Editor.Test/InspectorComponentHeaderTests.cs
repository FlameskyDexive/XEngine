using System.Reflection;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class InspectorComponentHeaderTests
{
    [Theory]
    [InlineData(240f)]
    [InlineData(335f)]
    [InlineData(520f)]
    public void SingleComponentToggleFoldAndMenuHaveIndependentTargets(float width)
    {
        using var f = new Fixture(width);
        var go = new GameObject("ModelBody");
        var renderer = go.AddComponent<SkinnedMeshRenderer>();
        f.Scene.Add(go); Selection.Select(go); f.Frame(); f.Frame();
        Assert.Same(renderer, Undo.FindComponent(renderer.Identifier));
        var title = f.Text("Skinned Mesh Renderer");
        Assert.Equal(22, title.LayoutHeight);
        var checkbox = f.HeaderCheckbox(title);
        Assert.True(checkbox.X < title.X);
        Assert.InRange(checkbox.LayoutHeight, 14, 22);
        var menu = f.Text(EditorIcons.EllipsisVertical, title.Y);
        Assert.Equal(22, menu.LayoutHeight);
        Assert.True(title.X + title.LayoutWidth <= menu.X);

        f.Click(checkbox.X + checkbox.LayoutWidth / 2, checkbox.Y + checkbox.LayoutHeight / 2);
        Assert.False(renderer.Enabled); Assert.True(go.Enabled);
        Assert.Same(go, Selection.ActiveObject); Assert.True(f.HasText("Mesh"));
        Undo.PerformUndo(); f.Frame(); Assert.True(renderer.Enabled);
        Undo.PerformRedo(); f.Frame(); Assert.False(renderer.Enabled);

        f.Click(title.X + 20, title.Y + 11);
        Assert.False(f.HasText("Mesh")); Assert.Same(go, Selection.ActiveObject);
        f.Click(title.X + 20, title.Y + 11); Assert.True(f.HasText("Mesh"));
        f.Click(menu.X + 10, menu.Y + 11);
        Assert.True(ContextMenu.IsOpen); Assert.True(f.HasText("Mesh"));
        ContextMenu.Close();
    }

    [Fact]
    public void MixedComponentCheckboxEditsAllAndUndoRestoresEachValueWithoutFolding()
    {
        using var f = new Fixture(335);
        var first = new GameObject("FirstModelBody");
        var second = new GameObject("SecondModelBody");
        var a = first.AddComponent<SkinnedMeshRenderer>();
        var b = second.AddComponent<SkinnedMeshRenderer>(); b.Enabled = false;
        f.Scene.Add(first); f.Scene.Add(second);
        Selection.Select(first); Selection.AddToSelection(second); f.Frame(); f.Frame();
        var title = f.Text("Skinned Mesh Renderer"); var checkbox = f.HeaderCheckbox(title);
        Assert.Equal(22, title.LayoutHeight);
        f.Click(checkbox.X + 7, checkbox.Y + 9);
        Assert.True(a.Enabled); Assert.True(b.Enabled); Assert.True(f.HasText("Mesh"));
        Assert.Equal(2, Selection.Count);
        Undo.PerformUndo(); f.Frame(); Assert.True(a.Enabled); Assert.False(b.Enabled);
        Undo.PerformRedo(); f.Frame(); Assert.True(a.Enabled); Assert.True(b.Enabled);
        f.Click(title.X + 20, title.Y + 11); Assert.False(f.HasText("Mesh"));
        f.Click(title.X + 20, title.Y + 11); Assert.True(f.HasText("Mesh"));
    }

    [Fact]
    public void LongComponentTitleReservesActionsAndStillStartsComponentDrag()
    {
        using var f = new Fixture(240);
        var go = new GameObject("Long title body");
        var component = go.AddComponent<ComponentWithAnExceptionallyLongInspectorHeaderTitle>();
        f.Scene.Add(go); Selection.Select(go); f.Frame(); f.Frame();
        var title = f.Text(PropertyGridUtils.NicifyName(component.GetType().Name));
        var preset = f.Text(EditorIcons.Sliders, title.Y);
        Assert.InRange(title.LayoutWidth, 1, 180);
        Assert.True(title.X + title.LayoutWidth <= preset.X);
        float x = title.X + 10, y = title.Y + 11;
        f.Paper.SetPointerPosition(x, y); f.Frame();
        f.Frame(() => f.Paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
        f.Frame(() => f.Paper.SetPointerPosition(x + 20, y + 2));
        f.Frame();
        var payload = Assert.IsType<ComponentDragPayload>(DragDrop.Payload);
        Assert.Same(component, payload.Component); Assert.Same(go, payload.GameObject);
    }

    public sealed class ComponentWithAnExceptionallyLongInspectorHeaderTitle : MonoBehaviour { }

    internal sealed class Fixture : IDisposable
    {
        private readonly Scene? _previousScene = Scene.Current;
        private readonly FontFile? _previousFont = EditorTheme.DefaultFont;
        private readonly bool _previousEditor = Application.IsEditor;
        private readonly IDisposable _theme;
        private readonly IDisposable _surface;
        private readonly PropertyGridConfig? _previousGridConfig = EditorApplication.PropertyGridConfig;
        private readonly float _width;
        private readonly InspectorPanel _panel = new();
        internal InspectorPanel Panel => _panel;
        internal readonly Scene Scene = new();
        private readonly Paper _paper;
        internal Paper Paper => _paper;
        internal bool FlushUndoBeforeDraw;
        internal Fixture(float width)
        {
            EditorRegistries.Initialize();
            _width = width;
            _paper = new Paper(new Renderer(), width, 900, new FontAtlasSettings { AtlasSize = 32 });
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            EditorTheme.DefaultFont = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
            typeof(Scene).GetProperty(nameof(Scene.Current))!.SetValue(null, Scene);
            Application.IsEditor = true; Selection.Clear(); Undo.Clear();
            typeof(EditorApplication).GetProperty(nameof(EditorApplication.PropertyGridConfig), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(null, new PropertyGridConfig());
            _theme = Origami.PushTheme(EditorTheme.BuildOrigamiTheme());
            _surface = Origami.PushOffscreenSurface();
        }
        internal void Frame(Action? input = null)
        {
            _paper.BeginFrame(1f / 60);
            if (FlushUndoBeforeDraw) Undo.FlushFrame();
            Origami.BeginFrame(_paper, 1f / 60); input?.Invoke();
            using (_paper.Column("host").Size(_width, 900).Enter()) _panel.OnGUI(_paper, _width, 900);
            Origami.EndFrame(_paper); _paper.EndFrame();
            Undo.FlushFrame();
        }
        internal List<ElementData> Elements()
        {
            var result = new List<ElementData>();
            for (int i = 0; i < _paper.ElementCount; i++) result.Add(_paper.GetElementData(i));
            return result;
        }
        internal ElementData Text(string text, float? y = null) => Elements().Single(e => e.Paragraph == text && (!y.HasValue || MathF.Abs(e.Y - y.Value) < .1f));
        internal bool HasText(string text) => Elements().Exists(e => e.Paragraph == text);
        internal ElementData HeaderCheckbox(ElementData title) => Elements().Single(e => e.OnClick != null
            && e.X < title.X && e.LayoutWidth <= 20 && e.Y >= title.Y && e.Y + e.LayoutHeight <= title.Y + 22);
        internal void Click(float x, float y)
        {
            _paper.SetPointerPosition(x, y); Frame();
            Frame(() => _paper.SetPointerState(PaperMouseBtn.Left, x, y, true, false));
            Frame(() => _paper.SetPointerState(PaperMouseBtn.Left, x, y, false, false)); Frame();
        }
        public void Dispose()
        {
            ContextMenu.Close(); DragDrop.Cancel(); _panel.OnClosed(); Selection.Clear(); Undo.Clear();
            typeof(Scene).GetProperty(nameof(Scene.Current))!.SetValue(null, _previousScene);
            Scene.Dispose(); _paper.Canvas.Dispose(); _surface.Dispose(); _theme.Dispose();
            EditorTheme.DefaultFont = _previousFont; Application.IsEditor = _previousEditor;
            typeof(EditorApplication).GetProperty(nameof(EditorApplication.PropertyGridConfig), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(null, _previousGridConfig);
        }
    }
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
