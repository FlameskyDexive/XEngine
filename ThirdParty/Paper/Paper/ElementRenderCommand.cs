// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Quill;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.PaperUI;

/// <summary>
/// Represents a drawable UI element command.
/// </summary>
internal class ElementRenderCommand
{
    public LayoutEngine.ElementHandle Element { get; set; }
    public Action<Canvas, Rect>? RenderAction { get; set; }
    public string? PaintKind { get; set; }
    public ulong PaintVersion { get; set; }
    public ulong PaintDetail { get; set; }
    public object? PaintSource { get; set; }
    public object? PaintDependency { get; set; }
}
