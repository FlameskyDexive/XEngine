// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.OrigamiUI;

/// <summary>Optional native-window actions rendered inside a dock space's top tab bar.</summary>
public sealed class DockWindowChrome
{
    public Action? Minimize { get; init; }
    public Action? ToggleMaximize { get; init; }
    public Action? Close { get; init; }
    public Action? BeginMove { get; init; }
    public Action<Float2>? MoveBy { get; init; }
    public Func<bool>? IsMaximized { get; init; }
}
