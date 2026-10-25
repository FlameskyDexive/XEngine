// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;

namespace Tests;

/// <summary>
/// Contract tests for the typed style WRITE path: the fluent setters now route through
/// SetStyleUnit/SetStyleFloat/SetStyleInt instead of boxing every UnitValue/float/int into the
/// object API. Verifies (a) values still land in layout, (b) the typed pipe is behaviorally
/// identical to the legacy boxed pipe, and (c) repeated setter calls allocate zero bytes.
/// </summary>
public class StyleTypedWriteTests
{
    [Fact]
    public void TypedUnitWrite_ReachesLayout()
    {
        var paper = NewPaper();
        ElementHandle handle = default;
        paper.BeginFrame(0.016f);
        using (var box = paper.Box("t")
                   .Width(UnitValue.Pixels(42))
                   .Height(UnitValue.Pixels(10)))
        {
            handle = box._handle;
        }
        paper.EndFrame();

        Assert.Equal(42f, handle.Data.LayoutRect.Size.X, 0.5f);
        Assert.Equal(10f, handle.Data.LayoutRect.Size.Y, 0.5f);
    }

    [Fact]
    public void TypedPipe_MatchesBoxedPipe_GeometryForAllValueKinds()
    {
        var paper = NewPaper();
        ElementHandle typed = default, boxed = default;
        paper.BeginFrame(0.016f);
        {
            // Typed pipes: unit + float (aspect ratio ties height to width).
            var t = paper.Row("typed").Width(UnitValue.Pixels(60)).Margin(5).AspectRatio(0.5f);
            typed = t._handle;
            // Boxed pipe writes of identical values on a sibling.
            var b = paper.Row("boxed");
            b.SetStyleProperty(GuiProp.Width, UnitValue.Pixels(60));
            b.SetStyleProperty(GuiProp.AspectRatio, 0.5f);
            boxed = b._handle;
        }
        paper.EndFrame();

        Assert.Equal(typed.Data.LayoutRect.Size.X, boxed.Data.LayoutRect.Size.X, 0.5f);
        Assert.Equal(typed.Data.LayoutRect.Size.Y, boxed.Data.LayoutRect.Size.Y, 0.5f);
        Assert.Equal(60f, typed.Data.LayoutRect.Size.X, 0.5f);
    }

    [Fact]
    public void RepeatedTypedSetterCalls_AllocateZeroBytes()
    {
        var paper = NewPaper();
        paper.BeginFrame(0.016f);
        var unitBuilder = paper.Box("unit");
        var floatBuilder = paper.Box("float");
        paper.EndFrame();

        // Warm JIT so first-call artifacts don't pollute the probe window.
        void Pump()
        {
            for (int i = 0; i < 100; i++)
            {
                unitBuilder.Width(UnitValue.Pixels(20)).Height(UnitValue.Pixels(12))
                    .MinWidth(UnitValue.Pixels(8)).MaxHeight(UnitValue.Pixels(64))
                    .Position(UnitValue.Pixels(1), UnitValue.Pixels(2))
                    .Padding(1, 1, 1, 1);
                floatBuilder.BorderWidth(1f).BackdropBlur(0f).AspectRatio(1f)
                    .WordSpacing(0.5f).LetterSpacing(0.25f).LineHeight(1.2f).FontSize(13f)
                    .TabSize(4);
            }
        }

        Pump();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Pump();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    private static Paper NewPaper() => new(new Renderer(), 400, 400, new FontAtlasSettings());

    private class Renderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
