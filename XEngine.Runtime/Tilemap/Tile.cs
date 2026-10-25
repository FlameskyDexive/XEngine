// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

[CreateAssetMenu("2D/Tile", Extension = ".tile", Order = 60)]
public sealed class Tile : TileBase
{
    public AssetRef<Sprite> Sprite;
    public Color Color = Color.White;
}
