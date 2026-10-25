// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// <see cref="Paper.HasPendingButtonEdge"/> is the bridge that keeps a click's release frame
/// scheduled: input edges are fed during Update (one loop iteration after the mouse event
/// invalidated), so on-demand hosts re-queue a repaint while the edge is still unconsumed.
/// These tests pin the flag's lifecycle: set on a real transition, surviving across frames
/// that do not render, and cleared exactly when a rendered frame consumes it.
/// <para>
/// <see cref="Paper.NeedsFollowUpFrame"/> is the next hop: widgets apply click mutations after
/// that frame's layout, so the host must paint one more time (tree expand, foldout, toggle)
/// or the change waits for an unrelated repaint — usually the next mouse move.
/// </para>
/// </summary>
public sealed class PaperButtonEdgePendingTests
{
    [Fact]
    public void PressSetsPendingEdge_FrameConsumesIt()
    {
        var paper = NewPaper();
        Assert.False(paper.HasPendingButtonEdge);
        Assert.False(paper.NeedsFollowUpFrame);

        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, true, false);
        Assert.True(paper.HasPendingButtonEdge);

        RenderFrame(paper);
        Assert.False(paper.HasPendingButtonEdge);
        Assert.True(paper.NeedsFollowUpFrame);
    }

    [Fact]
    public void FollowUpFrameClearsAfterIdleRender()
    {
        var paper = NewPaper();
        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, true, false);
        RenderFrame(paper);
        Assert.True(paper.NeedsFollowUpFrame);

        // No new button edge: the follow-up paint must not stay armed forever.
        RenderFrame(paper);
        Assert.False(paper.NeedsFollowUpFrame);
        Assert.False(paper.HasPendingButtonEdge);
    }

    [Fact]
    public void ReleaseWithoutRender_StaysPendingAcrossFeeds()
    {
        var paper = NewPaper();

        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, true, false);
        RenderFrame(paper);
        Assert.False(paper.HasPendingButtonEdge);

        // Release fed while no frame renders (the click-hang bug): the pending edge must
        // survive arbitrary further feeds so the next scheduled frame still carries it.
        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, false, false);
        paper.SetPointerState(PaperMouseBtn.Unknown, 12, 12, false, true);
        paper.SetPointerState(PaperMouseBtn.Unknown, 14, 14, false, true);
        Assert.True(paper.HasPendingButtonEdge);

        RenderFrame(paper);
        Assert.False(paper.HasPendingButtonEdge);
        Assert.True(paper.NeedsFollowUpFrame);
    }

    [Fact]
    public void RepeatedSameState_DoesNotReArmAfterConsumption()
    {
        var paper = NewPaper();

        paper.SetPointerState(PaperMouseBtn.Right, 5, 5, true, false);
        RenderFrame(paper);

        // Hosts clear presses every tick while the pointer is over another surface; the
        // repeated up-feed must not look like a fresh edge after the frame consumed it.
        paper.SetPointerState(PaperMouseBtn.Right, 5, 5, false, false);
        Assert.True(paper.HasPendingButtonEdge);
        RenderFrame(paper);

        paper.SetPointerState(PaperMouseBtn.Right, 5, 5, false, false);
        paper.SetPointerState(PaperMouseBtn.Right, 5, 5, false, false);
        Assert.False(paper.HasPendingButtonEdge);
    }

    [Fact]
    public void MiddleButtonEdge_AndClearInput_Reset()
    {
        var paper = NewPaper();

        paper.SetPointerState(PaperMouseBtn.Middle, 1, 1, true, false);
        Assert.True(paper.HasPendingButtonEdge);

        paper.ClearInput();
        Assert.False(paper.HasPendingButtonEdge);
        Assert.False(paper.NeedsFollowUpFrame);
    }

    private static Paper NewPaper() => new(new NullRenderer(), 64, 64, new FontAtlasSettings());

    private static void RenderFrame(Paper paper)
    {
        paper.BeginFrame(0.016f);
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
