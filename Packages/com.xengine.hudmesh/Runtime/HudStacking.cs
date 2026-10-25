namespace XEngine.Hudmesh;

/// <summary>
/// Pure vertical-stack math, ported from UnityHUD's HUDObject.GetComponentOffset/MeshHide:
/// each slot sits above the accumulated height (Size.Y + ItemLineGap) of every valid slot
/// below it. Offsets are recomputed from scratch on every change instead of shifted
/// incrementally — same result, no hidden-state bugs.
/// </summary>
internal static class HudStacking
{
    /// <summary>Pixel Y offset of slot <paramref name="index"/> given the widgets currently in the stack.</summary>
    public static float StackHeight(HudWidget?[] slots, int index)
    {
        float y = 0f;
        for (int i = index - 1; i >= 0; i--)
        {
            HudWidget? widget = slots[i];
            if (widget is { IsValid: true })
                y += widget.Size.Y + widget.ItemLineGap;
        }
        return y;
    }

    /// <summary>Total stacked height (used to place dynamic widgets such as bubbles above everything).</summary>
    public static float TotalHeight(HudWidget?[] slots)
    {
        float y = 0f;
        for (int i = 0; i < slots.Length; i++)
        {
            HudWidget? widget = slots[i];
            if (widget is { IsValid: true })
                y += widget.Size.Y + widget.ItemLineGap;
        }
        return y;
    }
}
