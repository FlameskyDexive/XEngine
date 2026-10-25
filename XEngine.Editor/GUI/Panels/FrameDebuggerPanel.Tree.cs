// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime.Rendering;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

public sealed partial class FrameDebuggerPanel
{
    private const float SearchHeight = 25f;

    private void DrawLeftColumn(Paper paper, FontFile font, float width, float height)
    {
        using (paper.Column("fd_left").Width(width).Height(height).Enter())
        {
            using (paper.Row("fd_search_row").Width(width).Height(SearchHeight + 8f).Padding(4, 4, 4, 4).Enter())
            {
                Origami.SearchField(paper, "fd_search", _searchText, v => _searchText = v ?? "")
                    .Width(UnitValue.StretchOne)
                    .Show();
            }

            float treeH = height - SearchHeight - 8f;
            FrameCapture? capture = FrameDebugger.LastCapture;
            if (capture == null)
            {
                using (paper.Column("fd_tree_empty").Width(width).Height(treeH).Enter())
                {
                    EditorGUI.EmptyState(paper, "fd_empty",
                        FrameDebugger.IsEnabled
                            ? "Waiting for next frame capture…"
                            : "Enable Frame Debugger to capture the next frame",
                        font);
                }
                return;
            }

            RebuildTreeNodes(capture);
            ApplyPendingExpand();
            Origami.Tree(paper, "fd_tree", width, treeH)
                .Nodes(_treeNodes)
                .RowHeight(20f)
                .ExpandStateSink(_expandStates)
                .IsSelected(n => _selectedGroupId != null
                    ? n.Id == _selectedGroupId
                    : n.UserData is int leaf && leaf == FrameDebugger.SelectedEventIndex)
                // OnSelect, not OnSelectModified: the Tree widget only fires the modified
                // variant when MultiSelect is enabled (Tree.cs SelectRow dispatch).
                .OnSelect(e =>
                {
                    if (e.Node.UserData is int leaf)
                        SelectLeaf(leaf);
                    else if (e.Node.UserData is FrameEventNode group)
                        SelectGroup(e.Node.Id, group);
                })
                .EmptyMessage("No events match the search")
                .Show();
        }
    }

    /// <summary>Rebuilds the flat node list when the capture, the search filter, or the
    /// state-event visibility changed. Node IDs derive from the unfiltered tree's preorder
    /// (groups) and the global leaf index (leaves), so expand state survives filtering;
    /// search mode uses its own ID prefix so its forced expansion never pollutes the
    /// normal-mode expand state.</summary>
    private void RebuildTreeNodes(FrameCapture capture)
    {
        bool searching = !string.IsNullOrEmpty(_searchText);
        if (ReferenceEquals(_treeCapture, capture)
            && _treeFilterText == _searchText
            && _treeShowState == _showStateEvents
            && _treeNodes.Count > 0)
            return;

        _treeCapture = capture;
        _treeFilterText = _searchText;
        _treeShowState = _showStateEvents;
        _treeNodes.Clear();
        _leafParentGroupIds.Clear();

        int preorder = 0;
        AppendNode(capture, capture.Root, 0, searching, rootGroupId: null, ref preorder);
    }

    /// <summary>Depth-first append honoring the search filter (leaves match, ancestors kept)
    /// and the state-event visibility toggle. Returns true when the subtree added any row.</summary>
    private bool AppendNode(
        FrameCapture capture, FrameEventNode node, int depth, bool searching,
        string? rootGroupId, ref int preorder)
    {
        int myPreorder = preorder++;

        if (node.LeafEventIndex is int leafIndex)
        {
            FrameEvent leaf = capture.LeafEvents[leafIndex];
            if (!_showStateEvents && IsStateEvent(leaf.Kind))
                return false;
            if (searching && leaf.Label.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            _treeNodes.Add(new TreeNode
            {
                Id = (searching ? "fds_l" : "fdl_") + leafIndex,
                Label = node.Label,
                Depth = depth,
                IsLeaf = true,
                UserData = leafIndex,
            });
            if (rootGroupId != null)
                _leafParentGroupIds[leafIndex] = rootGroupId;
            return true;
        }

        // Group: append tentatively, roll back when no descendant survives the filter.
        string groupId = (searching ? "fds_g" : "fdg_") + myPreorder;
        bool isCameraGroup = node.Label.Contains(".Render: ", StringComparison.Ordinal)
            || node.Label.StartsWith("Camera ", StringComparison.Ordinal);
        var groupNode = new TreeNode
        {
            Id = groupId,
            Label = node.Label,
            Depth = depth,
            HasChildren = true,
            Icon = isCameraGroup ? EditorIcons.Camera : "",
            Badge = node.Badge > 0 ? node.Badge.ToString() : null,
            BadgeColor = EditorTheme.InkDim,
            UserData = node,
        };
        if (searching)
            groupNode.OverrideExpanded = true;
        else if (depth <= 1 && !_expandStates.ContainsKey(groupId))
            groupNode.OverrideExpanded = true; // Unity default: root + camera groups start open
        int myRow = _treeNodes.Count;
        _treeNodes.Add(groupNode);

        bool any = false;
        for (int i = 0; i < node.Children.Count; i++)
            any |= AppendNode(capture, node.Children[i], depth + 1, searching, groupId, ref preorder);

        if (!any)
        {
            _treeNodes.RemoveAt(myRow);
            return false;
        }
        return true;
    }

    private static bool IsStateEvent(FrameEventKind kind) =>
        kind is FrameEventKind.SetRenderTarget or FrameEventKind.UpdateResource;

    /// <summary>Unity behavior: clicking a group row selects it and truncates the replay
    /// after the group's LAST event, so the game view shows everything the group rendered.
    /// Expansion stays on the chevron (handled by the Tree widget itself).</summary>
    private void SelectGroup(string nodeId, FrameEventNode group)
    {
        int lastLeaf = LastLeafIndex(group);
        if (lastLeaf < 0) return;
        SelectLeaf(lastLeaf);
        _selectedGroupId = nodeId;
    }

    private static int LastLeafIndex(FrameEventNode node)
    {
        if (node.LeafEventIndex is int leaf)
            return leaf;
        int last = -1;
        for (int i = 0; i < node.Children.Count; i++)
        {
            int childLast = LastLeafIndex(node.Children[i]);
            if (childLast > last)
                last = childLast;
        }
        return last;
    }

    /// <summary>Expansion writes are deferred one frame (the Tree widget owns element storage
    /// during its pass); applied here on the next rebuild-stable node list.</summary>
    private void ApplyPendingExpand()
    {
        if (_pendingExpandStates.Count == 0) return;
        for (int i = 0; i < _treeNodes.Count; i++)
        {
            if (_pendingExpandStates.TryGetValue(_treeNodes[i].Id, out bool value))
                _treeNodes[i].OverrideExpanded = value;
        }
        _pendingExpandStates.Clear();
    }
}
