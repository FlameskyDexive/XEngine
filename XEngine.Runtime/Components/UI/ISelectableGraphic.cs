// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>
/// Runtime-only color contract used by <see cref="Selectable"/>. The host cannot reference the
/// XGUI package assembly, so both the legacy <see cref="UIImage"/> and package Graphic implement
/// this small bridge.
/// </summary>
public interface ISelectableGraphic
{
    Color Color { get; set; }
}

/// <summary>
/// Optional extension for selectable targets that can replace their source sprite. The Runtime
/// owns this contract so <see cref="Selectable"/> stays independent of the XGUI package.
/// </summary>
public interface ISelectableSpriteGraphic : ISelectableGraphic
{
    AssetRef<Sprite> Sprite { get; set; }
}
