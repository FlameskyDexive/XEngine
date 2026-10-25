// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

namespace Tests;

public class FollowUpFrameTests
{
    [Fact]
    public void ConsumedPress_SetsNeedsFollowUpFrame()
    {
        var paper = NewPaper();
        Assert.False(paper.NeedsFollowUpFrame);

        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, true, false);
        RenderFrame(paper);
        Assert.True(paper.NeedsFollowUpFrame);

        RenderFrame(paper);
        Assert.False(paper.NeedsFollowUpFrame);
    }

    [Fact]
    public void ClearInput_ResetsNeedsFollowUpFrame()
    {
        var paper = NewPaper();
        paper.SetPointerState(PaperMouseBtn.Middle, 1, 1, true, false);
        RenderFrame(paper);
        Assert.True(paper.NeedsFollowUpFrame);

        paper.ClearInput();
        Assert.False(paper.NeedsFollowUpFrame);
        Assert.False(paper.HasPendingButtonEdge);
    }

    private static Paper NewPaper() => new(new Renderer(), 64, 64, new FontAtlasSettings());

    private static void RenderFrame(Paper paper)
    {
        paper.BeginFrame(0.016f);
        paper.EndFrame();
    }

    private class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
