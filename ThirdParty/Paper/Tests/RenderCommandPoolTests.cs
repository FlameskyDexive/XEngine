// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

namespace Tests;

/// <summary>
/// Contract tests for the pooled custom-draw path (RenderCommandListPool): draws registered on
/// rebuilt element slots fire exactly once per frame, and stale registrations from the previous
/// frame never leak into the next one. (The bytes-level win is measured by the memory soak, not
/// here — whole-frame probes pick up unrelated sources.)
/// </summary>
public class RenderCommandPoolTests
{
    [Fact]
    public void PooledCommands_FireExactlyOncePerFrame_AndDoNotLeakAcrossFrames()
    {
        var paper = NewPaper();
        int fired = 0;
        var firedPerFrame = new List<int>();

        for (int frame = 0; frame < 5; frame++)
        {
            int firedAtEntry = fired;
            paper.BeginFrame(0.016f);
            using (var box = paper.Box("d"))
            {
                var handle = box._handle;
                paper.Draw(ref handle, (_canvas, _rect) => fired++);
            }
            paper.EndFrame();
            firedPerFrame.Add(fired - firedAtEntry);
        }

        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, firedPerFrame);
    }

    [Fact]
    public void MultipleDraws_OnOneElement_AllFireInRegistrationOrder()
    {
        var paper = NewPaper();
        var order = new List<int>();

        paper.BeginFrame(0.016f);
        using (var box = paper.Box("multi"))
        {
            var handle = box._handle;
            paper.Draw(ref handle, (_c, _r) => order.Add(1));
            paper.Draw(ref handle, (_c, _r) => order.Add(2));
            paper.Draw(ref handle, (_c, _r) => order.Add(3));
        }
        paper.EndFrame();

        Assert.Equal(new[] { 1, 2, 3 }, order);

        // Next rebuild resets: fresh frame sees an empty command slate before re-registering.
        order.Clear();
        paper.BeginFrame(0.016f);
        using (var box = paper.Box("multi"))
        {
            // no Draw this frame on purpose
        }
        paper.EndFrame();

        Assert.Empty(order);
    }

    private static Paper NewPaper() => new(new Renderer(), 200, 200, new FontAtlasSettings());

    private class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
