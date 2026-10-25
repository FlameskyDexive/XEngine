// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>
/// How a value handed to <see cref="Rigidbody3D.AddForce(Vector.Float3, ForceMode)"/> or
/// <see cref="Rigidbody3D.AddTorque(Vector.Float3, ForceMode)"/> is interpreted.
/// </summary>
/// <remarks>
/// Two independent choices, which is what the four modes are: whether the value is spread over the
/// step or applied at once, and whether mass (or inertia, for torque) divides it. Values match Unity's
/// <c>ForceMode</c>.
/// </remarks>
public enum ForceMode
{
    /// <summary>
    /// A force in mass·distance/second², applied continuously over the step. Heavier bodies accelerate
    /// less.
    /// </summary>
    Force = 0,

    /// <summary>
    /// An impulse in mass·distance/second, applied at once. Heavier bodies gain less speed.
    /// </summary>
    Impulse = 1,

    /// <summary>
    /// A velocity in distance/second, added at once regardless of mass.
    /// </summary>
    VelocityChange = 2,

    /// <summary>
    /// An acceleration in distance/second², applied continuously over the step regardless of mass.
    /// </summary>
    Acceleration = 5,
}
