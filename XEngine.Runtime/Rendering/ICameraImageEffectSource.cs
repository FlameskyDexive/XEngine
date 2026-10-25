// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Optional camera component that contributes <see cref="ImageEffect"/> instances without
/// storing them on <see cref="Camera.Effects"/>. Used by the official post-processing package
/// so core does not take a compile-time dependency on that package.
/// </summary>
public interface ICameraImageEffectSource
{
    /// <summary>Blend volumes and copy settings onto owned effects before gather.</summary>
    void Prepare(Camera camera);

    /// <summary>Append enabled effects for this camera. Caller owns the list.</summary>
    void CollectImageEffects(List<ImageEffect> destination);
}
