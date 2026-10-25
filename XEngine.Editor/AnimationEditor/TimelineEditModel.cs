// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Pure time↔screen and clip-trim math for the Timeline window (M23). No Paper, no Timeline types
/// — unit-testable from XEngine.Editor.Test.
/// </summary>
public static class TimelineEditModel
{
    public const float TrimHandlePixels = 6f;
    public const double MinClipDuration = 0.05;

    public static float TimeToScreenX(double time, float width, double duration)
    {
        if (duration <= 0d || width <= 0f) return 0f;
        return (float)(time / duration * width);
    }

    public static double ScreenXToTime(float x, float width, double duration)
    {
        if (width <= 0f) return 0d;
        double t = x / width * duration;
        if (t < 0d) t = 0d;
        return t;
    }

    public static int TrackIndexAt(float y, float rulerHeight, float rowHeight, int trackCount)
    {
        if (rowHeight <= 0f || y < rulerHeight) return -1;
        int i = (int)((y - rulerHeight) / rowHeight);
        if (i < 0 || i >= trackCount) return -1;
        return i;
    }

    public static void MoveClip(ref double start, double duration, double newStart)
    {
        if (newStart < 0d) newStart = 0d;
        start = newStart;
        _ = duration;
    }

    public static void TrimStart(ref double start, ref double duration, double newStart)
    {
        double end = start + duration;
        if (newStart < 0d) newStart = 0d;
        if (newStart > end - MinClipDuration) newStart = end - MinClipDuration;
        duration = end - newStart;
        start = newStart;
    }

    public static void TrimEnd(ref double duration, double start, double newEnd)
    {
        if (newEnd < start + MinClipDuration) newEnd = start + MinClipDuration;
        duration = newEnd - start;
    }

    public static bool HitTrimStart(float pointerX, float clipX, float clipW, float handlePx = TrimHandlePixels)
        => pointerX >= clipX && pointerX <= clipX + handlePx && clipW > handlePx * 2f;

    public static bool HitTrimEnd(float pointerX, float clipX, float clipW, float handlePx = TrimHandlePixels)
        => pointerX >= clipX + clipW - handlePx && pointerX <= clipX + clipW && clipW > handlePx * 2f;

    public static bool HitSignal(float pointerX, float markerX, float radiusPx = 8f)
    {
        float d = pointerX - markerX;
        if (d < 0f) d = -d;
        return d <= radiusPx;
    }

    public static double ExpandDuration(double current, double clipEnd)
        => clipEnd > current ? clipEnd : current;
}
