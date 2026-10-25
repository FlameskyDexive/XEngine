// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace XEngine.Runtime;

/// <summary>
/// Base class for data assets that live independently of GameObjects (the XEngine equivalent of
/// Unity's <c>ScriptableObject</c>). Subclass this, decorate it with
/// <see cref="CreateAssetMenuAttribute"/>, and create instances via the Assets &#9656; Create
/// menu or <see cref="CreateInstance{T}"/>. Assets are persisted to <c>.asset</c> files in
/// XEngine's Echo text format and resolved at runtime through <see cref="AssetDatabase"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> <see cref="OnEnable"/> fires exactly once when an instance becomes resident
/// in the asset cache (on first load via <see cref="AssetBackendBase"/>, or immediately when
/// produced by <see cref="CreateInstance{T}"/>). <see cref="OnDisable"/> fires once when the
/// instance is disposed - either explicitly, or by the asset backend's idle sweep evicting an
/// unreferenced asset. This mirrors Unity's ScriptableObject contract.
/// </para>
/// <para>
/// Constructing an instance with <c>new</c> does <b>not</b> fire <see cref="OnEnable"/> - use
/// <see cref="CreateInstance{T}"/> for that, which is the path Unity's own
/// <c>ScriptableObject.CreateInstance</c> takes. The distinction matters for assets that do
/// one-time setup in <see cref="OnEnable"/>.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="OnEnable"/> runs synchronously on whichever thread loaded the
/// asset - usually the <see cref="AssetLoader"/> background thread, not the main thread. Do not
/// touch main-thread-only APIs (rendering, input, GUI) from <see cref="OnEnable"/>; defer that
/// work to <see cref="OnValidate"/> or a per-frame system. <see cref="OnDisable"/> runs on
/// whichever thread triggered disposal (often the idle-sweep thread).
/// </para>
/// </remarks>
public abstract class ScriptableObject : EngineObject
{
    // Callbacks can originate on the load and disposal threads. The lock preserves callback order,
    // while the volatile state keeps the diagnostic IsEnabled property cheap and race-free.
    private readonly object _lifecycleLock = new();
    private int _enabled; // 0 = disabled, 1 = enabled

    /// <summary>True while <see cref="OnEnable"/> has fired and <see cref="OnDisable"/> has not.</summary>
    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    /// <summary>
    /// Called once when this instance becomes resident in the asset cache, or immediately when
    /// created via <see cref="CreateInstance{T}"/>. Override to do one-time setup (cache derived
    /// values, subscribe to events, validate state). See the class remarks for threading notes.
    /// </summary>
    protected virtual void OnEnable() { }

    /// <summary>
    /// Called once when this instance is disposed (explicitly or via idle eviction). Override to
    /// release resources acquired in <see cref="OnEnable"/>. Always paired with a prior
    /// <see cref="OnEnable"/>; never fires if <see cref="OnEnable"/> never did.
    /// </summary>
    protected virtual void OnDisable() { }

    /// <summary>
    /// Called by the editor when the asset is modified and saved, and on first load. Override to
    /// validate or normalize serialized fields and recompute derived state.
    /// </summary>
    public override void OnValidate() { }

    /// <summary>
    /// Create an instance of <typeparamref name="T"/> and fire <see cref="OnEnable"/> immediately.
    /// This is the runtime equivalent of Unity's <c>ScriptableObject.CreateInstance&lt;T&gt;</c>:
    /// unlike <c>new T()</c>, it activates the instance so <see cref="OnEnable"/> runs.
    /// </summary>
    public static T CreateInstance<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor
            | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>() where T : ScriptableObject
    {
        return (T)CreateInstance(typeof(T));
    }

    /// <summary>
    /// Create an instance of <paramref name="type"/> and fire <see cref="OnEnable"/> immediately.
    /// <paramref name="type"/> must be a non-abstract <see cref="ScriptableObject"/> subclass with a
    /// parameterless constructor.
    /// </summary>
    public static ScriptableObject CreateInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor
            | DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(ScriptableObject).IsAssignableFrom(type))
            throw new ArgumentException($"Type '{type.FullName}' does not derive from ScriptableObject.", nameof(type));
        if (type.IsAbstract)
            throw new ArgumentException($"Cannot create an instance of abstract type '{type.FullName}'.", nameof(type));
        if (type.ContainsGenericParameters)
            throw new ArgumentException($"Cannot create an instance of open generic type '{type.FullName}'.", nameof(type));

        var instance = (ScriptableObject)Activator.CreateInstance(type, nonPublic: true)!;
        instance.NotifyEnabled();
        return instance;
    }

    /// <summary>
    /// Create an instance of the named <see cref="ScriptableObject"/> subclass and fire
    /// <see cref="OnEnable"/> immediately. <paramref name="typeName"/> is resolved via
    /// <see cref="RuntimeUtils.ResolveType"/>; returns null if the type cannot be found.
    /// </summary>
    [RequiresUnreferencedCode("Type names are resolved through reflection and cannot be statically analyzed.")]
    public static ScriptableObject? CreateInstance(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        var type = RuntimeUtils.ResolveType(typeName);
        if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type)
            || type.IsAbstract || type.ContainsGenericParameters)
            return null;

        return CreateInstance(type);
    }

    /// <summary>
    /// Fire <see cref="OnEnable"/> if it has not already fired. Idempotent. Called by the asset
    /// backend (via <see cref="AssetBackendBase.SetLoaded"/>) when an instance enters the cache,
    /// and by <see cref="CreateInstance{T}"/>.
    /// </summary>
    internal void NotifyEnabled()
    {
        lock (_lifecycleLock)
        {
            if (IsDisposed || _enabled != 0) return;
            Volatile.Write(ref _enabled, 1);
            try { OnEnable(); }
            catch (Exception ex) { Debug.LogError($"OnEnable threw on ScriptableObject '{Name}' ({GetType().Name}): {ex}"); }
        }
    }

    /// <summary>
    /// Fire <see cref="OnDisable"/> if <see cref="OnEnable"/> previously fired. Idempotent. Called
    /// from <see cref="OnDispose"/> (which both explicit <see cref="IDisposable.Dispose"/> and the idle sweep
    /// route through), so every disposal path triggers it exactly once.
    /// </summary>
    internal void NotifyDisabled()
    {
        lock (_lifecycleLock)
        {
            if (_enabled == 0) return;
            Volatile.Write(ref _enabled, 0);
            try { OnDisable(); }
            catch (Exception ex) { Debug.LogError($"OnDisable threw on ScriptableObject '{Name}' ({GetType().Name}): {ex}"); }
        }
    }

    /// <summary>
    /// EngineObject disposal hook. Fires <see cref="OnDisable"/> (no-op if never enabled).
    /// ScriptableObject subclasses use <see cref="OnDisable"/> for cleanup so this hook cannot be
    /// overridden without preserving the lifecycle contract.
    /// </summary>
    public sealed override void OnDispose() => NotifyDisabled();
}
