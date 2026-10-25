// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace XEngine.Runtime.Audio;

/// <summary>
/// Runtime twin of an <see cref="AudioMixer"/> asset (N1). The asset stays pure authoring data;
/// this instance owns everything that changes at runtime: compiled group indices, validated
/// hierarchy, snapshot overrides and chain gains. AudioSources register here and are notified
/// when anything they listen to changes, so playing sources react to volume/mute/solo/snapshot
/// edits without polling the asset.
/// <para>
/// Direct mutations of the asset's groups (legacy code paths, editor inspector edits) are picked
/// up by per-frame change detection in <see cref="Tick"/> — a fingerprint pass with no steady-state
/// allocation. Hierarchy validation never throws: unknown parents fall back to Master, parent
/// cycles are cut at compile time, duplicate names resolve to the first group (id lookups win).
/// </para>
/// </summary>
public sealed class AudioMixerInstance
{
    private static readonly List<AudioMixerInstance> s_live = new();
    private static readonly object s_liveLock = new();
    private static readonly ConditionalWeakTable<AudioMixer, AudioMixerInstance> s_byAsset = new();

    private readonly AudioMixer _asset;
    private readonly AudioMixerBusGraph _busGraph;

    // Compiled topology (rebuilt only on structural change).
    private int[] _parentIndex = Array.Empty<int>();
    private string[] _ids = Array.Empty<string>();
    private string[] _names = Array.Empty<string>();
    private Dictionary<string, int> _byId = new();
    private Dictionary<string, int> _byName = new();

    // Effective runtime state (asset default or instance override).
    private float[] _effectiveDB = Array.Empty<float>();
    private bool[] _effectiveMute = Array.Empty<bool>();
    private float[] _chainGain = Array.Empty<float>();
    private bool _anySolo;

    // N3 layering: the transition tween and duck envelopes land on top of the base layer.
    // Final effective dB = (transition layer active ? _transitionDB : _effectiveDB) - duck.
    private readonly AudioMixerAutomation _automation;
    private float[] _transitionDB = Array.Empty<float>();
    private bool _transitionLayerActive;

    // Active snapshot overrides: index → dB (instance-owned; never written back to the asset).
    private readonly Dictionary<int, float> _snapshotOverrides = new();
    private string _activeSnapshot = "";

    // Legacy change detection: fingerprints of the asset's group table. Value edits (volume,
    // mute, solo) refresh in place; structural edits (id/name/parent, group count) recompile.
    private long[] _valueFingerprints = Array.Empty<long>();
    private long[] _structFingerprints = Array.Empty<long>();
    private int _assetGroupCount = -1;
    private bool _structuralDirty = true;

    // Preallocated depth-first stack for chain walks (zero-alloc steady state).
    private int[] _walkStack = Array.Empty<int>();

    /// <summary>Bumped on every effective-state change; listeners use the event instead.</summary>
    public uint Version { get; private set; }

    /// <summary>Fired (main thread) when chain gains, mute/solo or the snapshot change.
    /// AudioSources recompute their cached gain here.</summary>
    public event Action? Changed;

    public AudioMixer Asset => _asset;
    public int GroupCount => _names.Length;
    public string ActiveSnapshot => _activeSnapshot;
    public bool AnySolo => _anySolo;

    private AudioMixerInstance(AudioMixer asset)
    {
        _asset = asset;
        _busGraph = new AudioMixerBusGraph(this);
        _automation = new AudioMixerAutomation(this);
        Recompile();
    }

    /// <summary>Snapshot transitions and duck envelopes for this mixer (N3).</summary>
    public AudioMixerAutomation Automation => _automation;

    /// <summary>Base effective volume (dB): asset default or snapshot override, before any
    /// transition tween or duck reduction.</summary>
    public float GetBaseVolumeDB(int index) => IsValid(index) ? _effectiveDB[index] : AudioMixerMath.MinVolumeDB;

    internal void SetTransitionDB(int index, float db)
    {
        if (index < 0 || index >= _transitionDB.Length) return;
        _transitionDB[index] = db;
        _transitionLayerActive = true;
    }

    internal void ClearTransitionLayer() => _transitionLayerActive = false;

    /// <summary>Tween-layer value when a transition is running, else the base value (no duck).</summary>
    internal float GetTweenDB(int index) => _transitionLayerActive ? _transitionDB[index] : GetBaseVolumeDB(index);

    internal bool HasSnapshot(string name)
    {
        foreach (var snapshot in _asset.Snapshots)
            if (snapshot.Name == name) return true;
        return false;
    }

    /// <summary>Recomputes chain gains (and native bus gains) for the current duck/transition
    /// state; called by the automation when an envelope moved this frame.</summary>
    internal void RefreshEffectiveFromAutomation()
    {
        RecomputeChains();
        Notify();
    }

    /// <summary>True when a native bus graph is currently built for this mixer: sources route
    /// through real group nodes and group gains apply once on the bus.</summary>
    public bool UsesNativeBus => _busGraph.IsBuilt;

    /// <summary>Builds the native bus graph when the backend can host one. Cheap no-op once
    /// built (or while no native device exists).</summary>
    public bool EnsureNativeGraph() => _busGraph.EnsureBuilt(_names.Length);

    /// <summary>Attaches a source's chain-end node output to the named group's bus. Returns
    /// false (caller keeps the endpoint attachment + managed gain) when no native graph exists.</summary>
    public bool AttachSourceEffect(IntPtr sourceEndNode, string idOrName)
    {
        if (sourceEndNode == IntPtr.Zero || !_busGraph.IsBuilt)
            return false;
        int idx = GetIndex(idOrName);
        if (idx < 0) idx = IndexOfName(AudioMixer.MasterGroupName);
        if (idx < 0) return false;
        return _busGraph.AttachSource(sourceEndNode, idx);
    }

    /// <summary>Latest post-fader Peak/RMS of a group's summed signal (0 when no native graph).</summary>
    public void GetMetering(int index, out float peak, out float rms)
        => _busGraph.GetMetering(index, out peak, out rms);

    /// <summary>Returns and clears the group's clip latch (a callback peak ≥ 0.999).</summary>
    public bool TakeClip(int index) => _busGraph.TakeClip(index);

    internal static void OnBackendInitialized()
    {
        lock (s_liveLock)
        {
            foreach (var instance in s_live)
            {
                // A previous engine's native nodes may still hold metering callbacks that
                // now point into the new device read path — tear them down (uninit) before
                // the next build replaces the graph.
                instance._busGraph.Teardown();
                instance._busGraph.Invalidate();
            }
        }
    }

    internal static void OnBackendShutdown()
    {
        // The engine is still alive here: proper node teardown, not just pointer invalidation.
        lock (s_liveLock)
        {
            foreach (var instance in s_live)
            {
                instance._busGraph.Teardown();
                instance._busGraph.Invalidate();
            }
        }
    }

    /// <summary>Returns the live instance for an asset, compiling it on first use.</summary>
    public static AudioMixerInstance Get(AudioMixer asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (s_byAsset.TryGetValue(asset, out var existing))
            return existing;

        var instance = new AudioMixerInstance(asset);
        s_byAsset.Add(asset, instance);
        lock (s_liveLock)
        {
            s_live.Add(instance);
        }
        return instance;
    }

    /// <summary>Drops the instance for an asset (asset unloaded/disposed).</summary>
    public static void Forget(AudioMixer asset) => s_byAsset.Remove(asset);

    /// <summary>Main-thread per-frame tick: picks up legacy direct mutations of the asset's
    /// groups. No allocation when nothing changed.</summary>
    public static void TickAll()
    {
        lock (s_liveLock)
        {
            for (int i = s_live.Count - 1; i >= 0; i--)
            {
                var instance = s_live[i];
                if (instance._asset.IsDisposed)
                {
                    s_byAsset.Remove(instance._asset);
                    s_live.RemoveAt(i);
                    continue;
                }
                instance.Tick();
            }
        }
    }

    // ----------------------------------------------------------------------------------------
    // Lookup
    // ----------------------------------------------------------------------------------------

    /// <summary>Resolves a group by persistent id first, then by name. -1 when unknown.</summary>
    public int GetIndex(string idOrName)
    {
        if (string.IsNullOrEmpty(idOrName))
            return IndexOfName(AudioMixer.MasterGroupName);
        if (_byId.TryGetValue(idOrName, out int byId))
            return byId;
        if (_byName.TryGetValue(idOrName, out int byName))
            return byName;
        return -1;
    }

    private int IndexOfName(string name) => _byName.TryGetValue(name, out int idx) ? idx : -1;

    public string GetId(int index) => _ids[index];
    public string GetName(int index) => _names[index];
    public int GetParentIndex(int index) => _parentIndex[index];

    /// <summary>Linear gain of the named group's full chain. Unknown names fall back to Master
    /// (with a suppressed diagnostic), matching the legacy asset API.</summary>
    public float GetChainGain(string idOrName)
    {
        int idx = GetIndex(idOrName);
        if (idx < 0)
        {
            if (!string.IsNullOrEmpty(idOrName))
                Debug.LogWarningOnce("mixer-unknown-group:" + _asset.Name + ":" + idOrName,
                    $"[AudioMixerInstance] group '{idOrName}' not found in mixer '{_asset.Name}', routing to Master.");
            idx = IndexOfName(AudioMixer.MasterGroupName);
            if (idx < 0) return 1f;
        }
        return _chainGain[idx];
    }

    /// <summary>Linear gain of the group's full chain by index.</summary>
    public float GetChainGain(int index) => IsValid(index) ? _chainGain[index] : 1f;

    /// <summary>Final effective volume (dB) of a group: base (default/override) shifted by the
    /// transition tween while one runs, minus the current duck reduction (N3 layering).</summary>
    public float GetEffectiveVolumeDB(int index)
    {
        if (!IsValid(index)) return AudioMixerMath.MinVolumeDB;
        float db = _transitionLayerActive ? _transitionDB[index] : _effectiveDB[index];
        return db - _automation.GetDuckInfo(index).ReductionDB;
    }

    public bool GetEffectiveMute(int index) => _effectiveMute[index];

    // ----------------------------------------------------------------------------------------
    // Mutations (instance state only)
    // ----------------------------------------------------------------------------------------

    /// <summary>Sets a group's effective volume in dB (writes through to the asset default —
    /// authoring edit), recomputes chains and notifies listeners.</summary>
    public bool SetGroupVolumeDB(int index, float db)
    {
        if (!IsValid(index)) return false;
        var group = _asset.Groups[index];
        float clamped = AudioMixerMath.ClampDB(db);
        if (group.VolumeDB == clamped) return false;
        group.VolumeDB = clamped;
        RefreshEffectiveFromAsset(index);
        RecomputeChains();
        Notify();
        return true;
    }

    public bool SetMute(int index, bool mute)
    {
        if (!IsValid(index)) return false;
        var group = _asset.Groups[index];
        if (group.Mute == mute) return false;
        group.Mute = mute;
        RefreshEffectiveFromAsset(index);
        RecomputeChains();
        Notify();
        return true;
    }

    public bool SetSolo(int index, bool solo)
    {
        if (!IsValid(index)) return false;
        var group = _asset.Groups[index];
        if (group.Solo == solo) return false;
        group.Solo = solo;
        RefreshEffectiveAllFromAsset();
        RecomputeChains();
        Notify();
        return true;
    }

    /// <summary>Activates a snapshot as instance overrides: groups listed in the snapshot get an
    /// effective dB override; unlisted groups keep their current effective value. The asset's
    /// stored defaults are NOT modified (runtime state stays out of the asset).</summary>
    public bool ApplySnapshot(string snapshotName)
    {
        foreach (var snapshot in _asset.Snapshots)
        {
            if (snapshot.Name != snapshotName) continue;

            _snapshotOverrides.Clear();
            foreach (var volume in snapshot.Volumes)
            {
                int idx = GetIndex(volume.Group);
                if (idx >= 0)
                    _snapshotOverrides[idx] = AudioMixerMath.ClampDB(volume.VolumeDB);
            }
            _activeSnapshot = snapshotName;
            // A new base layer invalidates any frozen transition tween (N3 layering).
            _transitionLayerActive = false;
            RefreshEffectiveAllFromAsset();
            RecomputeChains();
            Notify();
            return true;
        }
        return false;
    }

    /// <summary>Clears snapshot overrides, returning every group to its asset default.</summary>
    public void ClearSnapshot()
    {
        if (_snapshotOverrides.Count == 0 && _activeSnapshot.Length == 0) return;
        _snapshotOverrides.Clear();
        _activeSnapshot = "";
        _transitionLayerActive = false;
        RefreshEffectiveAllFromAsset();
        RecomputeChains();
        Notify();
    }

    /// <summary>Forces a full recompile and notification (editor tooling after bulk edits).</summary>
    public void ForceRecompile()
    {
        _structuralDirty = true;
        Recompile();
        Notify();
    }

    private void Notify()
    {
        Version++;
        Changed?.Invoke();
    }

    // ----------------------------------------------------------------------------------------
    // Compilation & validation
    // ----------------------------------------------------------------------------------------

    private void Recompile()
    {
        var groups = _asset.Groups;
        int count = groups.Count;

        EnsureCapacity(ref _parentIndex, count);
        EnsureCapacity(ref _ids, count);
        EnsureCapacity(ref _names, count);
        EnsureCapacity(ref _effectiveDB, count);
        EnsureCapacity(ref _effectiveMute, count);
        EnsureCapacity(ref _chainGain, count);
        EnsureCapacity(ref _valueFingerprints, count);
        EnsureCapacity(ref _structFingerprints, count);
        EnsureCapacity(ref _transitionDB, count);
        EnsureCapacity(ref _walkStack, count + 1);

        _byId = new Dictionary<string, int>(count);
        _byName = new Dictionary<string, int>(count);

        for (int i = 0; i < count; i++)
        {
            var g = groups[i];
            _ids[i] = g.Id;
            _names[i] = g.Name;

            // Duplicate ids/names: first group wins for lookups; later duplicates are only
            // reachable by index. A diagnostic keeps this visible without throwing.
            if (!string.IsNullOrEmpty(g.Id) && !_byId.ContainsKey(g.Id))
                _byId[g.Id] = i;
            if (!string.IsNullOrEmpty(g.Name) && !_byName.ContainsKey(g.Name))
                _byName[g.Name] = i;
            else if (!string.IsNullOrEmpty(g.Name))
                Debug.LogWarningOnce("mixer-duplicate-group:" + _asset.Name + ":" + g.Name,
                    $"[AudioMixerInstance] mixer '{_asset.Name}' has duplicate group name '{g.Name}'; the first one wins.");
        }

        // Parent links: empty or unknown parents hang under Master. Cycles are cut by walking
        // with a depth bound — validation must never hang the mixer.
        int master = IndexOfName(AudioMixer.MasterGroupName);
        for (int i = 0; i < count; i++)
        {
            var g = groups[i];
            int parent;
            if (string.IsNullOrEmpty(g.Parent))
            {
                parent = i == master ? -1 : master;
                if (i != master && parent < 0)
                    Debug.LogWarningOnce("mixer-no-master:" + _asset.Name,
                        $"[AudioMixerInstance] mixer '{_asset.Name}' has no Master group; '{g.Name}' stays at the output.");
            }
            else
            {
                parent = IndexOfName(g.Parent);
                if (parent < 0)
                {
                    Debug.LogWarningOnce("mixer-unknown-parent:" + _asset.Name + ":" + g.Parent,
                        $"[AudioMixerInstance] group '{g.Name}' references unknown parent '{g.Parent}' in mixer '{_asset.Name}'; falling back to Master.");
                    parent = master;
                }
            }

            if (parent == i)
                parent = -1; // self-parent is a cycle; treat as root

            _parentIndex[i] = parent;
        }

        // Cut longer cycles: any group whose parent chain exceeds the group count is re-rooted.
        for (int i = 0; i < count; i++)
        {
            int depth = 0;
            int current = i;
            while (current >= 0 && depth <= count)
            {
                current = _parentIndex[current];
                depth++;
            }
            if (depth > count)
            {
                Debug.LogWarningOnce("mixer-parent-cycle:" + _asset.Name + ":" + _names[i],
                    $"[AudioMixerInstance] parent cycle detected at group '{_names[i]}' in mixer '{_asset.Name}'; re-rooting to Master.");
                _parentIndex[i] = i == master ? -1 : master;
            }
        }

        RefreshEffectiveAllFromAsset();
        RecomputeChains();
        CaptureFingerprints(count);
        _assetGroupCount = count;
        _structuralDirty = false;

        // Structural edits invalidate the native topology: rebuild it so group nodes match the
        // compiled hierarchy. Sources re-attach on their next recompute (the Notify below).
        if (_busGraph.IsBuilt)
        {
            _busGraph.Teardown();
            _busGraph.EnsureBuilt(count);
        }
    }

    private void RefreshEffectiveAllFromAsset()
    {
        var groups = _asset.Groups;
        for (int i = 0; i < groups.Count && i < _effectiveDB.Length; i++)
            RefreshEffectiveFromAsset(i);
        _anySolo = false;
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Solo) { _anySolo = true; break; }
        }
    }

    private void RefreshEffectiveFromAsset(int index)
    {
        var g = _asset.Groups[index];
        _effectiveDB[index] = _snapshotOverrides.TryGetValue(index, out float db)
            ? db
            : AudioMixerMath.ClampDB(g.VolumeDB);
        _effectiveMute[index] = g.Mute;
    }

    /// <summary>Recomputes every chain gain in place: product of effective linear volumes along the
    /// validated parent chain, honouring mute and solo silencing. With a native bus graph the same
    /// gains are pushed to the group nodes — the single place audio touches them.</summary>
    private void RecomputeChains()
    {
        int count = _names.Length;
        for (int i = 0; i < count; i++)
            _chainGain[i] = ComputeChainGain(i);

        if (_busGraph.IsBuilt)
            for (int i = 0; i < count; i++)
                _busGraph.ApplyGroupGain(i);
    }

    private float ComputeChainGain(int start)
    {
        // Walk to the root collecting indices, then multiply top-down.
        int depth = 0;
        int current = start;
        while (current >= 0 && depth < _walkStack.Length)
        {
            _walkStack[depth++] = current;
            current = _parentIndex[current];
        }

        float gain = 1f;
        for (int d = depth - 1; d >= 0; d--)
        {
            int idx = _walkStack[d];
            if (_effectiveMute[idx])
                return 0f;
            if (_anySolo && !IsInSoloedSubtree(idx))
                return 0f;
            // Final dB = base or transition tween, minus the current duck reduction (N3).
            float db = _transitionLayerActive ? _transitionDB[idx] : _effectiveDB[idx];
            db -= _automation.GetDuckInfo(idx).ReductionDB;
            gain *= AudioMixerMath.DecibelsToLinear(db);
        }
        return gain;
    }

    /// <summary>True when the group itself is soloed or has a soloed descendant (solo keeps the
    /// output ancestors of soloed groups audible).</summary>
    private bool IsInSoloedSubtree(int index)
    {
        if (_effectiveSoloState(index)) return true;
        int count = _names.Length;
        for (int i = 0; i < count; i++)
        {
            if (!_effectiveSoloState(i)) continue;
            // Walk i's ancestors; if we hit `index`, index is an ancestor of a soloed group.
            int current = _parentIndex[i];
            int guard = 0;
            while (current >= 0 && guard++ <= count)
            {
                if (current == index) return true;
                current = _parentIndex[current];
            }
        }
        return false;
    }

    private bool _effectiveSoloState(int index) => index < _asset.Groups.Count && _asset.Groups[index].Solo;

    // ----------------------------------------------------------------------------------------
    // Legacy change detection (zero-alloc fingerprint pass)
    // ----------------------------------------------------------------------------------------

    /// <summary>Runs one legacy change-detection pass over the asset. <see cref="AudioContext.Update"/>
    /// calls this per frame; hosts outside the engine loop (edit-mode tooling, tests) may drive it directly.</summary>
    public void Tick() => Tick(AudioContext.DeltaTime);

    /// <summary>Tick with an explicit delta for the transition/duck envelopes (tests, tooling).</summary>
    public void Tick(float automationDeltaSeconds)
    {
        var groups = _asset.Groups;
        int count = groups.Count;

        // Build the native bus graph as soon as a native backend exists (no-op otherwise).
        if (!_busGraph.IsBuilt && AudioContext.IsInitialized)
            EnsureNativeGraph();

        if (_structuralDirty || count != _assetGroupCount)
        {
            Recompile();
            Notify();
            return;
        }

        // Fingerprint pass: value edits refresh in place (no allocation); structural edits
        // (id/name/parent) need the lookup dictionaries rebuilt.
        bool valueChanged = false;
        bool structChanged = false;
        for (int i = 0; i < count; i++)
        {
            long valueFp = ValueFingerprint(groups[i]);
            long structFp = StructFingerprint(groups[i]);
            if (_valueFingerprints[i] != valueFp)
            {
                _valueFingerprints[i] = valueFp;
                valueChanged = true;
            }
            if (_structFingerprints[i] != structFp)
            {
                _structFingerprints[i] = structFp;
                structChanged = true;
            }
        }

        if (valueChanged)
        {
            RefreshEffectiveAllFromAsset();
            RecomputeChains();
            Notify();
        }

        if (structChanged)
        {
            Recompile();
            Notify();
        }

        // N3: advance snapshot-transition tween and duck envelopes (no-op when idle).
        _automation.Tick(automationDeltaSeconds);
    }

    private void CaptureFingerprints(int count)
    {
        var groups = _asset.Groups;
        for (int i = 0; i < count && i < groups.Count; i++)
        {
            _valueFingerprints[i] = ValueFingerprint(groups[i]);
            _structFingerprints[i] = StructFingerprint(groups[i]);
        }
    }

    private static long ValueFingerprint(AudioMixerGroupData g)
    {
        long hash = 1469598103934665603L;
        hash = (hash ^ BitConverter.DoubleToInt64Bits(g.VolumeDB)) * 1099511628211L;
        hash = (hash ^ (g.Mute ? 1 : 0)) * 1099511628211L;
        hash = (hash ^ (g.Solo ? 1 : 0)) * 1099511628211L;
        return hash;
    }

    private static long StructFingerprint(AudioMixerGroupData g)
    {
        long hash = 1469598103934665603L;
        hash = (hash ^ (long)(g.Parent?.GetHashCode() ?? 0)) * 1099511628211L;
        hash = (hash ^ (long)(g.Name?.GetHashCode() ?? 0)) * 1099511628211L;
        hash = (hash ^ (long)(g.Id?.GetHashCode() ?? 0)) * 1099511628211L;
        return hash;
    }

    // ----------------------------------------------------------------------------------------

    private static void EnsureCapacity<T>(ref T[] array, int count)
    {
        if (array.Length < count)
            array = new T[count];
    }

    private bool IsValid(int index) => index >= 0 && index < _names.Length;
}
