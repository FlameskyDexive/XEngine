// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using XEngine.Echo;

namespace XEngine.Runtime;

public abstract class EngineObject : IDisposable
{
    private static int s_nextID = 1;

    protected int _instanceID;
    public int InstanceID => _instanceID;

    // Asset path if we have one
    [HideInInspector] public string AssetPath = string.Empty;

    /// <summary>
    /// A unique asset identifier. When set, serialization will store only a reference
    /// and deserialization will resolve the object from the <see cref="AssetDatabase"/>.
    /// </summary>
    [HideInInspector] public Guid AssetID = Guid.Empty;

    [HideInInspector] public string Name;

    // Interlocked-guarded rather than a plain bool: a finalizer can now race an explicit Dispose()
    // call from another thread (the finalizer thread runs independently of everything else), so the
    // check-and-set must be atomic or both could pass the guard and double-run OnDispose.
    private int _disposed;
    public bool IsDisposed => _disposed != 0;

    public EngineObject() : this(null) { }

    public EngineObject(string? name = "New Object")
    {
        _instanceID = Interlocked.Increment(ref s_nextID);
        Name = "New" + GetType().Name;
        CreatedInstance();
        Name = name ?? Name;
    }

    public virtual void CreatedInstance() { }

    public virtual void OnValidate() { }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        // Explicit disposal means a finalizer (if this type has one) has nothing left to do.
        GC.SuppressFinalize(this);
        OnDispose();
    }


    public static bool operator ==(EngineObject left, EngineObject right)
    {
        return ReferenceEquals(left, right);
    }
    public static bool operator !=(EngineObject left, EngineObject right) => !(left == right);
    public override bool Equals(object? obj) => this == (obj as EngineObject);
    public override int GetHashCode() => _instanceID;

    public virtual void OnDispose() { }

    // ---- Deferred destruction (prowl-sync M3.2, upstream 01717709) ----

    private static readonly List<EngineObject> s_destroyQueue = [];
    private static List<EngineObject> s_destroySwap = [];

    /// <summary>
    /// Unity-style deferred destruction: the object is queued and disposed at the end of the current
    /// frame's <c>Game.SimulationStep</c>, so gameplay code can destroy objects mid-callback — even
    /// the callback's own GameObject — without pulling state out from under the rest of the pass.
    /// <para/>
    /// Calling this twice queues once (<see cref="IsDisposed"/> objects are skipped); direct
    /// <see cref="Dispose"/> calls remain immediate and idempotent with the deferred path.
    /// </summary>
    public static void Destroy(EngineObject? obj)
    {
        if (obj == null || obj.IsDisposed) return;
        s_destroyQueue.Add(obj);
    }

    /// <summary>
    /// Disposes everything queued by <see cref="Destroy"/> since the last flush. Called once per
    /// frame from <c>Game.SimulationStep</c>; the swap-buffer dance means a Dispose that queues
    /// another Destroy flushes next frame instead of recursing here.
    /// </summary>
    internal static void FlushDestroyed()
    {
        if (s_destroyQueue.Count == 0) return;

        // Move the queued objects to the swap list: a Dispose that queues another Destroy flushes
        // next frame instead of recursing here.
        s_destroySwap.AddRange(s_destroyQueue);
        s_destroyQueue.Clear();
        foreach (EngineObject obj in s_destroySwap)
        {
            try { obj.Dispose(); }
            catch (Exception ex) { Debug.LogError($"[EngineObject] Deferred Destroy threw: {ex.Message}\n{ex.StackTrace}"); }
        }
        s_destroySwap.Clear();
    }

    /// <summary>
    /// Call at the top of any accessor a caller might reasonably use every frame (a texture's Width,
    /// a mesh's VertexCount, ...). Throws loudly if this object was already disposed - the intended
    /// signal for "you're holding a raw reference the asset system didn't know was still in use."
    /// Otherwise touches this object's AssetID as activity, so a raw (non-AssetRef) reference that IS
    /// being read regularly still counts as in-use and won't be idle-swept out from under it - only
    /// an asset nobody reads at all, via any path, goes idle.
    /// </summary>
    protected void EnsureNotDisposed(
        [System.Runtime.CompilerServices.CallerMemberName] string? member = null)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(Name,
                $"'{Name}' ({GetType().Name}) was already disposed when '{member}' was accessed. " +
                "If this asset should stay loaded, hold it via AssetRef<T> (read .Res, or call " +
                ".Touch()) instead of a raw field, or use AssetDatabase.LockToScene/LockPermanent " +
                "for something that must survive being unused for a while.");

        if (AssetID != Guid.Empty)
            AssetDatabase.Touch(AssetID);
    }

    public override string ToString() => Name;

    protected void SerializeHeader(EchoObject compound)
    {
        compound.Add("Name", new(Name));
        compound.Add("AssetPath", new(AssetPath));
        if (AssetID != Guid.Empty)
            compound.Add("AssetID", new(AssetID.ToString()));
    }

    protected void DeserializeHeader(EchoObject value)
    {
        Name = value.Get("Name")?.StringValue ?? string.Empty;
        AssetPath = value.Get("AssetPath")?.StringValue ?? string.Empty;
        if (Guid.TryParse(value.Get("AssetID")?.StringValue, out Guid assetId))
            AssetID = assetId;
    }
}

public static class EngineObjectExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNotValid(this EngineObject obj) => obj is null || obj.IsDisposed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(this EngineObject obj) => obj is not null && !obj.IsDisposed;
}
