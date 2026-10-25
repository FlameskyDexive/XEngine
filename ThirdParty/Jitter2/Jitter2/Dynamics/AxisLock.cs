// XENGINE: local addition. Frozen degrees of freedom, see ThirdParty/Jitter2/PATCHES.md.

using System;
using System.Runtime.CompilerServices;

using Jitter2.LinearMath;

namespace Jitter2.Dynamics;

/// <summary>
/// World-space degrees of freedom a body is forbidden to move along.
/// </summary>
/// <remarks>
/// Locks are absolute: no contact, joint, force or directly assigned velocity can move the body
/// along a locked axis. They are the kernel side of a "freeze axis" authoring switch.
/// </remarks>
[Flags]
public enum AxisLock
{
    None = 0,

    /// <summary>Translation along the world X axis.</summary>
    LinearX = 1 << 0,

    /// <summary>Translation along the world Y axis.</summary>
    LinearY = 1 << 1,

    /// <summary>Translation along the world Z axis.</summary>
    LinearZ = 1 << 2,

    /// <summary>Rotation about the world X axis.</summary>
    AngularX = 1 << 3,

    /// <summary>Rotation about the world Y axis.</summary>
    AngularY = 1 << 4,

    /// <summary>Rotation about the world Z axis.</summary>
    AngularZ = 1 << 5,

    Linear = LinearX | LinearY | LinearZ,
    Angular = AngularX | AngularY | AngularZ,
    All = Linear | Angular,
}

internal static class AxisLockExtensions
{
    /// <summary>
    /// Removes the locked angular axes from an inverse inertia tensor, so the solver cannot spend
    /// impulse on rotations that will be discarded.
    /// </summary>
    /// <remarks>
    /// This is the projection <c>P·I⁻¹·P</c> for the diagonal <c>P</c> that drops the locked axes:
    /// zeroing row <c>i</c> and column <c>i</c> keeps the tensor symmetric and positive
    /// semi-definite, and reads as infinite inertia about that axis. Without it the response would
    /// still be correct along the locked axis — the velocity mask sees to that — but a contact would
    /// size its impulse as if the body could spin, so friction and restitution would come out softer
    /// than they should for a body that cannot.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ProjectInverseInertia(this AxisLock locked, ref JMatrix inverseInertia)
    {
        if ((locked & AxisLock.Angular) == 0) return;

        if ((locked & AxisLock.AngularX) != 0)
        {
            inverseInertia.M11 = inverseInertia.M12 = inverseInertia.M13 = (Real)0.0;
            inverseInertia.M21 = inverseInertia.M31 = (Real)0.0;
        }

        if ((locked & AxisLock.AngularY) != 0)
        {
            inverseInertia.M21 = inverseInertia.M22 = inverseInertia.M23 = (Real)0.0;
            inverseInertia.M12 = inverseInertia.M32 = (Real)0.0;
        }

        if ((locked & AxisLock.AngularZ) != 0)
        {
            inverseInertia.M31 = inverseInertia.M32 = inverseInertia.M33 = (Real)0.0;
            inverseInertia.M13 = inverseInertia.M23 = (Real)0.0;
        }
    }
}
