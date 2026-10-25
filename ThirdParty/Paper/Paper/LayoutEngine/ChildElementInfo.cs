// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PaperUI.LayoutEngine;

// Mutable value type: layout passes mutate entries in place through CollectionsMarshal.AsSpan
// refs. Converted from a class so the per-frame layout pass allocates nothing per child (lists of
// these are pooled per Paper instance, see LayoutScratchPool).
internal struct ChildElementInfo
{
    public ElementHandle Element;
    public float CrossBefore;
    public float Cross;
    public float CrossAfter;
    public float MainBefore;
    public float Main;
    public float MainAfter;

    public ChildElementInfo(ElementHandle element)
    {
        Element = element;
        CrossBefore = 0f;
        Cross = 0f;
        CrossAfter = 0f;
        MainBefore = 0f;
        Main = 0f;
        MainAfter = 0f;
    }
}
