// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.PaperUI.LayoutEngine;

/// <summary>
/// CollectionsMarshal.AsSpan for every TFM Paper targets. The layout pass mutates pooled
/// StretchItem/ChildElementInfo lists in place through refs into the list's backing array; on
/// modern TFMs this is the BCL API, on netstandard2.1 (no CollectionsMarshal) the backing array is
/// fetched once per element type via reflection (List&lt;T&gt;._items on all mainstream runtimes)
/// and wrapped. Callers must not Add/Remove on the list while holding the span (same contract as
/// CollectionsMarshal.AsSpan).
/// </summary>
internal static class ListSpanCompat
{
    public static Span<T> AsSpan<T>(List<T> list)
    {
#if NET5_0_OR_GREATER
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list);
#else
        var field = ItemsField<T>.Field
            ?? throw new InvalidOperationException("List<T> backing field '_items' not found on this runtime.");
        return new Span<T>((T[])field.GetValue(list)!, 0, list.Count);
#endif
    }

#if !NET5_0_OR_GREATER
    private static class ItemsField<T>
    {
        public static readonly System.Reflection.FieldInfo? Field = typeof(List<T>).GetField(
            "_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    }
#endif
}
