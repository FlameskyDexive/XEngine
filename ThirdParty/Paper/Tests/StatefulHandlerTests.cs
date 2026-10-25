// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using XEngine.PaperUI;
using XEngine.PaperUI.Events;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;

namespace Tests;

/// <summary>
/// Contract tests for the stateful event bridges backing On*<T>(capturedValue, handler):
/// a click carries the state captured in its own build pass, stale subscriptions disappear on
/// rebuild without resubscription, and the typed pipe allocates no more per frame than the
/// equivalent plain-delegate pipe (calibrated against a shared static noop handler).
/// </summary>
public class StatefulHandlerTests
{
    private static readonly Action<ClickEvent> s_noopPlain = static _ => { };

    [Fact]
    public void StatefulChannel_MatchesPlainChannel_UnderIdenticalPointerSequences()
    {
        // Identical build + pointer script run against a plain-delegate element and a
        // stateful <T> element; whichever way Paper synthesizes presses/clicks in headless
        // mode, both subscription channels must observe exactly the same events.
        int[] results = { -1, -1 };

        void Script(int channel, float jiggle)
        {
            var paper = NewPaper();
            paper.BeginFrame(0.016f);
            using (paper.Box("btn").Width(UnitValue.Pixels(60)).Height(UnitValue.Pixels(20))
                       .Position(UnitValue.Pixels(10), UnitValue.Pixels(10)))
            {
                if (channel == 0)
                    paper.Box("hit").Width(60).Height(20).Position(10, 10)
                         .OnClick(e => results[0] = 7);
                else
                    paper.Box("hit").Width(60).Height(20).Position(10, 10)
                         .OnClick(7, (v, _) => results[1] = v);
            }
            paper.SetPointerState(PaperMouseBtn.Unknown, 15f + jiggle, 15f, false, true);
            paper.SetPointerState(PaperMouseBtn.Left, 20f + jiggle, 20f, true, false);
            paper.EndFrame();

            paper.BeginFrame(0.016f);
            using (paper.Box("btn").Width(UnitValue.Pixels(60)).Height(UnitValue.Pixels(20))
                       .Position(UnitValue.Pixels(10), UnitValue.Pixels(10)))
            {
                if (channel == 0)
                    paper.Box("hit").Width(60).Height(20).Position(10, 10)
                         .OnClick(e => results[0] = 7);
                else
                    paper.Box("hit").Width(60).Height(20).Position(10, 10)
                         .OnClick(7, (v, _) => results[1] = v);
            }
            paper.SetPointerState(PaperMouseBtn.Left, 21f + jiggle, 20f, false, true);
            paper.EndFrame();
        }

        Script(0, 0f);
        int plainResult = results[0];
        Script(1, 3f);
        Assert.Equal(plainResult, results[1]);
    }

    [Fact]
    public void ReusedSlotWithoutResubscription_StopsReceiving()
    {
        var paper = NewPaper();
        int calls = 0;

        paper.BeginFrame(0.016f);
        using (paper.Box("t").Width(UnitValue.Pixels(50)).Height(UnitValue.Pixels(20))
                   .Position(UnitValue.Pixels(5), UnitValue.Pixels(5)))
        {
            paper.Box("hit").Width(UnitValue.Pixels(50)).Height(UnitValue.Pixels(20))
                 .Position(UnitValue.Pixels(5), UnitValue.Pixels(5))
                 .OnClick(1, (_, _) => calls++);
        }
        paper.EndFrame();

        // Rebuild WITHOUT any subscription: click must not fire.
        paper.BeginFrame(0.016f);
        using (paper.Box("t").Width(UnitValue.Pixels(50)).Height(UnitValue.Pixels(20))
                   .Position(UnitValue.Pixels(5), UnitValue.Pixels(5)))
        {
        }
        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, true, false);
        paper.SetPointerState(PaperMouseBtn.Left, 10, 10, false, false);
        paper.EndFrame();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void TypedPipe_NoHeavierThanPlainPipe_InSteadyState()
    {
        var paper = NewPaper();

        void PlainFrame()
        {
            paper.BeginFrame(0.016f);
            using (paper.Box("p"))
                paper.Box("row").OnClick(s_noopPlain);
            paper.EndFrame();
        }

        int captured = 3;
        Action<ClickEvent> typed = e => { captured = 4; };
        void TypedFrame()
        {
            paper.BeginFrame(0.016f);
            using (paper.Box("t"))
                paper.Box("row").OnClick(7, (v, _e) => { }); // stateful pipe
            paper.EndFrame();
        }

        long Measure(Action frame, int warmups)
        {
            for (int i = 0; i < warmups; i++) frame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            frame();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long plainDelta = Measure(PlainFrame, 2);
        long typedDelta = Measure(TypedFrame, 2);

        Assert.True(typedDelta <= plainDelta,
            $"typed pipe ({typedDelta}B/frame) heavier than plain pipe ({plainDelta}B/frame)");
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
