using System;
using System.Collections.Generic;
using XEngine.Editor.UI;
using XEngine.OrigamiUI;

namespace XEngine.Editor.GUI.Panels;

internal sealed class HierarchyRowIndex : ITreeRowIndex
{
    private readonly List<TreeNode> _nodes;
    private readonly Dictionary<string, int> _slots;
    private readonly Dictionary<string, bool> _resolved;
    private readonly UiTreeVisibilityIndex _index;
    private readonly int[] _parents;
    private int _dropSlot = -1;
    public int LastDropRowsTouched { get; private set; }
    /// <summary>Largest row list the index accepts. The segment tree costs 48 B per node, so a full
    /// million-row scene stays under 50 MiB; HierarchyPanel falls back to the whole-list tree above it
    /// instead of throwing from the frame loop.</summary>
    public const int MaxNodes = 1_000_000;
    public HierarchyRowIndex(List<TreeNode> nodes, Dictionary<string, bool> stored, Dictionary<string, bool> resolved)
    {
        if (nodes.Count > MaxNodes) throw new ArgumentException($"Hierarchy index supports at most {MaxNodes:N0} nodes.", nameof(nodes));
        _nodes = nodes; _resolved = resolved; _slots = new(nodes.Count, StringComparer.Ordinal);
        int[] parents = new int[nodes.Count], stack = new int[nodes.Count];
        _parents = parents;
        int top = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            while (top > 0 && nodes[stack[top - 1]].Depth >= node.Depth) top--;
            parents[i] = top == 0 ? -1 : stack[top - 1]; stack[top++] = i;
            _slots.Add(node.Id, i);
            if (node.DropIndicator.HasValue) _dropSlot = i;
        }
        _index = new UiTreeVisibilityIndex(parents, MaxNodes);
        resolved.Clear();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            bool raw = node.OverrideExpanded ?? (stored.TryGetValue(node.Id, out bool value) ? value : node.DefaultExpanded);
            if (node.OverrideExpanded.HasValue) stored[node.Id] = raw;
            SetExpanded(node.Id, raw);
        }
    }
    public int Count => _index.VisibleCount;
    public int GetNodeIndex(int visibleRank) => _index.GetNodeIndex(visibleRank);
    public bool IsExpanded(int nodeIndex) => _resolved[_nodes[nodeIndex].Id];
    public int GetParentIndex(int nodeIndex) => _parents[nodeIndex];
    public int GetVisibleRank(string nodeId) => _slots.TryGetValue(nodeId, out int slot) ? _index.GetVisibleRank(slot) : -1;
    public int GetVisibleRank(int nodeIndex) => _index.GetVisibleRank(nodeIndex);
    public int GetVisibleDescendantCount(int nodeIndex) => _index.GetVisibleDescendantCount(nodeIndex);
    public long FoldRevision { get; private set; }
    public int LastFoldNodeIndex { get; private set; } = -1;
    public bool LastFoldExpanded { get; private set; }
    public void SetTransientVisible(int nodeIndex, bool visible) => _index.SetTransientVisible(nodeIndex, visible);
    public void UpdateDropIndicator(string? targetId, float normalizedY)
    {
        int slot = targetId != null && _slots.TryGetValue(targetId, out int found) ? found : -1;
        TreeDropPosition? position = slot < 0 ? null : normalizedY < .25f ? TreeDropPosition.Above
            : normalizedY > .75f ? (IsExpanded(slot) ? TreeDropPosition.IntoFirst : TreeDropPosition.Below)
            : TreeDropPosition.Into;
        LastDropRowsTouched = 0;
        if (_dropSlot == slot && (slot < 0 || _nodes[slot].DropIndicator == position)) return;
        if (_dropSlot >= 0 && _dropSlot != slot) { _nodes[_dropSlot].DropIndicator = null; LastDropRowsTouched++; }
        if (slot >= 0) { _nodes[slot].DropIndicator = position; LastDropRowsTouched++; }
        _dropSlot = slot;
    }
    public void SetExpanded(string nodeId, bool expanded)
    {
        if (!_slots.TryGetValue(nodeId, out int slot)) return;
        var node = _nodes[slot];
        bool resolved = node.HasChildren && !node.IsLeaf && (node.OverrideExpanded ?? expanded);
        long revision = _index.Revision;
        _index.SetExpanded(slot, resolved); _resolved[nodeId] = resolved;
        if (_index.Revision != revision) { FoldRevision++; LastFoldNodeIndex = slot; LastFoldExpanded = resolved; }
    }
}
