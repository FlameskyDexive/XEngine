// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.OrigamiUI;
using XEngine.Runtime;

namespace XEngine.Editor.GUI.Panels;

public enum HierarchyFoldAction
{
    None,
    Collapse,
    Expand,
    SelectParent,
    SelectFirstChild,
}

/// <summary>Pure Hierarchy arrow-key navigation over the visible (non-collapsed) rows.</summary>
public static class HierarchyKeyboard
{
    public static void CollectVisibleIndices(
        IReadOnlyList<TreeNode> nodes,
        IReadOnlyDictionary<string, bool> expandState,
        List<int> dest)
    {
        dest.Clear();
        if (nodes == null) return;
        int hideDeeperThan = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            TreeNode node = nodes[i];
            if (hideDeeperThan >= 0 && node.Depth > hideDeeperThan)
                continue;
            hideDeeperThan = -1;
            dest.Add(i);
            bool expanded = node.HasChildren
                && expandState != null
                && expandState.TryGetValue(node.Id, out bool exp)
                && exp;
            if (node.HasChildren && !expanded)
                hideDeeperThan = node.Depth;
        }
    }

    public static int IndexOfVisible(List<int> visible, IReadOnlyList<object> flat, object? active)
    {
        if (active == null || visible == null || flat == null) return -1;
        for (int i = 0; i < visible.Count; i++)
        {
            int flatIndex = visible[i];
            if (flatIndex >= 0 && flatIndex < flat.Count && ReferenceEquals(flat[flatIndex], active))
                return i;
        }
        return -1;
    }

    public static int StepVisible(int visibleIndex, int delta, int visibleCount)
    {
        if (visibleCount <= 0) return -1;
        if (visibleIndex < 0)
            return delta >= 0 ? 0 : visibleCount - 1;
        int next = visibleIndex + delta;
        if (next < 0) return 0;
        if (next >= visibleCount) return visibleCount - 1;
        return next;
    }

    public static HierarchyFoldAction ResolveFold(bool left, bool hasChildren, bool expanded, bool hasVisibleParent)
    {
        if (left)
        {
            if (hasChildren && expanded) return HierarchyFoldAction.Collapse;
            return hasVisibleParent ? HierarchyFoldAction.SelectParent : HierarchyFoldAction.None;
        }

        if (hasChildren && !expanded) return HierarchyFoldAction.Expand;
        return hasChildren && expanded ? HierarchyFoldAction.SelectFirstChild : HierarchyFoldAction.None;
    }

    public static int FindParentVisibleIndex(List<int> visible, IReadOnlyList<TreeNode> nodes, int visibleIndex)
    {
        if (visibleIndex < 0 || visibleIndex >= visible.Count) return -1;
        int depth = nodes[visible[visibleIndex]].Depth;
        for (int i = visibleIndex - 1; i >= 0; i--)
        {
            if (nodes[visible[i]].Depth < depth)
                return i;
        }
        return -1;
    }

    public static GameObject? NodeObject(TreeNode node) => node.UserData as GameObject;
}
