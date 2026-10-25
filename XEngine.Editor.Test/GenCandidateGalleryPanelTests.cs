using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>★ Gen-P4.1 — headless gallery panel does not throw.</summary>
public sealed class GenCandidateGalleryPanelTests : EditorTestHarness
{
    [Fact]
    public void OnGUI_HeadlessPaper_DoesNotThrow()
    {
        var paper = new Paper(new NullRenderer(), 900, 480, new FontAtlasSettings());
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        new GenCandidateGalleryPanel().OnGUI(paper, 900, 480);
        Origami.EndFrame(paper);
        paper.EndFrame();
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
