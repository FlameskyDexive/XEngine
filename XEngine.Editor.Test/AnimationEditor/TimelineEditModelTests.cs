// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Editor.AnimationEditor;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>M23: Timeline window time↔screen, trim/move, and signal hit-testing math.</summary>
public class TimelineEditModelTests
{
    [Fact]
    public void TimeScreenRoundTrip()
    {
        const float width = 400f;
        const double duration = 8d;
        double t = 2d;
        float x = TimelineEditModel.TimeToScreenX(t, width, duration);
        Assert.Equal(100f, x, 3);
        Assert.Equal(t, TimelineEditModel.ScreenXToTime(x, width, duration), 4);
    }

    [Fact]
    public void TrackIndexAt_SkipsRuler()
    {
        Assert.Equal(-1, TimelineEditModel.TrackIndexAt(10f, rulerHeight: 18f, rowHeight: 18f, trackCount: 3));
        Assert.Equal(0, TimelineEditModel.TrackIndexAt(18f, 18f, 18f, 3));
        Assert.Equal(2, TimelineEditModel.TrackIndexAt(18f + 36f, 18f, 18f, 3));
        Assert.Equal(-1, TimelineEditModel.TrackIndexAt(18f + 90f, 18f, 18f, 3));
    }

    [Fact]
    public void MoveClip_ClampsToZero()
    {
        double start = 2d;
        TimelineEditModel.MoveClip(ref start, duration: 1d, newStart: -0.5d);
        Assert.Equal(0d, start);
        TimelineEditModel.MoveClip(ref start, 1d, 3.5d);
        Assert.Equal(3.5d, start);
    }

    [Fact]
    public void TrimStart_KeepsMinDuration()
    {
        double start = 0d;
        double duration = 2d;
        TimelineEditModel.TrimStart(ref start, ref duration, newStart: 1.99d);
        Assert.True(duration >= TimelineEditModel.MinClipDuration);
        Assert.Equal(2d, start + duration, 4);
    }

    [Fact]
    public void TrimEnd_KeepsMinDuration()
    {
        double duration = 2d;
        TimelineEditModel.TrimEnd(ref duration, start: 1d, newEnd: 1.01d);
        Assert.True(duration >= TimelineEditModel.MinClipDuration);
    }

    [Fact]
    public void HitTrimHandles()
    {
        Assert.True(TimelineEditModel.HitTrimStart(10f, clipX: 10f, clipW: 80f));
        Assert.False(TimelineEditModel.HitTrimStart(40f, 10f, 80f));
        Assert.True(TimelineEditModel.HitTrimEnd(88f, 10f, 80f));
        Assert.False(TimelineEditModel.HitTrimEnd(40f, 10f, 80f));
    }

    [Fact]
    public void HitSignal_WithinRadius()
    {
        Assert.True(TimelineEditModel.HitSignal(50f, 55f, 8f));
        Assert.False(TimelineEditModel.HitSignal(50f, 70f, 8f));
    }

    [Fact]
    public void ExpandDuration_GrowsWhenClipPastEnd()
    {
        Assert.Equal(5d, TimelineEditModel.ExpandDuration(3d, 5d));
        Assert.Equal(3d, TimelineEditModel.ExpandDuration(3d, 2d));
    }
}
