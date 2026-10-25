// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PaperUI.LayoutEngine;

// Mutable value type: the stretch-resolution loops mutate items in place through
// CollectionsMarshal.AsSpan refs. Converted from a class so the per-frame layout pass allocates
// nothing per item (lists of these are pooled per Paper instance, see LayoutScratchPool).
internal struct StretchItem
{
    public enum ItemTypes : byte { Before, Size, After }

    public int Index;
    public float Factor;
    public ItemTypes ItemType;
    public bool Frozen;
    public float Violation;
    public float Computed;
    public float Min;
    public float Max;

    public StretchItem(int index, float factor, ItemTypes itemType, float min, float max)
    {
        Index = index;
        Factor = factor;
        ItemType = itemType;
        Frozen = false;
        Violation = 0f;
        Computed = 0f;
        Min = min;
        Max = max;
    }
}
