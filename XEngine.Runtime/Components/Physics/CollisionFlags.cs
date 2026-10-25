// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Which sides of a <see cref="CharacterController"/> hit something during the last
/// <see cref="CharacterController.Move"/>. Matches Unity's <c>CollisionFlags</c>.
/// </summary>
[Flags]
public enum CollisionFlags
{
    None = 0,
    Sides = 1,
    Above = 2,
    Below = 4,
}
