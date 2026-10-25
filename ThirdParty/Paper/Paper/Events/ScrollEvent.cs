// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.PaperUI.LayoutEngine;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.PaperUI.Events;

/// <summary> Represents a scroll wheel event on an element. Delta is the scroll amount and direction (positive = down/right, negative = up/left, in arbitrary units). </summary>
public class ScrollEvent : ElementEvent
{
    /// <summary> Gets the scroll delta, in logical units. Positive values indicate scrolling down or right; negative values indicate up or left, depending on the scroll direction. </summary>
    public float Delta { get; }

    /// <summary> Initializes a new ScrollEvent with the specified source element, element rectangle, pointer position, and scroll delta (positive for upward/forward scrolling, negative for downward/backward). </summary>
    public ScrollEvent(ElementHandle source, Rect elementRect, Float2 pointerPos, float delta)
        : base(source, elementRect, pointerPos)
    {
        Delta = delta;
    }
}
