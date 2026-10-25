// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.UI;

/// <summary>
/// Runtime-owned dirty categories used by the Canvas host. The enum deliberately does
/// not depend on any XGUI component type, so the render pipeline can reason about a
/// canvas without loading the UI package.
/// </summary>
[Flags]
public enum CanvasDirtyFlags
{
    None = 0,
    Transform = 1 << 0,
    Vertices = 1 << 1,
    Material = 1 << 2,
    Clip = 1 << 3,
    Hierarchy = 1 << 4,
    All = Transform | Vertices | Material | Clip | Hierarchy,
}
