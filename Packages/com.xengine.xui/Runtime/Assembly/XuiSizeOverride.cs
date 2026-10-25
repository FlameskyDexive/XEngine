// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.XUI;

/// <summary>
/// Reports an authored <c>size</c> as the element's preferred size, so a layout container honours it.
/// </summary>
/// <remarks>
/// <para>
/// Without this, <c>size</c> silently does nothing on some elements inside a <c>VStack</c> or
/// <c>HStack</c>. A container sizes its children from what they report through
/// <see cref="ILayoutElementHost"/>, and a component that reports its own preferred size — a
/// <c>Text</c> measuring its glyphs, say — wins over the rect the author set. The result is a
/// document that reads as if it pinned a height and a screen where that height is whatever the text
/// happened to measure, including zero.
/// </para>
/// <para>
/// So the assembler attaches this wherever the document pins an axis. Measurement takes the maximum
/// across reporting components, so an explicit size beats an intrinsic one and <c>size</c> means the
/// same thing everywhere — which is the only version of the rule worth documenting.
/// </para>
/// </remarks>
public sealed class XuiSizeOverride : UIBehaviour, ILayoutElementHost
{
    /// <summary>Authored width, or -1 when the document left the axis to the layout.</summary>
    public float Width { get; set; } = -1f;

    /// <summary>Authored height, or -1 when the document left the axis to the layout.</summary>
    public float Height { get; set; } = -1f;

    public bool IgnoreLayout => false;

    public float MinWidth => Width;
    public float PreferredWidth => Width;
    public float FlexibleWidth => -1f;

    public float MinHeight => Height;
    public float PreferredHeight => Height;
    public float FlexibleHeight => -1f;

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    public override void ApplyLayout(in Rect parentRect) { }
}
