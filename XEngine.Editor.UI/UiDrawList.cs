namespace XEngine.Editor.UI;

/// <summary>Backend-neutral indexed draw. MaterialKey is resolved by the owning renderer.</summary>
public readonly record struct UiDrawCommand(int IndexOffset, int IndexCount, int VertexOffset, uint MaterialKey);
public readonly record struct UiVisibleDraw(UiNodeHandle Node, UiRect Clip, UiDrawCommand Command);

/// <summary>
/// Retains node-local commands and caches their visible preorder sequence. Prepare follows
/// layout and command/geometry updates. Idle Prepare is O(1); a changed tree is traversed,
/// skipping invisible/clipped subtrees. No material sorting: transparent order is preserved.
/// </summary>
public sealed class UiDrawList : IDisposable
{
    private readonly UiNodeStore _nodes;
    private readonly UiGeometryStore<UiDrawCommand> _commands;
    private UiVisibleDraw[] _visible;
    private ulong _revision;
    private bool _changed = true, _disposed;
    public int Count { get; private set; }
    public int VisitedNodes { get; private set; }

    public UiDrawList(UiNodeStore nodes, int nodeCapacity, int commandPages, int commandsPerPage = 4)
    {
        _nodes = nodes;
        _commands = new(nodes, nodeCapacity, commandPages, commandsPerPage);
        _visible = new UiVisibleDraw[_commands.Capacity];
    }

    public bool TrySet(UiNodeHandle node, ReadOnlySpan<UiDrawCommand> commands)
    {
        CheckAlive();
        for (int i = 0; i < commands.Length; i++)
            if (commands[i].IndexOffset < 0 || commands[i].IndexCount < 0 || commands[i].VertexOffset < 0 ||
                (long)commands[i].IndexOffset + commands[i].IndexCount > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(commands));
        if (_commands.TryGet(node, out var current) && _commands.Read(current).SequenceEqual(commands)) return true;
        if (!_commands.TrySet(node, commands, out _)) return false;
        _changed = true; return true;
    }

    public bool Remove(UiNodeHandle node)
    {
        CheckAlive();
        if (!_commands.Remove(node)) return false;
        _changed = true; return true;
    }
    public int PruneRemovedNodes()
    {
        CheckAlive(); int count = _commands.PruneRemovedNodes();
        if (count != 0) _changed = true;
        return count;
    }

    public ReadOnlySpan<UiVisibleDraw> Prepare()
    {
        CheckAlive();
        // Accessing FirstRoot also rejects a disposed node owner, even on an idle lookup.
        var root = _nodes.FirstRoot;
        VisitedNodes = 0;
        if (!_changed && _revision == _nodes.DrawRevision) return _visible.AsSpan(0, Count);
        _changed = true;
        int previousCount = Count;
        Count = 0;
        for (; !root.IsNull; root = _nodes.NextSibling(root))
        {
            var node = root;
            while (true)
            {
                VisitedNodes++;
                var clip = _nodes.GetClip(node);
                if (_nodes.GetStyle(node).Visible && clip.Width > 0 && clip.Height > 0)
                {
                    if (_commands.TryGet(node, out var range))
                    {
                        var commands = _commands.Read(range);
                        for (int i = 0; i < commands.Length; i++)
                            if (commands[i].IndexCount > 0)
                                _visible[Count++] = new(node, clip, commands[i]);
                    }
                    var child = _nodes.FirstChild(node);
                    if (!child.IsNull) { node = child; continue; }
                }
                while (node != root && _nodes.NextSibling(node).IsNull) node = _nodes.Parent(node);
                if (node == root) break;
                node = _nodes.NextSibling(node);
            }
        }
        // Clear obsolete references after a panel closes/hides; retain bounded array storage.
        if (previousCount > Count) Array.Clear(_visible, Count, previousCount - Count);
        _revision = _nodes.DrawRevision; _changed = false;
        return _visible.AsSpan(0, Count);
    }

    private void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _commands.Dispose(); _visible = []; Count = VisitedNodes = 0;
    }
}
