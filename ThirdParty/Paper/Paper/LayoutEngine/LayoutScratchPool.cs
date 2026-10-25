// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.PaperUI.LayoutEngine;

/// <summary>
/// Per-Paper pool of the scratch lists the layout pass needs for every element (stretch items,
/// child records, index lists, wrap lines). Layout is recursive, so rent/return is stack-like:
/// each DoLayout frame rents its own lists and returns them on exit, which means nested layout
/// calls can never trample a parent's scratch. Lists are cleared on return; after the first few
/// frames the pool reaches steady state and the layout pass allocates nothing per frame.
/// Not thread-safe by design: a Paper instance is single-threaded per frame, and the pool hangs
/// off the Paper instance (no cross-instance or cross-thread sharing).
/// </summary>
internal sealed class LayoutScratchPool
{
    private readonly Stack<List<StretchItem>> _stretchLists = new();
    private readonly Stack<List<ChildElementInfo>> _childLists = new();
    private readonly Stack<List<int>> _intLists = new();
    private readonly Stack<List<(int Start, int Count, float Used, float Cross)>> _lineLists = new();

    public List<StretchItem> RentStretchList()
        => _stretchLists.Count > 0 ? _stretchLists.Pop() : new List<StretchItem>();

    public void Return(List<StretchItem> list)
    {
        list.Clear();
        _stretchLists.Push(list);
    }

    public List<ChildElementInfo> RentChildList()
        => _childLists.Count > 0 ? _childLists.Pop() : new List<ChildElementInfo>();

    public void Return(List<ChildElementInfo> list)
    {
        list.Clear();
        _childLists.Push(list);
    }

    public List<int> RentIntList()
        => _intLists.Count > 0 ? _intLists.Pop() : new List<int>();

    public void Return(List<int> list)
    {
        list.Clear();
        _intLists.Push(list);
    }

    public List<(int Start, int Count, float Used, float Cross)> RentLineList()
        => _lineLists.Count > 0 ? _lineLists.Pop() : new List<(int, int, float, float)>();

    public void Return(List<(int Start, int Count, float Used, float Cross)> list)
    {
        list.Clear();
        _lineLists.Push(list);
    }
}
