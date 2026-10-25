// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// How a <see cref="SpriteRenderer"/> maps a sprite onto its draw size.
/// Old scenes default to <see cref="Simple"/>.
/// </summary>
public enum SpriteDrawMode
{
    Simple = 0,
    Sliced = 1,
    Tiled = 2,
}
