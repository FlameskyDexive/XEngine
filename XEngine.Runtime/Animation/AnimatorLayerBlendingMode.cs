// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Animation;

/// <summary>How an animation layer combines with the layers below it.</summary>
public enum AnimatorLayerBlendingMode : byte
{
    /// <summary>
    /// The layer overrides the accumulated pose for the bones its mask allows, interpolated by the
    /// layer's weight. The base layer is always Override.
    /// </summary>
    Override = 0,

    /// <summary>
    /// The layer adds its pose (treated as a delta from the reference) on top of the accumulated
    /// result, scaled by the layer's weight and mask. Use for add-on motions like breathing.
    /// </summary>
    Additive = 1,
}
