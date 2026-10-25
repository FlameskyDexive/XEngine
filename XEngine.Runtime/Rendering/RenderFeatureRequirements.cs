// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Rendering;

[Flags]
public enum RenderFeatureRequirements
{
    None = 0,
    Depth = 1 << 0,
    Normals = 1 << 1,
    MotionVectors = 1 << 2,
}
