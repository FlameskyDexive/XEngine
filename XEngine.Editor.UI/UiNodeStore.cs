namespace XEngine.Editor.UI;

/// <summary>
/// Single UI-thread, bounded node ownership. Allocation occurs only on capacity growth.
/// Handles retain an identity token, never the store or its payload. No implicit detached nodes:
/// a node without a parent is a layout root, with explicit sibling ordering.
/// </summary>
public sealed partial class UiNodeStore : IDisposable
{
    private struct Slot
    {
        public ulong Generation;
        public bool Alive;
        public int Parent, First, Last, Previous, Next;
        public UiInvalidation Dirty;
        public ulong PaintVersion;
        public UiStyle Style;
        public UiSize Content, Desired;
        public UiRect Bounds, Clip, RootBounds;
        public float ScrollX, ScrollY;
        public UiControlKind Control;
        public float Value;
        public double StretchSize;
        public bool StretchFrozen;
        public bool MeasureBranch, ArrangeBranch;
    }

    private readonly object _identity = new();
    private Slot[] _slots;
    private readonly int _maximumCapacity;
    private int _used, _free = -1, _firstRoot = -1, _lastRoot = -1;
    private bool _disposed;

    public UiNodeStore(int initialCapacity = 256, int maximumCapacity = 1_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCapacity, initialCapacity);
        _slots = new Slot[initialCapacity];
        _maximumCapacity = maximumCapacity;
    }

    public int Count { get; private set; }
    public int Capacity => _slots.Length;
    /// <summary>Changes affecting draw ordering/clips; compare after layout. Paint data is owned by renderers.</summary>
    public ulong DrawRevision { get; private set; }
    public ulong PaintRevision { get; private set; }
    public UiNodeHandle Focused { get; private set; }
    public UiNodeHandle Captured { get; private set; }
    public UiNodeHandle FirstRoot { get { ThrowIfDisposed(); return Handle(_firstRoot); } }
    public bool IsAlive(UiNodeHandle handle) => !_disposed && ReferenceEquals(handle.Owner, _identity)
        && (uint)handle.Index < (uint)_used && _slots[handle.Index].Alive
        && _slots[handle.Index].Generation == handle.Generation;

    public UiNodeHandle Create(UiNodeHandle parent = default)
    {
        ThrowIfDisposed();
        int parentIndex = parent.IsNull ? -1 : Validate(parent);
        int index;
        if (_free >= 0)
        {
            index = _free;
            _free = _slots[index].Next;
        }
        else
        {
            if (_used == _slots.Length)
            {
                if (_used == _maximumCapacity) throw new InvalidOperationException("UI node capacity exhausted.");
                Array.Resize(ref _slots, (int)Math.Min((long)_maximumCapacity, (long)_slots.Length * 2));
            }
            index = _used++;
            _slots[index].Generation = 1;
        }
        ulong generation = _slots[index].Generation;
        _slots[index] = new Slot { Alive = true, Generation = generation, Parent = -1,
            First = -1, Last = -1, Previous = -1, Next = -1, Dirty = UiInvalidation.All, Style = UiStyle.Default };
        AttachLast(index, parentIndex);
        MarkPaint(index);
        Count++;
        if (parentIndex >= 0) InvalidateIndex(parentIndex, UiInvalidation.All);
        return Handle(index);
    }

    public UiNodeHandle Parent(UiNodeHandle node) => Handle(_slots[Validate(node)].Parent);
    public UiNodeHandle FirstChild(UiNodeHandle node) => Handle(_slots[Validate(node)].First);
    public UiNodeHandle NextSibling(UiNodeHandle node) => Handle(_slots[Validate(node)].Next);
    public UiInvalidation GetInvalidation(UiNodeHandle node) => _slots[Validate(node)].Dirty;

    public void Invalidate(UiNodeHandle node, UiInvalidation flags) => InvalidateIndex(Validate(node), flags);

    // Used by individual stages after processing; clearing a parent never clears its children.
    public void Acknowledge(UiNodeHandle node, UiInvalidation flags)
        => _slots[Validate(node)].Dirty &= ~flags;

    public void SetFocus(UiNodeHandle node)
    {
        ThrowIfDisposed();
        if (!node.IsNull && (!CanInteract(Validate(node)) || !_slots[node.Index].Style.Focusable))
            throw new ArgumentException("Focus requires a visible, enabled, focusable node.", nameof(node));
        if (Focused == node) return;
        if (!Focused.IsNull) Invalidate(Focused, UiInvalidation.Paint);
        Focused = node;
        if (!node.IsNull) Invalidate(node, UiInvalidation.Paint);
    }

    public void SetCapture(UiNodeHandle node)
    {
        ThrowIfDisposed();
        if (!node.IsNull && !CanInteract(Validate(node)))
            throw new ArgumentException("Capture requires a visible, enabled node.", nameof(node));
        if (Captured == node) return;
        // Capture loss cancels the originating gesture, even if capture later returns.
        // Invalidate before clearing so cached pressed visuals are rebuilt.
        if (IsAlive(_pressed)) Invalidate(_pressed, UiInvalidation.Paint);
        _pressed = default;
        Captured = node;
    }

    /// <summary>Move to the end of a parent's children (or root list). Validation is atomic.</summary>
    public void Reparent(UiNodeHandle node, UiNodeHandle parent = default)
    {
        int index = Validate(node), target = parent.IsNull ? -1 : Validate(parent);
        for (int ancestor = target; ancestor >= 0; ancestor = _slots[ancestor].Parent)
            if (ancestor == index) throw new ArgumentException("Reparent would create a UI cycle.", nameof(parent));
        int oldParent = _slots[index].Parent;
        if (oldParent == target && _slots[index].Next < 0) return;
        Detach(index);
        AttachLast(index, target);
        if (oldParent >= 0) InvalidateIndex(oldParent, UiInvalidation.All);
        InvalidateIndex(index, UiInvalidation.All);
        if (!CanInteract(index)) ClearInputInSubtree(index);
    }

    /// <summary>Iterative postorder removal; no recursion or scratch allocation for deep trees.</summary>
    public void Remove(UiNodeHandle node)
    {
        int root = Validate(node), parent = _slots[root].Parent;
        Detach(root);
        int index = root;
        while (true)
        {
            while (_slots[index].First >= 0) index = _slots[index].First;
            int next = _slots[index].Next, up = _slots[index].Parent;
            bool done = index == root;
            if (!done) Detach(index);
            Release(index);
            if (done) break;
            index = next >= 0 ? next : up;
        }
        if (parent >= 0) InvalidateIndex(parent, UiInvalidation.All);
    }

    private void InvalidateIndex(int index, UiInvalidation flags)
    {
        if ((flags & (UiInvalidation.Measure | UiInvalidation.Arrange | UiInvalidation.Structure)) != 0)
            AdvanceDrawRevision();
        if ((flags & UiInvalidation.Structure) != 0) flags |= UiInvalidation.All;
        if ((flags & UiInvalidation.Measure) != 0) flags |= UiInvalidation.Arrange | UiInvalidation.Paint | UiInvalidation.HitTest;
        if ((flags & UiInvalidation.Arrange) != 0) flags |= UiInvalidation.Paint | UiInvalidation.HitTest;
        _slots[index].Dirty |= flags;
        if ((flags & UiInvalidation.Paint) != 0) MarkPaint(index);
        if ((flags & (UiInvalidation.Measure | UiInvalidation.Arrange)) == 0) return;
        bool measure = (flags & UiInvalidation.Measure) != 0;
        bool dependency = measure;
        for (int parent = _slots[index].Parent; parent >= 0; parent = _slots[parent].Parent)
        {
            ref var ancestor = ref _slots[parent];
            ancestor.MeasureBranch |= measure;
            ancestor.ArrangeBranch = true;
            if (!dependency) continue;
            ancestor.Dirty |= UiInvalidation.Measure | UiInvalidation.Arrange | UiInvalidation.Paint | UiInvalidation.HitTest;
            MarkPaint(parent);
            // Both dimensions are independent of children: still visit this branch, but
            // do not remeasure/rearrange ancestors merely to reach pending descendants.
            if (ancestor.Style.Width.Sizing == UiSizing.Fixed && ancestor.Style.Height.Sizing == UiSizing.Fixed)
                dependency = false;
        }
    }

    private void AttachLast(int index, int parent)
    {
        AdvanceDrawRevision();
        int previous = parent < 0 ? _lastRoot : _slots[parent].Last;
        _slots[index].Parent = parent;
        _slots[index].Previous = previous;
        _slots[index].Next = -1;
        if (previous >= 0) _slots[previous].Next = index;
        else if (parent < 0) _firstRoot = index;
        else _slots[parent].First = index;
        if (parent < 0) _lastRoot = index;
        else _slots[parent].Last = index;
    }

    private void Detach(int index)
    {
        AdvanceDrawRevision();
        var node = _slots[index];
        if (node.Previous >= 0) _slots[node.Previous].Next = node.Next;
        else if (node.Parent < 0) _firstRoot = node.Next;
        else _slots[node.Parent].First = node.Next;
        if (node.Next >= 0) _slots[node.Next].Previous = node.Previous;
        else if (node.Parent < 0) _lastRoot = node.Previous;
        else _slots[node.Parent].Last = node.Previous;
        _slots[index].Parent = _slots[index].Previous = _slots[index].Next = -1;
    }

    private void Release(int index)
    {
        var handle = Handle(index);
        if (Focused == handle) Focused = default;
        if (Captured == handle) Captured = default;
        if (_pressed == handle) _pressed = default;
        ulong generation = _slots[index].Generation;
        _slots[index] = default;
        // Retire an exhausted slot permanently rather than wrap and revive an ancient handle.
        if (generation < ulong.MaxValue)
        {
            _slots[index].Generation = generation + 1;
            _slots[index].Next = _free;
            _free = index;
        }
        Count--;
    }

    private UiNodeHandle Handle(int index) => index < 0 ? default : new(_identity, index, _slots[index].Generation);
    private int Validate(UiNodeHandle node)
    {
        ThrowIfDisposed();
        if (!IsAlive(node)) throw new ArgumentException("Stale, null, or foreign UI node handle.", nameof(node));
        return node.Index;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void AdvanceDrawRevision() => DrawRevision = checked(DrawRevision + 1);
    private void MarkPaint(int index)
    {
        PaintRevision = checked(PaintRevision + 1);
        _slots[index].PaintVersion = PaintRevision;
        _slots[index].Dirty |= UiInvalidation.Paint;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slots = [];
        Count = _used = 0;
        _free = _firstRoot = _lastRoot = -1;
        Focused = Captured = _pressed = default;
    }
}
