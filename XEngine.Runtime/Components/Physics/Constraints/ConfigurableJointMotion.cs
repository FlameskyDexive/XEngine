// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime;

/// <summary>Per-axis motion mode for <see cref="ConfigurableJoint"/>. Matches Unity's enum.</summary>
public enum ConfigurableJointMotion
{
    Locked = 0,
    Limited = 1,
    Free = 2,
}
