using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XEngine.Runtime;

namespace XEngine.Editor.UI.Rendering;

public readonly record struct UiUploadTicket
{
    internal UiUploadTicket(object owner, ulong serial, int ranges, long bytes)
    { Owner = owner; Serial = serial; RangeCount = ranges; Bytes = bytes; }
    internal object? Owner { get; }
    internal ulong Serial { get; }
    public int RangeCount { get; }
    public long Bytes { get; }
    public bool IsValid => Owner != null;
}

/// <summary>
/// Fixed-capacity RHI buffer plus CPU mirror. Encodes byte-offset updates into the caller's
/// ordered command stream. The backend owns native synchronization/disposal; submission
/// acknowledgment is NOT a completion fence or permission to retire unrelated resources.
/// </summary>
public sealed class UiRhiUploadBuffer<T> : IDisposable where T : unmanaged
{
    private readonly object _identity = new();
    private readonly object _device;
    private readonly IUiUploadSource<T> _mirror;
    private readonly UiUploadBuffer<T>? _ownedMirror;
    private UiUploadRange[] _pending;
    private ulong _serial;
    private int _pendingCount;
    private bool _batchOpen, _disposed;

    /// <summary>Borrowed by vertex arrays/draw recording; only this owner disposes it.</summary>
    public GraphicsBuffer Buffer { get; }
    public int Capacity => _mirror.Capacity;
    public int DirtyPageCount => _mirror.DirtyPageCount;

    public UiRhiUploadBuffer(BufferType type, int capacity, int pageElements = 64)
        : this(type, new UiUploadBuffer<T>(capacity, pageElements), true) { }

    /// <summary>Borrow a source without duplicating its CPU storage. Dispose this GPU owner first.</summary>
    public UiRhiUploadBuffer(BufferType type, IUiUploadSource<T> source)
        : this(type, source, false) { }

    private UiRhiUploadBuffer(BufferType type, IUiUploadSource<T> source, bool ownsSource)
    {
        ArgumentNullException.ThrowIfNull(source);
        _mirror = source;
        _ownedMirror = ownsSource ? (UiUploadBuffer<T>)source : null;
        try
        {
            _device = Graphics.Device ?? throw new InvalidOperationException("Initialize a graphics device before creating a UI buffer.");
            if (source.Capacity < 1 || source.PageCount < 1 ||
                (ulong)source.Capacity * (ulong)Unsafe.SizeOf<T>() > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(source));
            _pending = new UiUploadRange[source.PageCount];
            // CreateBuffer encodes a copy of initial data; it establishes storage capacity
            // before any later offset update. It must not start with a zero-byte allocation.
            Buffer = new GraphicsBuffer(type, MemoryMarshal.AsBytes(_mirror.Read(0, source.Capacity)), true);
            while (_mirror.TryGetPending(out var initial)) _mirror.Acknowledge(initial);
        }
        catch { _ownedMirror?.Dispose(); throw; }
    }

    public bool Write(int offset, ReadOnlySpan<T> data)
    {
        CheckUsable();
        if (_ownedMirror == null) throw new InvalidOperationException("Write through the borrowed source's owner.");
        return _ownedMirror.Write(offset, data);
    }

    public UiUploadTicket EncodePending(CommandBuffer command, int maxPagesPerRange = int.MaxValue)
    {
        CheckUsable(); ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPagesPerRange, 1);
        if (_batchOpen) throw new InvalidOperationException("Complete the previous upload submission first.");
        if (_mirror.DirtyPageCount == 0) return default;
        if (_serial == ulong.MaxValue) throw new InvalidOperationException("Upload serial exhausted.");
        _serial++; _pendingCount = 0;
        long bytes = 0;
        try
        {
            int cursor = 0;
            while (_mirror.TryGetPending(out var range, maxPagesPerRange, cursor))
            {
                uint offset = checked((uint)((long)range.Offset * Unsafe.SizeOf<T>()));
                command.UpdateBuffer(Buffer, _mirror.ReadPending(range), offset);
                _pending[_pendingCount++] = range;
                bytes += (long)range.Count * Unsafe.SizeOf<T>();
                cursor = range.Offset + range.Count;
            }
            _batchOpen = true;
            return new(_identity, _serial, _pendingCount, bytes);
        }
        catch
        {
            Array.Clear(_pending, 0, _pendingCount); _pendingCount = 0;
            // Caller must discard the partly encoded command buffer; mirror stays dirty.
            throw;
        }
    }

    public void CompleteSubmission(UiUploadTicket ticket, bool succeeded)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_batchOpen || !ReferenceEquals(ticket.Owner, _identity) || ticket.Serial != _serial)
            throw new ArgumentException("Stale, null or foreign upload ticket.", nameof(ticket));
        if (succeeded)
            for (int i = 0; i < _pendingCount; i++) _mirror.Acknowledge(_pending[i]);
        Array.Clear(_pending, 0, _pendingCount); _pendingCount = 0; _batchOpen = false;
    }

    private void CheckUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(_device, Graphics.Device) || Graphics.IsDeviceLost)
            throw new InvalidOperationException("Graphics device changed; recreate the UI RHI buffer.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_batchOpen) throw new InvalidOperationException("Submit or cancel the encoded upload before disposal.");
        Buffer.Dispose(); _ownedMirror?.Dispose(); _pending = []; _disposed = true;
    }
}
