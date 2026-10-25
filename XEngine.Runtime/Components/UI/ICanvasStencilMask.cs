// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.UI;

/// <summary>
/// Package-facing declaration for a component that uses the Graphic on its GameObject to write
/// an entry into the Canvas stencil buffer. The Runtime host owns traversal and GPU submission;
/// packages implement this interface without making the host depend on their concrete type.
/// </summary>
public interface ICanvasStencilMask
{
    /// <summary>Whether the source Graphic remains visible while it writes the mask.</summary>
    bool ShowMaskGraphic { get; }
}
