// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// Reports unavailable host coverage as skipped. On macOS, device creation and native
/// loading must succeed in the test body rather than becoming a passing early return.
/// </summary>
public sealed class MacOSMetalFactAttribute : FactAttribute
{
    public MacOSMetalFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Requires a macOS host with a Metal device.";
    }
}

public sealed class MacOSMetalTheoryAttribute : TheoryAttribute
{
    public MacOSMetalTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Requires a macOS host with a Metal device.";
    }
}
