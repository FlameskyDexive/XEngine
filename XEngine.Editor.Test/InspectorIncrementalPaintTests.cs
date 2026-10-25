using System.Linq;
using System.Reflection;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.PropertyEditors;
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

/// <summary>
/// The Inspector's property scroll body is a compared subtree. Under
/// <c>--inspector-paint-incremental</c> Origami checkboxes / buttons / sliders declare exact
/// paint keys (animation <c>t</c>, colours, label) so a settled GameObject inspector stops
/// reporting visual change, and must re-key when the selection, a transform value or a
/// component enabled flag changes. The panel is built headlessly under a comparing observer
/// (no render layers, so no real region); the observer sees every visual input the region would.
/// </summary>
public sealed class InspectorIncrementalPaintTests : EditorTestHarness
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void InspectorScrollSettlesAndRekeysOnSelectionTransformAndEnabled()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        var theme = OrigamiTheme.CreateDefaults();
        theme.Font = font;
        using var themeScope = Origami.PushTheme(theme);
        var flag = typeof(Program).GetProperty(nameof(Program.InspectorPaintIncremental), Any)!;
        bool previousFlag = (bool)flag.GetValue(null)!;
        var previousFont = EditorTheme.DefaultFont;
        var previousSelection = Selection.Selected.ToArray();
        var panel = new InspectorPanel();
        using var go = new GameObject("InspectorPaintTest");
        using var other = new GameObject("InspectorPaintOther");
        try
        {
            EditorTheme.DefaultFont = font;
            EnsureHostPropertyEditors();
            flag.SetValue(null, true);
            Selection.Select(go);

            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();               // checkbox / button hover-t settle
            Frame(); Assert.False(observer.Changed);
            Frame(); Assert.False(observer.Changed);

            go.Transform.LocalPosition = new Float3(1f, 2f, 3f);
            Frame(); Assert.True(observer.Changed);
            Frame(); Frame(); Assert.False(observer.Changed);

            go.Enabled = false;                                 // header checkbox + title colour
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();               // checkbox fill animation
            Frame(); Assert.False(observer.Changed);

            go.AddComponent<Camera>();                          // CameraEditor + LayerMask chips (culling mask)
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();
            Frame(); Assert.False(observer.Changed);

            Selection.Select(other);
            Frame(); Assert.True(observer.Changed);
            for (int i = 0; i < 12; i++) Frame();
            Frame(); Assert.False(observer.Changed);

            // Clearing global selection must invalidate the Inspector and its remembered object.
            Selection.Clear();
            Frame(); Assert.True(observer.Changed);             // empty-state label
            Frame(); Frame(); Assert.False(observer.Changed);
        }
        finally
        {
            flag.SetValue(null, previousFlag);
            EditorTheme.DefaultFont = previousFont;
            Selection.Clear();
            foreach (var o in previousSelection) Selection.AddToSelection(o);
            panel.OnClosed();
            observer.State.Dispose();
            paper.Canvas.Dispose();
        }

        void Frame()
        {
            paper.BeginFrame(Step); Origami.BeginFrame(paper, Step);
            using (paper.Box("host").Size(420, 720).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                panel.OnGUI(paper, 420, 720);
            }
            Origami.EndFrame(paper); paper.EndFrame();
        }
    }

    private sealed class Observer : IPaperSubtreePainter
    {
        public readonly PaperSubtreeVisualState State = new();
        public bool Changed;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        { Changed = State.HasChanged(paper, root); return false; }
    }

    /// <summary>
    /// Headless tests skip <see cref="EditorApplication"/>, so the property-grid host fallback
    /// (LayerMask chips, object fields) is missing unless we install it. Camera's default
    /// culling mask is Everything — five named layers become five versioned chip-X paints.
    /// </summary>
    private static void EnsureHostPropertyEditors()
    {
        EditorRegistries.Initialize();
        var prop = typeof(EditorApplication).GetProperty(nameof(EditorApplication.PropertyGridConfig), Any)!;
        var cfg = (PropertyGridConfig?)prop.GetValue(null);
        if (cfg == null)
        {
            cfg = new PropertyGridConfig();
            BuiltInFieldDrawers.Register(cfg.Drawers);
            prop.SetValue(null, cfg);
        }
        cfg.FallbackFieldDrawer ??= (paper, id, label, fieldType, value, onChange, depth) =>
        {
            if (typeof(EngineObject).IsAssignableFrom(fieldType))
                EngineObjectPropertyEditor.SetFieldType(fieldType);
            var editor = EditorRegistries.GetPropertyEditor(fieldType);
            if (editor == null) return false;
            editor.OnGUI(paper, id, label, value, onChange, depth);
            return true;
        };
        cfg.IsSimpleFieldType ??= fieldType => EditorRegistries.GetPropertyEditor(fieldType) != null;
    }

    private static Paper NewPaper() => new(new Renderer(), 420, 720, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
