// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.PaperUI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime;

/// <summary>
/// Per-scene registry of the components that actually implement a per-frame callback
/// (Start/Update/LateUpdate/FixedUpdate, plus OnRenderCollect/DrawGizmos/OnGui). Components register
/// when they become enabled in an active scene and unregister when disabled/removed, so each loop
/// iterates a short, pre-filtered, execution-ordered list instead of walking every GameObject and
/// component. This is why <see cref="GameObject"/> stores its components in insertion order - ordering
/// for callbacks is owned here, decoupled from component storage.
/// </summary>
internal sealed class SceneComponentRegistry
{
    [Flags]
    private enum Ticks
    {
        None = 0,
        Start = 1, Update = 2, LateUpdate = 4, FixedUpdate = 8,
        RenderCollect = 16, DrawGizmos = 32, OnGui = 64,

        // Physics callbacks are dispatched per collision pair rather than swept per frame, so they get
        // no Channel. They live here to reuse the per-type override cache: colliders consult it to
        // decide whether their shapes need overlap tracking at all.
        TriggerEnter = 128, TriggerStay = 256, TriggerExit = 512,
        CollisionEnter = 1024, CollisionStay = 2048, CollisionExit = 4096,

        AnyTrigger = TriggerEnter | TriggerStay | TriggerExit,
        AnyCollision = CollisionEnter | CollisionStay | CollisionExit,
    }

    // Which callbacks a concrete component type overrides. Computed once per type.
    private static readonly Dictionary<Type, Ticks> s_typeTicks = new();

    /// <summary>
    /// Clears the static type-tick cache. Called during hot-reload so user-script MonoBehaviour
    /// Types (registered when preview scenes instantiate model/prefab hierarchies) don't pin the
    /// collectible script AssemblyLoadContext.
    /// </summary>
    [OnAssemblyUnload]
    internal static void ClearTypeTickCache() => s_typeTicks.Clear();

    private static Ticks GetTicks(Type type)
    {
        if (s_typeTicks.TryGetValue(type, out Ticks cached))
            return cached;

        Ticks ticks = Ticks.None;
        if (Overrides(type, nameof(MonoBehaviour.Start))) ticks |= Ticks.Start;
        if (Overrides(type, nameof(MonoBehaviour.Update))) ticks |= Ticks.Update;
        if (Overrides(type, nameof(MonoBehaviour.LateUpdate))) ticks |= Ticks.LateUpdate;
        if (Overrides(type, nameof(MonoBehaviour.FixedUpdate))) ticks |= Ticks.FixedUpdate;
        if (Overrides(type, nameof(MonoBehaviour.OnRenderCollect))) ticks |= Ticks.RenderCollect;
        if (Overrides(type, nameof(MonoBehaviour.DrawGizmos))) ticks |= Ticks.DrawGizmos;
        if (Overrides(type, nameof(MonoBehaviour.OnGui))) ticks |= Ticks.OnGui;
        if (Overrides(type, nameof(MonoBehaviour.OnTriggerEnter))) ticks |= Ticks.TriggerEnter;
        if (Overrides(type, nameof(MonoBehaviour.OnTriggerStay))) ticks |= Ticks.TriggerStay;
        if (Overrides(type, nameof(MonoBehaviour.OnTriggerExit))) ticks |= Ticks.TriggerExit;
        if (Overrides(type, nameof(MonoBehaviour.OnCollisionEnter))) ticks |= Ticks.CollisionEnter;
        if (Overrides(type, nameof(MonoBehaviour.OnCollisionStay))) ticks |= Ticks.CollisionStay;
        if (Overrides(type, nameof(MonoBehaviour.OnCollisionExit))) ticks |= Ticks.CollisionExit;

        s_typeTicks[type] = ticks;
        return ticks;
    }

    /// <summary>
    /// Which physics callbacks a component implements. Cached per type, so this costs a dictionary
    /// lookup rather than reflection after the first call.
    /// </summary>
    internal static PhysicsCallbacks GetPhysicsCallbacks(MonoBehaviour component)
    {
        Ticks ticks = GetTicks(component.GetType());
        var callbacks = PhysicsCallbacks.None;
        if ((ticks & Ticks.TriggerEnter) != 0) callbacks |= PhysicsCallbacks.TriggerEnter;
        if ((ticks & Ticks.TriggerStay) != 0) callbacks |= PhysicsCallbacks.TriggerStay;
        if ((ticks & Ticks.TriggerExit) != 0) callbacks |= PhysicsCallbacks.TriggerExit;
        if ((ticks & Ticks.CollisionEnter) != 0) callbacks |= PhysicsCallbacks.CollisionEnter;
        if ((ticks & Ticks.CollisionStay) != 0) callbacks |= PhysicsCallbacks.CollisionStay;
        if ((ticks & Ticks.CollisionExit) != 0) callbacks |= PhysicsCallbacks.CollisionExit;
        return callbacks;
    }

    private static bool Overrides(Type type, string method)
    {
        // All the callbacks are public virtual on MonoBehaviour; an override's DeclaringType differs.
        // No LINQ (PR0001): manual scan over GetMethods.
        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo m = methods[i];
            if (m.Name == method && m.GetBaseDefinition().DeclaringType == typeof(MonoBehaviour))
                return m.DeclaringType != typeof(MonoBehaviour);
        }
        return false;
    }

    /// <summary>One callback's membership: an insertion-ordered list plus an execution-order-sorted
    /// snapshot rebuilt only when membership changes. Iterating the snapshot also keeps a loop safe
    /// against components enabling/disabling others mid-callback.</summary>
    private sealed class Channel
    {
        private readonly List<MonoBehaviour> _list = new();
        private MonoBehaviour[] _sorted = Array.Empty<MonoBehaviour>();
        private bool _dirty;

        public void Add(MonoBehaviour c) { _list.Add(c); _dirty = true; }

        public void Remove(MonoBehaviour c)
        {
            if (!_list.Remove(c)) return;
            _dirty = true;
            // The sorted snapshot is rebuilt lazily on the next Snapshot() call; a channel that is
            // never iterated again (an unloaded scene still referenced by an editor panel or a Paper
            // closure) would otherwise keep every removed component - and, for user scripts, the
            // collectible script AssemblyLoadContext - alive through the stale array.
            if (_list.Count == 0) _sorted = Array.Empty<MonoBehaviour>();
        }

        public void Clear()
        {
            _list.Clear();
            _sorted = Array.Empty<MonoBehaviour>();
            _dirty = false;
        }

        public MonoBehaviour[] Snapshot()
        {
            if (_dirty)
            {
                // No LINQ (PR0001): stable sort by execution order, then copy to array.
                // Array.Sort is not stable, so build (order, index, component) pairs and sort by
                // (order, index) to preserve registration order for equal execution orders.
                int n = _list.Count;
                var keys = new int[n];
                var indices = new int[n];
                for (int i = 0; i < n; i++)
                {
                    keys[i] = RuntimeUtils.GetExecutionOrder(_list[i]) ?? 0;
                    indices[i] = i;
                }
                Array.Sort(keys, indices);
                var sorted = new MonoBehaviour[n];
                for (int i = 0; i < n; i++)
                    sorted[i] = _list[indices[i]];
                _sorted = sorted;
                _dirty = false;
            }
            return _sorted;
        }
    }

    private readonly Channel _start = new();
    private readonly Channel _update = new();
    private readonly Channel _lateUpdate = new();
    private readonly Channel _fixedUpdate = new();
    private readonly Channel _renderCollect = new();
    private readonly Channel _drawGizmos = new();
    private readonly Channel _onGui = new();

    // Enabled cameras in registration order. Editor viewports asked for "the cameras in this scene"
    // every frame by walking every GameObject with GetComponent<Camera>(); at 100k objects that is
    // ~4 ms per caller per frame, so they read this list instead.
    private readonly List<Camera> _cameras = new();

    public void Register(MonoBehaviour c)
    {
        if (c._updateRegistered) return;
        c._updateRegistered = true;
        if (c is Camera camera && !_cameras.Contains(camera)) _cameras.Add(camera);

        Ticks ticks = GetTicks(c.GetType());
        if ((ticks & Ticks.Start) != 0 && !c.HasStarted) _start.Add(c);
        if ((ticks & Ticks.Update) != 0) _update.Add(c);
        if ((ticks & Ticks.LateUpdate) != 0) _lateUpdate.Add(c);
        if ((ticks & Ticks.FixedUpdate) != 0) _fixedUpdate.Add(c);
        if ((ticks & Ticks.RenderCollect) != 0) _renderCollect.Add(c);
        if ((ticks & Ticks.DrawGizmos) != 0) _drawGizmos.Add(c);
        if ((ticks & Ticks.OnGui) != 0) _onGui.Add(c);
    }

    public void Unregister(MonoBehaviour c)
    {
        if (!c._updateRegistered) return;
        c._updateRegistered = false;
        if (c is Camera camera) _cameras.Remove(camera);

        _start.Remove(c);
        _update.Remove(c);
        _lateUpdate.Remove(c);
        _fixedUpdate.Remove(c);
        _renderCollect.Remove(c);
        _drawGizmos.Remove(c);
        _onGui.Remove(c);
    }

    /// <summary>Drops every membership and snapshot. Called when the owning scene is disposed so a
    /// scene object that outlives its unload (panels, closures) holds no component references.</summary>
    public void Clear()
    {
        _cameras.Clear();
        _start.Clear();
        _update.Clear();
        _lateUpdate.Clear();
        _fixedUpdate.Clear();
        _renderCollect.Clear();
        _drawGizmos.Clear();
        _onGui.Clear();
    }

    /// <summary>Appends the cameras currently enabled in this scene (component and object enabled,
    /// not disposed) to <paramref name="output"/>, in registration order. Allocation-free; O(cameras)
    /// rather than O(objects).</summary>
    public void CollectEnabledCameras(List<Camera> output)
    {
        for (int i = 0; i < _cameras.Count; i++)
        {
            Camera camera = _cameras[i];
            if (!camera.IsDisposed && camera.EnabledInHierarchy) output.Add(camera);
        }
    }

    // ---- Gameplay callbacks (gated per-component by ShouldExecuteGameplay inside Internal*) ----

    [HotPath]
    public void RunStart()
    {
        foreach (MonoBehaviour c in _start.Snapshot())
        {
            if (c.IsDisposed || c.HasStarted || !c.EnabledInHierarchy) continue;
            c.InternalStart();
            if (c.HasStarted) _start.Remove(c);
        }
    }

    [HotPath]
    public void RunUpdate()
    {
        foreach (MonoBehaviour c in _update.Snapshot())
            if (!c.IsDisposed && c.EnabledInHierarchy) c.InternalUpdate();
    }

    [HotPath]
    public void RunLateUpdate()
    {
        foreach (MonoBehaviour c in _lateUpdate.Snapshot())
            if (!c.IsDisposed && c.EnabledInHierarchy) c.InternalLateUpdate();
    }

    [HotPath]
    public void RunFixedUpdate()
    {
        foreach (MonoBehaviour c in _fixedUpdate.Snapshot())
            if (!c.IsDisposed && c.EnabledInHierarchy) c.InternalFixedUpdate();
    }

    // ---- Rendering / gizmo / GUI callbacks (always run, even in edit mode; not gameplay-gated) ----

    public void RunRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        var filter = Scene.RenderCollectFilter;
        foreach (MonoBehaviour c in _renderCollect.Snapshot())
        {
            if (c.IsDisposed || !c.EnabledInHierarchy) continue;
            if (filter != null)
            {
                GameObject go = c.GameObject;
                if (go != null && !filter(go, camera)) continue;
            }
            try { c.OnRenderCollect(camera, renderables, lights); }
            catch (Exception ex) { Debug.LogError($"[{c.GameObject?.Name}/{c.GetType().Name}] OnRenderCollect threw: {ex.Message}\n{ex.StackTrace}"); }
        }
    }

    public void RunDrawGizmos()
    {
        foreach (MonoBehaviour c in _drawGizmos.Snapshot())
        {
            if (c.IsDisposed || !c.EnabledInHierarchy || c.HideFlags.HasFlag(HideFlags.NoGizmos)) continue;
            if (Debug.GizmoFilter is { } filter && !filter(c)) continue;
            // Scoped so the Scene view's Gizmo Scale and Fade Gizmos apply per component: the scale
            // is taken about this component's own position and the fade only dims what it drew.
            Debug.BeginGizmoObject(c);
            try { c.DrawGizmos(); }
            catch (Exception ex) { Debug.LogError($"[{c.GameObject?.Name}/{c.GetType().Name}] DrawGizmos threw: {ex.Message}\n{ex.StackTrace}"); }
            finally { Debug.EndGizmoObject(); }
        }
    }

    public void RunOnGui(Paper paper)
    {
        foreach (MonoBehaviour c in _onGui.Snapshot())
        {
            if (c.IsDisposed || !c.EnabledInHierarchy) continue;
            try { c.OnGui(paper); }
            catch (Exception ex) { Debug.LogError($"[{c.GameObject?.Name}/{c.GetType().Name}] OnGui threw: {ex.Message}\n{ex.StackTrace}"); }
        }
    }
}
