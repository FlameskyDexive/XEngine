// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace SoftwareRasterizer.Rasterizer;

public struct RasterVertex
{
    public Float4 Position;
    public Float4[] Varyings;
    public Float3 ScreenPosition;
}
