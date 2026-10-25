// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>Backend-neutral clipping state carried by a Canvas render node.</summary>
public readonly struct CanvasClipState : IEquatable<CanvasClipState>
{
    public static CanvasClipState None => default;

    public readonly bool Enabled;
    public readonly bool HasRect;
    public readonly bool UsesStencil;
    public readonly bool IsStencilWrite;
    public readonly bool IsStencilUnwind;
    public readonly bool SuppressStencilWriterColor;
    public readonly uint StencilDepth;
    public readonly uint StencilReference;
    public readonly Float4x4 ClipToLocal;
    public readonly Float4 ClipRect;
    public readonly float CornerRadius;
    public readonly float Softness;

    public CanvasClipState(
        bool enabled,
        bool hasRect,
        bool usesStencil,
        bool isStencilWrite,
        bool isStencilUnwind,
        bool suppressStencilWriterColor,
        uint stencilDepth,
        uint stencilReference,
        in Float4x4 clipToLocal,
        in Float4 clipRect,
        float cornerRadius,
        float softness)
    {
        Enabled = enabled;
        HasRect = hasRect;
        UsesStencil = usesStencil;
        IsStencilWrite = isStencilWrite;
        IsStencilUnwind = isStencilUnwind;
        SuppressStencilWriterColor = suppressStencilWriterColor;
        StencilDepth = stencilDepth;
        StencilReference = stencilReference;
        ClipToLocal = clipToLocal;
        ClipRect = clipRect;
        CornerRadius = cornerRadius;
        Softness = softness;
    }

    public static CanvasClipState Rect(
        in Float4x4 clipToLocal,
        in Float4 clipRect,
        float cornerRadius = 0f,
        float softness = 0f)
        => new(true, true, false, false, false, false, 0, 0, clipToLocal, clipRect, cornerRadius, softness);

    public static CanvasClipState Stencil(uint depth)
        => new(true, false, true, false, false, false, depth, depth, Float4x4.Identity, default, 0f, 0f);

    /// <summary>Creates a stencil test state, optionally preserving a rectangular shader clip.</summary>
    public static CanvasClipState WithStencil(in CanvasClipState rect, uint depth)
        => new(true, rect.HasRect, true, false, false, false, depth, depth, rect.ClipToLocal, rect.ClipRect,
            rect.CornerRadius, rect.Softness);

    /// <summary>Creates the state used while drawing a mask graphic. The graphic tests its
    /// parent stencil value and increments it to <paramref name="depth"/>.</summary>
    public static CanvasClipState StencilWrite(uint depth, uint parentDepth, bool suppressColor = false)
        => StencilWrite(depth, parentDepth, suppressColor, CanvasClipState.None);

    /// <summary>Creates a stencil writer while retaining an inherited rectangular shader clip.</summary>
    public static CanvasClipState StencilWrite(uint depth, uint parentDepth, bool suppressColor,
        in CanvasClipState rect)
        => new(true, rect.HasRect, true, true, false, suppressColor, depth, parentDepth, rect.ClipToLocal,
            rect.ClipRect, rect.CornerRadius, rect.Softness);

    /// <summary>Creates the colorless draw emitted after a mask subtree to restore its parent's
    /// stencil value before later siblings are rendered.</summary>
    public static CanvasClipState StencilUnwind(uint parentDepth, uint childDepth,
        in CanvasClipState rect)
        => new(true, rect.HasRect, true, false, true, true, parentDepth, childDepth, rect.ClipToLocal,
            rect.ClipRect, rect.CornerRadius, rect.Softness);

    public bool Equals(CanvasClipState other)
        => Enabled == other.Enabled
        && HasRect == other.HasRect
        && UsesStencil == other.UsesStencil
        && IsStencilWrite == other.IsStencilWrite
        && IsStencilUnwind == other.IsStencilUnwind
        && SuppressStencilWriterColor == other.SuppressStencilWriterColor
        && StencilDepth == other.StencilDepth
        && StencilReference == other.StencilReference
        && ClipToLocal.Equals(other.ClipToLocal)
        && ClipRect.Equals(other.ClipRect)
        && CornerRadius.Equals(other.CornerRadius)
        && Softness.Equals(other.Softness);

    public override bool Equals(object? obj) => obj is CanvasClipState other && Equals(other);
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Enabled);
        hash.Add(HasRect);
        hash.Add(UsesStencil);
        hash.Add(IsStencilWrite);
        hash.Add(IsStencilUnwind);
        hash.Add(SuppressStencilWriterColor);
        hash.Add(StencilDepth);
        hash.Add(StencilReference);
        hash.Add(ClipToLocal);
        hash.Add(ClipRect);
        hash.Add(CornerRadius);
        hash.Add(Softness);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Tests a point already transformed into clip-local coordinates. Stencil clips are evaluated by
    /// the backend and therefore do not reject pointer candidates here. Rounded rectangle corners use
    /// the same signed-distance geometry as the UI shader, with the radius clamped to the clip bounds.
    /// </summary>
    internal bool ContainsLocalPoint(Float2 point)
    {
        if (!Enabled || !HasRect) return true;

        float minX = ClipRect.X;
        float minY = ClipRect.Y;
        float maxX = ClipRect.Z;
        float maxY = ClipRect.W;
        if (point.X < minX || point.X > maxX || point.Y < minY || point.Y > maxY)
            return false;

        float radius = MathF.Max(0f, CornerRadius);
        radius = MathF.Min(radius, MathF.Min(maxX - minX, maxY - minY) * 0.5f);
        if (radius <= 0f) return true;

        float dx = point.X < minX + radius ? point.X - (minX + radius)
            : point.X > maxX - radius ? point.X - (maxX - radius) : 0f;
        float dy = point.Y < minY + radius ? point.Y - (minY + radius)
            : point.Y > maxY - radius ? point.Y - (maxY - radius) : 0f;

        // At least one axis outside the corner square means the point is on a straight edge.
        if (dx == 0f || dy == 0f) return true;
        return dx * dx + dy * dy <= radius * radius + 1e-5f;
    }
}
