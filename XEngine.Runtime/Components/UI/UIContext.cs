// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.UI;

/// <summary>
/// Inherited rendering context passed down the UI hierarchy during
/// <see cref="GameCanvas"/> tree construction.
/// Accumulates values from <see cref="CanvasGroup"/> components.
/// </summary>
public struct UIContext
{
    /// <summary>
    /// Accumulated alpha multiplier (1 = fully opaque, 0 = fully transparent).
    /// Each <see cref="CanvasGroup"/> multiplies its own alpha into this value.
    /// </summary>
    public float Alpha;

    /// <summary>
    /// Whether pointer events should be processed for elements under this context.
    /// A <see cref="CanvasGroup"/> with <see cref="CanvasGroup.Interactable"/> set to
    /// <c>false</c> disables interaction for all descendants.
    /// </summary>
    public bool Interactable;

    /// <summary>
    /// Whether raycasts (pointer hit-testing) are blocked for elements under this context.
    /// </summary>
    public bool BlocksRaycasts;

    /// <summary>
    /// RESERVED (2026-08-20, font-clarity node 4): canvas design-to-screen pixel scale for
    /// per-size SDF raster buckets. Currently always 1 — retained-canvas adaptation is parked
    /// until per-bucket DistanceRange lands (see Quill Canvas.ScaleSettings note and
    /// FontQualityExtensions.PickBucket); filling this in is the hookup point.
    /// </summary>
    public float CanvasScale;

    /// <summary>
    /// Creates a default context with full opacity and all interaction enabled.
    /// </summary>
    public static UIContext Default => new()
    {
        Alpha = 1f,
        Interactable = true,
        BlocksRaycasts = true,
        CanvasScale = 1f,
    };
}
