using System;
using XEngine.Vector;
using XEngine.Vector.Spatial;

namespace XEngine.Quill;

/// <summary>Opaque ordered layer. Its renderer supplies the execution contract.
/// The owner must outlive every Canvas draw list containing this marker.</summary>
public interface ICanvasRenderLayer { }

/// <summary>Exact transform and shader-scissor state at a layer boundary. This is
/// not an axis-aligned approximation and does not inherit brush/font styling.</summary>
public readonly struct CanvasLayerContext : IEquatable<CanvasLayerContext>
{
    internal readonly Transform2D Transform, Scissor;
    internal readonly Float2 ScissorExtent;
    public float FramebufferScale { get; }
    public float GlobalAlpha { get; }
    internal CanvasLayerContext(Transform2D transform, Transform2D scissor, Float2 extent, float scale, float alpha)
    { Transform = transform; Scissor = scissor; ScissorExtent = extent; FramebufferScale = scale; GlobalAlpha = alpha; }
    public bool Equals(CanvasLayerContext other) => FramebufferScale == other.FramebufferScale
        && GlobalAlpha == other.GlobalAlpha
        && DrawCall.SameTransform(in Transform, in other.Transform)
        && DrawCall.SameTransform(in Scissor, in other.Scissor)
        && ScissorExtent.X == other.ScissorExtent.X && ScissorExtent.Y == other.ScissorExtent.Y;
    public override bool Equals(object? value) => value is CanvasLayerContext other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Transform, Scissor, ScissorExtent, FramebufferScale, GlobalAlpha);
}
