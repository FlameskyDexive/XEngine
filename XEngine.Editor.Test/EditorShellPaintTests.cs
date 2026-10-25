using System.Collections.Generic;
using System.IO;
using System.Reflection;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime.GUI;
using XEngine.Scribe;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Main-window chrome is a compared subtree. Headless papers have no render layers, so
/// <see cref="EditorShellPaint"/> does not create a region; the observer sees the same visual
/// inputs the live region would. Status text must settle, then re-key when the message changes.
/// </summary>
public sealed class EditorShellPaintTests
{
    private const float Step = 1f / 60f;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Fact]
    public void AttachIsNoopWithoutRenderLayers()
    {
        var flag = typeof(Program).GetProperty(nameof(Program.ShellPaintIncremental), Any)!;
        bool previous = (bool)flag.GetValue(null)!;
        var paper = NewPaper();
        var paint = new EditorShellPaint();
        try
        {
            flag.SetValue(null, true);
            paper.BeginFrame(Step);
            using (paper.Box("header_menu").Size(200, 24).Enter())
                paint.AttachMenu(paper);
            paper.EndFrame();
            Assert.Equal(-1, paint.MenuCaptures);
            Assert.Equal(-1, paint.ToolbarCaptures);
            Assert.Equal(-1, paint.StatusCaptures);
        }
        finally
        {
            flag.SetValue(null, previous);
            paint.Dispose();
            paper.Canvas.Dispose();
        }
    }

    [Fact]
    public void StatusChromeSettlesAndRekeysOnMessage()
    {
        var paper = NewPaper();
        var observer = new Observer();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var font = new FontFile(Path.Combine(root, "XEngine.Editor", "Resources", "Geist-Regular.ttf"));
        string message = "ready";
        try
        {
            Frame(); Assert.True(observer.Changed);
            Frame(); Assert.False(observer.Changed);
            Frame(); Assert.False(observer.Changed);

            message = "error: missing mesh";
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
            using (paper.Row("statusbar").Size(640, 22).Enter())
            {
                paper.CurrentParent.Data.SubtreePainter = observer;
                paper.Box("sb_log_m").Width(400).Height(22).Text(message, font);
                paper.Box("sb_sel").Width(40).Height(22).Text("0", font);
            }
            paper.EndFrame();
        }
    }

    private sealed class Observer : IPaperSubtreePainter
    {
        public readonly PaperSubtreeVisualState State = new();
        public bool Changed;
        public bool TryPaint(Paper paper, ElementHandle root, Canvas host)
        { Changed = State.HasChanged(paper, root); return false; }
    }

    private static Paper NewPaper() => new(new Renderer(), 640, 80, new FontAtlasSettings { AtlasSize = 32 });
    private sealed class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> calls) { }
    }
}
