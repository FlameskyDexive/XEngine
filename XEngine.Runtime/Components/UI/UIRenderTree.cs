// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Per-<see cref="GameCanvas"/> store of <see cref="UIRenderItem"/>s. The canvas writes
/// into the tree from <c>RebuildIfDirty</c>; the pipeline reads from it via
/// <see cref="CollectFor"/>. Items are pooled inside the tree so the steady-state
/// allocation rate is zero.
/// </summary>
internal sealed class UIRenderTree
{
    // -------- Active items, in DFS order --------
    private readonly List<UIRenderItem> _items = new(32);

    // Runtime-neutral views used by CanvasRegistry/UniversalRenderPipeline. The UI item remains
    // pooled and owns compatibility behavior; nodes are pooled adapters around those items.
    private readonly List<CanvasRenderNode> _nodes = new(32);
    private readonly Stack<CanvasRenderNode> _freeNodes = new(32);

    // -------- Free list: reusable UIRenderItem instances ----
    private readonly Stack<UIRenderItem> _freeItems = new(32);

    /// <summary>Read-only access for the pipeline / canvas. Safe to enumerate.</summary>
    public IReadOnlyList<UIRenderItem> Items => _items;
    public int Count => _items.Count;

    public void AppendNodes(List<CanvasRenderNode> destination)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            UIRenderItem item = _items[i];
            item.Node.RefreshSubmission(item.Mesh, item.Material, item.Model,
                item.SortKey, item.Surface, item.ClipState);
            destination.Add(item.Node);
        }
    }

    public void AppendNodes(List<IRenderable> destination)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            UIRenderItem item = _items[i];
            item.Node.RefreshSubmission(item.Mesh, item.Material, item.Model,
                item.SortKey, item.Surface, item.ClipState);
            destination.Add(item.Node);
        }
    }

    // -------- Topology mutation --------

    /// <summary>
    /// Returns a fresh-or-recycled <see cref="UIRenderItem"/> *not yet* in the items list.
    /// The caller must invoke <see cref="UIRenderItem.Initialize"/> and then <see cref="Add"/>.
    /// </summary>
    public UIRenderItem RentItem() =>
        _freeItems.Count > 0 ? _freeItems.Pop() : new UIRenderItem();

    public void Add(UIRenderItem item)
    {
        _items.Add(item);
        CanvasRenderNode node = _freeNodes.Count > 0 ? _freeNodes.Pop() : new CanvasRenderNode();
        node.Attach(item, item.Mesh, item.Material, item.Model, item.SortKey, item.Surface, item.ClipState);
        item.Node = node;
        _nodes.Add(node);
    }

    /// <summary>
    /// Releases all current items back to the free list. Called at the start of every
    /// <c>RebuildIfDirty</c>.
    /// </summary>
    public void Clear()
    {
        for (int i = 0; i < _items.Count; i++)
        {
            UIRenderItem it = _items[i];
            // Drop external refs so disabled items don't keep meshes/materials alive.
            it.Owner = null!; it.Canvas = null!;
            it.Mesh = null!;  it.Material = null!;
            it.Props.Clear();
            it.HasClip = false; it.ClipSource = null;
            _freeItems.Push(it);

            CanvasRenderNode node = it.Node;
            node.Detach();
            _freeNodes.Push(node);
            it.Node = null!;
        }
        _items.Clear();
        _nodes.Clear();
    }

    /// <summary>
    /// Sorts items by <see cref="UIRenderItem.SortKey"/>. The neutral adapter is owned by
    /// each item, so the pair remains aligned even when a comparer sees equal keys.
    /// </summary>
    public void SortHierarchical()
    {
        _items.Sort(static (a, b) => a.SortKey.CompareTo(b.SortKey));
    }

    /// <summary>
    /// Patches model matrices for items whose owning Transform.Version changed since last
    /// rebuild. Avoids a full rebuild when only rotation/translation changed.
    /// </summary>
    public void RefreshTransforms()
    {
        for (int i = 0; i < _items.Count; i++)
            _items[i].RefreshModelIfDirty();
    }

    // ============================================================
    // Static scene query - the pipeline's entry point
    // ============================================================

    /// <summary>
    /// Walks every active <see cref="GameCanvas"/> in <paramref name="scene"/>, asks each
    /// to <c>RebuildIfDirty</c>, and appends every item whose surface matches
    /// <paramref name="surface"/> to <paramref name="dst"/>.
    /// </summary>
    /// <remarks>
    /// We can't cache the canvas list across frames: scenes can hot-add/remove canvases
    /// at runtime. The walk is O(active GameObjects), bounded by the editor's tree size,
    /// and only allocates if a canvas needs a rebuild.
    /// </remarks>
    public static void CollectFor(Scene scene, UISurface surface, List<IRenderable> dst)
        => CanvasRegistry.CollectRenderables(scene, surface, dst);

    /// <summary>
    /// Maps the user-facing <see cref="RenderMode"/> to the pipeline-side <see cref="UISurface"/>.
    /// Single source of truth - every other site that needs the conversion calls this.
    /// </summary>
    internal static UISurface ToSurface(RenderMode m) => m switch
    {
        RenderMode.WorldSpace => UISurface.World,
        _                     => UISurface.Overlay,
    };
}
