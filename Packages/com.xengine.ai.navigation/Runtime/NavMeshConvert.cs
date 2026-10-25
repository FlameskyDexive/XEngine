// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

using DotRecast.Core.Numerics;

using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Cheap <see cref="Float3"/> ↔ <see cref="RcVec3f"/> copies. Coordinates are not mirrored:
/// XEngine feeds Recast with reversed triangle winding so walkable-slope normals stay correct,
/// and query results can be used as world positions without a per-point transform.
/// </summary>
public static class NavMeshConvert
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RcVec3f ToRecast(Float3 v) => new(v.X, v.Y, v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float3 ToEngine(RcVec3f v) => new(v.X, v.Y, v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToRecast(Float3 v, float[] dest, int offset)
    {
        dest[offset] = v.X;
        dest[offset + 1] = v.Y;
        dest[offset + 2] = v.Z;
    }
}
