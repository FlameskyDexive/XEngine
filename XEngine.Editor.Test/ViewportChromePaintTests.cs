using System.Collections.Generic;
using System.IO;
using System.Reflection;

using XEngine.Editor;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Scribe;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Scene / Game chrome is a compared subtree. Headless papers have no render layers, so
/// the live region does not attach; the observer sees the same visual inputs. Toolbar
/// text must settle, then re-key when a control label changes.
/// </summary>
public sealed class ViewportChromePaintTests
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void SceneToolbarSettlesAndRekeysOnDrawMode()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        string label = "Shaded";
        try
        {
            Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            Frame(); Assert.False(observer.Changed);

            label = "Wireframe";
            Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
        }
        finally
        {
            observer.State.Dispose();
            paper.Canvas.Dispose();
        }

        void Frame()
        {
            paper.BeginFrame(Step);
            using (paper.Row("sv_tb").Size(720, 26).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                paper.Box("sv_tb_drawmode").Width(92).Height(20).Text(label, font);
            }
            paper.EndFrame();
        }
    }

    [Fact]
    public void SceneAndGameFlagsDefaultOff()
    {
        Assert.False(Program.ScenePaintIncremental);
        Assert.False(Program.GamePaintIncremental);
        var scene = typeof(Program).GetProperty(nameof(Program.ScenePaintIncremental), Any)!;
        var game = typeof(Program).GetProperty(nameof(Program.GamePaintIncremental), Any)!;
        Assert.False((bool)scene.GetValue(null)!);
        Assert.False((bool)game.GetValue(null)!);
    }

    private sealed class Observer : IPaperSubtreePainter
    {
        public readonly PaperSubtreeVisualState State = new();
        public bool Changed;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        { Changed = State.HasChanged(paper, root); return false; }
    }

    private static Paper NewPaper() => new(new Renderer(), 720, 80, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => texture is Int2 size ? size : new Int2(16, 16);
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
