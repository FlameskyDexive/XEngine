// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Editor.AnimationEditor;

/// <summary>M34: layout math for the transition inspector timeline (Unity TransitionPreview analogue).</summary>
public static class TransitionTimelineLayout
{
    public const float Height = 72f;
    public const float BarH = 14f;

    public readonly struct Layout
    {
        public readonly float X, Y, W;
        public readonly float ViewLength;
        public readonly float SourceX, SourceW, SourceY;
        public readonly float DestX, DestW, DestY;
        public readonly float CrossX, CrossW, CrossY;
        public readonly float ExitX;
        public readonly bool HasExitTime;

        public Layout(
            float x, float y, float w, float viewLength,
            float sourceX, float sourceW, float sourceY,
            float destX, float destW, float destY,
            float crossX, float crossW, float crossY,
            float exitX, bool hasExitTime)
        {
            X = x; Y = y; W = w; ViewLength = viewLength;
            SourceX = sourceX; SourceW = sourceW; SourceY = sourceY;
            DestX = destX; DestW = destW; DestY = destY;
            CrossX = crossX; CrossW = crossW; CrossY = crossY;
            ExitX = exitX; HasExitTime = hasExitTime;
        }
    }

    public enum Hit { None, ExitTime, Duration, Offset }

    public static Layout Compute(
        float x, float y, float width, float height,
        float duration, float offset, float exitTime, bool hasExitTime, bool hasFixedDuration)
    {
        float view = 1f + MathF.Max(0f, offset);
        if (view < 1f) view = 1f;
        float unit = width / view;
        float sourceW = unit;
        float destX = x + offset * unit;
        float destW = unit;
        float sourceY = y + 8f;
        float destY = y + height - 8f - BarH;
        float durUnits = hasFixedDuration ? duration : duration;
        if (durUnits < 0f) durUnits = 0f;
        float crossStartT = hasExitTime ? exitTime : MathF.Max(0f, 1f - durUnits);
        float crossX = x + crossStartT * unit;
        float crossW = MathF.Max(4f, durUnits * unit);
        float exitX = x + (hasExitTime ? exitTime : 0f) * unit;
        return new Layout(
            x, y, width, view,
            x, sourceW, sourceY,
            destX, destW, destY,
            crossX, crossW, (sourceY + destY) * 0.5f,
            exitX, hasExitTime);
    }

    public static Hit HitTest(in Layout layout, float px, float py)
    {
        if (layout.HasExitTime && MathF.Abs(px - layout.ExitX) <= 6f
            && py >= layout.SourceY - 4f && py <= layout.SourceY + BarH + 4f)
            return Hit.ExitTime;
        if (px >= layout.CrossX + layout.CrossW - 6f && px <= layout.CrossX + layout.CrossW + 6f
            && py >= layout.Y && py <= layout.Y + Height)
            return Hit.Duration;
        if (py >= layout.DestY - 2f && py <= layout.DestY + BarH + 2f
            && px >= layout.DestX && px <= layout.DestX + layout.DestW)
            return Hit.Offset;
        return Hit.None;
    }

    public static void ApplyDrag(
        Hit hit, float deltaPx, float width, float viewLength,
        ref float duration, ref float offset, ref float exitTime)
    {
        if (width <= 0f || viewLength <= 0f) return;
        float dt = deltaPx * viewLength / width;
        switch (hit)
        {
            case Hit.ExitTime:
                exitTime = Clamp01(exitTime + dt);
                break;
            case Hit.Duration:
                duration = MathF.Max(0f, duration + dt);
                break;
            case Hit.Offset:
                offset = MathF.Max(0f, offset + dt);
                break;
        }
    }

    private static float Clamp01(float v)
    {
        if (v < 0f) return 0f;
        if (v > 1f) return 1f;
        return v;
    }
}
