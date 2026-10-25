namespace XEngine.Editor.UI;

/// <summary>Single UI-thread admission budget. Charges are supplied estimates, not heap/VRAM readings.</summary>
public sealed class UiMemoryBudget
{
    public UiMemoryBudget(long capacityBytes)
    { ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes); CapacityBytes = capacityBytes; }
    public long CapacityBytes { get; }
    public long ReservedBytes { get; private set; }
    /// <summary>Largest simultaneous admitted payload since budget creation; not measured heap or VRAM.</summary>
    public long PeakReservedBytes { get; private set; }
    public long AvailableBytes => CapacityBytes - ReservedBytes;

    public UiMemoryLease Reserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes > AvailableBytes) throw new InvalidOperationException("UI cache memory budget exhausted.");
        var lease = new UiMemoryLease(this, bytes);
        ReservedBytes += bytes;
        PeakReservedBytes = Math.Max(PeakReservedBytes, ReservedBytes);
        return lease;
    }
    internal void Release(long bytes) => ReservedBytes -= bytes;
}

public sealed class UiMemoryLease : IDisposable
{
    private UiMemoryBudget? _owner;
    internal UiMemoryLease(UiMemoryBudget owner, long bytes) { _owner = owner; Bytes = bytes; }
    public long Bytes { get; }
    public void Dispose()
    {
        if (_owner == null) return;
        _owner.Release(Bytes); _owner = null;
    }
}
