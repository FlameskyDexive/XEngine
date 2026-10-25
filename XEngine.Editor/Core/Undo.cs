using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using XEngine.Data.Provenance;
using XEngine.Echo;
using XEngine.Editor.GUI.SceneView;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>
/// Central undo/redo system for the editor.
/// Supports property changes (via Echo serialization), structural changes (create/delete GO),
/// and generic action-based undo via lambdas.
/// </summary>
public static partial class Undo
{
    // ================================================================
    //  Data Structures
    // ================================================================

    private abstract class UndoRecord
    {
        public abstract void PerformUndo();
        public abstract void PerformRedo();
    }

    /// <summary>
    /// Records a property change on a serializable object (typically MonoBehaviour).
    /// Stores before/after EchoObject snapshots. Restores by copying fields onto the live object.
    /// Tracks targets by Identifier (Guid) so records survive destroy/recreate cycles.
    /// </summary>
    private class PropertyRecord : UndoRecord
    {
        public Type TargetType;
        public EchoObject BeforeState;
        public EchoObject AfterState;

        // Identifier-based tracking for MonoBehaviour targets
        public Guid ComponentIdentifier;
        // Fallback for non-MonoBehaviour targets (plain objects)
        public WeakReference<object>? FallbackRef;

        public PropertyRecord(object target, EchoObject before, EchoObject after)
        {
            TargetType = target.GetType();
            BeforeState = before;
            AfterState = after;

            if (target is MonoBehaviour mb)
                ComponentIdentifier = mb.Identifier;
            else
            {
                ComponentIdentifier = Guid.Empty;
                FallbackRef = new WeakReference<object>(target);
            }
        }

        public override void PerformUndo() => RestoreState(BeforeState);
        public override void PerformRedo() => RestoreState(AfterState);

        public object? ResolveTarget()
        {
            if (ComponentIdentifier != Guid.Empty)
                return FindComponentByIdentifier(ComponentIdentifier);
            if (FallbackRef != null && FallbackRef.TryGetTarget(out var fallback))
            {
                if (fallback is EngineObject eo && eo.IsDisposed) return null;
                return fallback;
            }
            return null;
        }

        private void RestoreState(EchoObject state)
        {
            var target = ResolveTarget();
            if (target == null) return;

            CopyFieldsFromEcho(target, TargetType, state);
        }
    }

    /// <summary>
    /// Records an arbitrary undoable action with explicit undo/redo lambdas.
    /// Used for GO header fields, transform changes, structural operations, etc.
    /// </summary>
    private class ActionRecord : UndoRecord
    {
        public Action UndoAction;
        public Action RedoAction;

        public ActionRecord(Action undo, Action redo)
        {
            UndoAction = undo;
            RedoAction = redo;
        }

        public override void PerformUndo() => UndoAction();
        public override void PerformRedo() => RedoAction();
    }

    /// <summary>
    /// One user-visible undo step. May contain multiple records
    /// (e.g., editing multiple selected objects in one frame).
    /// </summary>
    private class UndoStep
    {
        public string Description;
        public List<UndoRecord> Records;
        /// <summary>True if this step contains ONLY PropertyRecords and can be coalesced with the next.</summary>
        public bool IsCoalescable;
        /// <summary>When this step was created/last coalesced (milliseconds).</summary>
        public long Timestamp;

        public UndoStep(string description, List<UndoRecord> records, bool isCoalescable = false)
        {
            Description = description;
            Records = records;
            IsCoalescable = isCoalescable;
            Timestamp = Environment.TickCount64;
        }
    }

    // ================================================================
    //  State
    // ================================================================

    private static readonly List<UndoStep> _undoStack = new();
    private static readonly List<UndoStep> _redoStack = new();

    // Per-frame pending snapshots: target -> beforeState (captured at start of draw, before any mutations)
    private static readonly Dictionary<object, EchoObject> _pendingSnapshots = new();

    // Immediate action records accumulated this frame (RegisterAction calls)
    private static readonly List<(string description, UndoRecord record)> _pendingActions = new();

    // Pre-grouped action steps accumulated this frame (RegisterActionGroup calls). Each is ONE undo step
    // holding many records, so editing N selected objects collapses into a single user-visible step.
    private static readonly List<(UndoStep step, bool coalesce)> _pendingActionGroups = new();

    // Deferred created/destroy objects serialized at FlushFrame so components added after registration are captured
    private static readonly List<(GameObject go, string description, bool isCreate)> _pendingStructural = new();

    // Deferred created-object batches flushed as ONE undo step each (compound creations like UI templates)
    private static readonly List<(List<GameObject> gos, string description)> _pendingStructuralGroups = new();

    // Continuous operation state (gizmo drag) tracks by Identifier, not reference
    private static bool _isContinuous;
    private static string _continuousDescription = "";
    private static List<(Guid goId, Float3 pos, Quaternion rot, Float3 scale)>? _continuousStartState;

    // ================================================================
    //  Configuration
    // ================================================================

    public static int MaxSteps { get; set; } = 100;

    // ================================================================
    //  Events
    // ================================================================

    /// <summary>Fires after an undo or redo operation completes.</summary>
    public static event Action? OnUndoRedo;

    [OnAssemblyUnload]
    internal static void ClearUndoEventsForReload()
    {
        OnUndoRedo = null;
        _onPropertyChanged = null;
        _onPropertyChangedHasSubscribers = false;
    }

    /// <summary>
    /// ★ Layer 0 (A0-cont / A4) — Fires whenever FlushPendingRecords detects a real property change,
    /// carrying the provenance-bearing delta. Subscribers: the operation journal (A2 persistence),
    /// the in-process MCP server's "scene changed" tool (B3), and any change-bus consumer. Only raised
    /// for PropertyRecord-path changes (the Equals short-circuit already filtered no-op frames), so the
    /// cost is paid only on frames that actually mutated something.
    /// </summary>
    public static event Action<PropertyChangedEventArgs> OnPropertyChanged
    {
        add
        {
            _onPropertyChanged += value;
            _onPropertyChangedHasSubscribers = true;
        }
        remove
        {
            _onPropertyChanged -= value;
            _onPropertyChangedHasSubscribers = _onPropertyChanged is not null;
        }
    }
    private static event Action<PropertyChangedEventArgs>? _onPropertyChanged;

    // Backing flag kept in sync by the event accessors so FlushPendingRecords can skip the
    // delta/allocation path entirely when no consumer is listening (zero cost when unwired).
    private static bool _onPropertyChangedHasSubscribers;

    /// <summary>
    /// ★ Layer 0 — The intent/author/source attached to the next batch of property changes captured by
    /// FlushPendingRecords. The editor gizmo path sets this to a default editor provenance; AI write
    /// tools (B2) push a tool-named provenance before mutating the scene, then pop it. When null, the
    /// default <c>ChangeProvenance.ForEditor()</c> is used.
    /// </summary>
    private static readonly Stack<ChangeProvenance> _provenanceStack = new();

    /// <summary>Push a provenance context for subsequent property changes (use with a using/finally pop).</summary>
    public static void PushProvenance(ChangeProvenance provenance) => _provenanceStack.Push(provenance);

    /// <summary>Pop the most recently pushed provenance context.</summary>
    public static void PopProvenance()
    {
        if (_provenanceStack.Count > 0)
        {
            _provenanceStack.Pop();
        }
    }

    private static ChangeProvenance CurrentProvenance
        => _provenanceStack.Count > 0 ? _provenanceStack.Peek() : ChangeProvenance.ForEditor();

    /// <summary>Kind of change reported by <see cref="OnPropertyChanged"/>. A4 lets a ChangeBus
    /// subscriber (e.g. the in-process MCP "scene changed" tool) distinguish property edits from
    /// structural changes. Property is the common case today; structural kinds are reserved for when
    /// the create/delete paths emit deltas (currently they use non-serializable lambda actions).</summary>
    public enum ChangeKind
    {
        Property,
        ObjectCreated,
        ObjectDeleted,
    }

    /// <summary>Argument for <see cref="OnPropertyChanged"/>: the structured delta + why/who/when.</summary>
    public sealed class PropertyChangedEventArgs : EventArgs
    {
        /// <summary>The target object that changed (already serialized into the delta).</summary>
        public required object Target { get; init; }
        /// <summary>Stable identifier of the target (component Guid) when available, else Guid.Empty.</summary>
        public required Guid TargetIdentifier { get; init; }
        /// <summary>The provenance-bearing delta (replayable via EchoObject.ApplyDelta).</summary>
        public required EchoObject Delta { get; init; }
        /// <summary>Human-readable undo description ("Modify Properties", caller-supplied).</summary>
        public required string Description { get; init; }
        /// <summary>What kind of change this is. Defaults to Property (the FlushPendingRecords path).</summary>
        public ChangeKind Kind { get; init; } = ChangeKind.Property;
    }

    // ================================================================
    //  State Queries
    // ================================================================

    public static bool CanUndo => _undoStack.Count > 0;
    public static bool CanRedo => _redoStack.Count > 0;
    public static bool IsContinuous => _isContinuous;

    public static string UndoDescription => _undoStack.Count > 0
        ? $"Undo {_undoStack[^1].Description}"
        : "Undo";

    public static string RedoDescription => _redoStack.Count > 0
        ? $"Redo {_redoStack[^1].Description}"
        : "Redo";

    // ================================================================
    //  Property Recording (for MonoBehaviour / plain objects via PropertyGrid)
    // ================================================================

    /// <summary>
    /// Snapshot an object's current state for undo. Call at the TOP of PropertyGrid.Draw()
    /// or custom editor OnGUI(), BEFORE any widgets draw this captures the state before
    /// any in-place mutations (nested objects, collections, curves, etc.).
    /// No-op if already snapshotted this frame for the same target.
    /// </summary>
    public static void Snapshot(object target)
    {
        if (Application.IsPlaying) return;
        if (target == null) return;
        if (_isContinuous) return;
        if (s_numericMutation?.Owns(target) == true) return;

        if (!_pendingSnapshots.ContainsKey(target))
        {
            var before = Serializer.Serialize(target.GetType(), target);
            _pendingSnapshots[target] = before;
        }
    }

    /// <summary>
    /// True when the user is mid-edit this frame (mouse held/clicked or typed).
    /// Inspector draw paths must not Echo-serialize every visible component otherwise.
    /// </summary>
    public static bool IsInteractiveEdit
        => Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2)
            || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)
            || Input.InputString.Length > 0
            || Input.GetKey(KeyCode.Backspace) || Input.GetKey(KeyCode.Delete)
            || Input.GetKeyDown(KeyCode.Enter) || Input.GetKeyDown(KeyCode.Space)
            || (Input.IsCtrlPressed && (Input.GetKeyDown(KeyCode.V) || Input.GetKeyDown(KeyCode.X)))
            || (Input.IsShiftPressed && Input.GetKeyDown(KeyCode.Insert));

    private static long s_ignoreInteractiveSnapshotAtVersion = long.MinValue;
    private static int s_selectionMouseButtons;

    /// <summary>
    /// Hierarchy / Scene clicks arrive with the mouse down. Skip the inspector Echo
    /// snapshot during that mouse gesture so select-object does not serialize every component.
    /// A selection version must never suppress subsequent Inspector edits indefinitely.
    /// </summary>
    public static void IgnoreInteractiveSnapshotForCurrentSelection()
    {
        s_selectionMouseButtons = GetSnapshotMouseButtons();
        s_ignoreInteractiveSnapshotAtVersion = s_selectionMouseButtons != 0
            ? Selection.ChangeVersion : long.MinValue;
    }

    private static int GetSnapshotMouseButtons()
    {
        int buttons = 0;
        for (int i = 0; i < 3; i++)
            if (Input.GetMouseButton(i) || Input.GetMouseButtonDown(i)) buttons |= 1 << i;
        return buttons;
    }

    private static void UpdateSelectionSnapshotSuppression()
    {
        if (s_selectionMouseButtons == 0) return;
        // Remove released buttons permanently; another button must not extend the selection gesture.
        s_selectionMouseButtons &= GetSnapshotMouseButtons();
        if (s_selectionMouseButtons == 0 || s_ignoreInteractiveSnapshotAtVersion != Selection.ChangeVersion)
        {
            s_selectionMouseButtons = 0;
            s_ignoreInteractiveSnapshotAtVersion = long.MinValue;
        }
    }

    /// <summary>
    /// Same as <see cref="Snapshot"/>, but only when <see cref="IsInteractiveEdit"/> is true.
    /// Use from per-frame inspector / PropertyGrid draw, not from explicit button callbacks.
    /// </summary>
    public static void SnapshotIfInteracting(object target)
        => SnapshotIfInteracting(target, null);

    /// <summary>
    /// GUI editors must use the input buffered by their own Paper surface. Window's Update
    /// clears runtime typed characters before Render builds the Inspector; Paper retains them
    /// until its controls dispatch. This also keeps detached surfaces independent.
    /// </summary>
    public static void SnapshotIfInteracting(object target, Paper? paper)
    {
        UpdateSelectionSnapshotSuppression();
        if (!(paper == null ? IsInteractiveEdit : HasInspectorInput(paper))) return;
        if (s_ignoreInteractiveSnapshotAtVersion == Selection.ChangeVersion) return;
        Snapshot(target);
    }

    private static bool HasInspectorInput(Paper paper)
    {
        if (paper.IsPointerDown(PaperMouseBtn.Left) || paper.IsPointerDown(PaperMouseBtn.Right)
            || paper.IsPointerDown(PaperMouseBtn.Middle) || paper.HasPendingButtonEdge) return true;
        if (paper.InputString.Count > 0
            || paper.IsKeyPressedOrRepeating(PaperKey.Backspace) || paper.IsKeyPressedOrRepeating(PaperKey.Delete)
            || paper.IsKeyPressed(PaperKey.Enter) || paper.IsKeyPressed(PaperKey.Space)) return true;
        bool control = paper.IsKeyDown(PaperKey.LeftControl) || paper.IsKeyDown(PaperKey.RightControl);
        return control && (paper.IsKeyPressedOrRepeating(PaperKey.V) || paper.IsKeyPressedOrRepeating(PaperKey.X));
    }

    // ================================================================
    //  Generic Action Recording
    // ================================================================

    /// <summary>
    /// Register an undoable action with explicit undo/redo lambdas.
    /// </summary>
    public static void RegisterAction(string description, Action undo, Action redo)
    {
        if (Application.IsPlaying) return;

        _pendingActions.Add((description, new ActionRecord(undo, redo)));
    }

    /// <summary>
    /// Records a property change on a GameObject, looking the GO back up by Identifier on undo/redo
    /// so the record survives destroy/recreate cycles. Apply receives the live GO and the value to assign.
    /// Caller is still responsible for the immediate write - this only registers the undo entry.
    /// </summary>
    public static void RecordGameObjectChange<T>(GameObject go, string description, T oldValue, T newValue, Action<GameObject, T> apply, bool coalesce = false, Func<GameObject, T>? get = null)
    {
        if (s_numericMutation?.Owns(go) == true)
        {
            s_numericMutation.Record(go, description, oldValue, newValue, apply, get);
            return;
        }
        Guid id = go.Identifier;
        Action undo = () => { var g = FindGO(id); if (g != null) apply(g, oldValue); };
        Action redo = () => { var g = FindGO(id); if (g != null) apply(g, newValue); };
        if (coalesce) RegisterCoalescableAction(description, undo, redo);
        else RegisterAction(description, undo, redo);
    }

    /// <summary>
    /// Register an undoable action that coalesces with the previous action if it has the
    /// same description and is within the time window. Use for continuous text input (Name fields, etc.).
    /// The undo lambda is kept from the FIRST action; the redo lambda is updated to the LATEST.
    /// </summary>
    public static void RegisterCoalescableAction(string description, Action undo, Action redo)
    {
        if (Application.IsPlaying) return;

        // Check if we can coalesce with the top of the undo stack
        if (_pendingActions.Count == 0 && _undoStack.Count > 0)
        {
            var prev = _undoStack[^1];
            if (prev.Description == description
                && prev.Records.Count == 1
                && prev.Records[0] is ActionRecord
                && Environment.TickCount64 - prev.Timestamp <= CoalesceWindowMs)
            {
                // Update redo to latest value, keep original undo
                prev.Records[0] = new ActionRecord(((ActionRecord)prev.Records[0]).UndoAction, redo);
                prev.Timestamp = Environment.TickCount64;
                _redoStack.Clear();
                return;
            }
        }

        // Can't coalesce push as normal action but mark as coalescable
        _pendingActions.Add((description, new ActionRecord(undo, redo)));
    }

    /// <summary>
    /// Register several undo/redo actions as ONE undo step (so editing many selected objects in a single
    /// interaction collapses to a single undo). When <paramref name="coalesce"/> is true the step merges
    /// with the previous step if it has the same description and record count within the coalesce window
    /// (for continuous edits like dragging a multi-object transform field).
    /// </summary>
    public static void RegisterActionGroup(string description, IReadOnlyList<(Action undo, Action redo)> actions, bool coalesce = false)
    {
        if (Application.IsPlaying) return;
        if (actions == null || actions.Count == 0) return;

        var records = new List<UndoRecord>(actions.Count);
        foreach (var (u, r) in actions) records.Add(new ActionRecord(u, r));
        _pendingActionGroups.Add((new UndoStep(description, records, isCoalescable: false), coalesce));
    }

    /// <summary>
    /// Apply a value to every given GameObject and record the change as a single grouped undo step.
    /// Captures each object's current value as the undo state, then writes the new value. Mirrors
    /// <see cref="RecordGameObjectChange"/> but batched across a multi-object selection.
    /// </summary>
    public static void ApplyGameObjectChanges<T>(IReadOnlyList<GameObject> gos, string description,
        Func<GameObject, T> get, Action<GameObject, T> set, T newValue, bool coalesce = false)
    {
        if (Application.IsPlaying)
        {
            foreach (var go in gos) set(go, newValue);
            return;
        }

        var actions = new List<(Action undo, Action redo)>(gos.Count);
        foreach (var go in gos)
        {
            Guid id = go.Identifier;
            T oldV = get(go);
            T captured = newValue;
            actions.Add((() => { var g = FindGO(id); if (g != null) set(g, oldV); },
                         () => { var g = FindGO(id); if (g != null) set(g, captured); }));
            set(go, newValue);
        }
        EditorSceneManager.MarkDirty();
        RegisterActionGroup(description, actions, coalesce);
    }

    // ================================================================
    //  Structural Operations
    // ================================================================

    /// <summary>
    /// Register that a GameObject was just created. Undo will destroy it, redo will recreate it.
    /// Serialization is deferred to FlushFrame so components added after this call are captured.
    /// Call AFTER the GO has been added to the scene and parented.
    /// </summary>
    public static void RegisterCreatedObject(GameObject go, string description)
    {
        if (Application.IsPlaying) return;
        if (go == null) return;

        _pendingStructural.Add((go, description, isCreate: true));
    }

    /// <summary>
    /// Register a batch of just-created GameObjects as ONE undo step: a single undo destroys all of
    /// them, a single redo recreates all of them. Serialization is deferred to FlushFrame like
    /// <see cref="RegisterCreatedObject"/>. Pass only top-level roots — each record already covers
    /// the object's whole subtree. Use for compound creations (e.g. control templates spanning a
    /// root plus canvas-level popup nodes).
    /// </summary>
    public static void RegisterCreatedObjectGroup(IReadOnlyList<GameObject> gos, string description)
    {
        if (Application.IsPlaying) return;
        if (gos == null || gos.Count == 0) return;

        var batch = new List<GameObject>(gos.Count);
        foreach (var go in gos)
            if (go != null) batch.Add(go);
        if (batch.Count == 0) return;

        _pendingStructuralGroups.Add((batch, description));
    }

    private static void FlushCreatedObject(GameObject go, string description)
    {
        _pendingActions.Add((description, CreateCreatedObjectRecord(go)));
    }

    private static UndoRecord CreateCreatedObjectRecord(GameObject go)
    {
        // Now serialize all components have been added by this point
        var serialized = Serializer.Serialize(typeof(object), go);
        var goId = go.Identifier;
        var parentId = go.Parent?.Identifier ?? Guid.Empty;
        var siblingIndex = go.Parent != null ? go.Parent.Children.IndexOf(go) : -1;

        return new ActionRecord(
            undo: () =>
            {
                var scene = Scene.Current;
                if (scene == null) return;
                var target = FindGameObjectByIdentifier(scene, goId);
                if (target == null) return;

                if (Selection.IsSelected(target))
                    Selection.Clear();

                foreach (var child in target.GetChildrenDeep().ToList())
                    scene.Remove(child);
                scene.Remove(target);
                target.Dispose();
            },
            redo: () =>
            {
                var scene = Scene.Current;
                if (scene == null) return;

                var restored = Serializer.Deserialize<GameObject>(serialized);
                if (restored == null) return;
                RestoreIdentifiers(restored, serialized);

                scene.Add(restored);
                if (parentId != Guid.Empty)
                {
                    var parent = FindGameObjectByIdentifier(scene, parentId);
                    if (parent != null)
                    {
                        restored.SetParent(parent);
                        if (siblingIndex >= 0 && siblingIndex < parent.Children.Count)
                            restored.SetSiblingIndex(siblingIndex);
                    }
                }

                Selection.Select(restored);
                EditorSceneManager.MarkDirty();
            });
    }

    /// <summary>
    /// Register that a GameObject is about to be destroyed. Undo will recreate it, redo will destroy it.
    /// Call BEFORE the GO is removed from the scene.
    /// </summary>
    public static void RegisterDestroyObject(GameObject go, string description)
    {
        if (Application.IsPlaying) return;
        if (go == null) return;

        // Serialize the entire GO tree before destruction
        var serialized = Serializer.Serialize(typeof(object), go);
        var parentId = go.Parent?.Identifier ?? Guid.Empty;
        var siblingIndex = go.Parent != null ? go.Parent.Children.IndexOf(go) : -1;
        var goId = go.Identifier;

        RegisterAction(description,
            undo: () =>
            {
                var scene = Scene.Current;
                if (scene == null) return;

                var restored = Serializer.Deserialize<GameObject>(serialized);
                if (restored == null) return;
                RestoreIdentifiers(restored, serialized);

                scene.Add(restored);
                if (parentId != Guid.Empty)
                {
                    var parent = FindGameObjectByIdentifier(scene, parentId);
                    if (parent != null)
                    {
                        restored.SetParent(parent);
                        if (siblingIndex >= 0 && siblingIndex < parent.Children.Count)
                            restored.SetSiblingIndex(siblingIndex);
                    }
                }

                Selection.Select(restored);
                EditorSceneManager.MarkDirty();
            },
            redo: () =>
            {
                var scene = Scene.Current;
                if (scene == null) return;
                var target = FindGameObjectByIdentifier(scene, goId);
                if (target == null) return;

                if (Selection.IsSelected(target))
                    Selection.Clear();

                foreach (var child in target.GetChildrenDeep().ToList())
                    scene.Remove(child);
                scene.Remove(target);
                target.Dispose();
                EditorSceneManager.MarkDirty();
            });
    }

    // ================================================================
    //  Continuous Operations (Gizmo Drag)
    // ================================================================

    /// <summary>
    /// Begin a continuous undo operation (e.g., gizmo drag).
    /// Snapshots all targets' transforms at the start. Call EndContinuous when done.
    /// </summary>
    public static void BeginContinuous(GameObject[] targets, string description)
    {
        if (Application.IsPlaying) return;
        if (_isContinuous) return;

        _isContinuous = true;
        _continuousDescription = description;
        _continuousStartState = new List<(Guid, Float3, Quaternion, Float3)>();

        foreach (var go in targets)
        {
            if (go == null) continue;
            _continuousStartState.Add((go.Identifier, go.Transform.LocalPosition, go.Transform.LocalRotation, go.Transform.LocalScale));
        }
    }

    /// <summary>
    /// End a continuous operation and push one undo step if anything changed.
    /// </summary>
    public static void EndContinuous()
    {
        if (!_isContinuous || _continuousStartState == null) return;

        var records = new List<UndoRecord>();

        foreach (var (goId, startPos, startRot, startScale) in _continuousStartState)
        {
            var go = FindGO(goId);
            if (go == null) continue;

            var endPos = go.Transform.LocalPosition;
            var endRot = go.Transform.LocalRotation;
            var endScale = go.Transform.LocalScale;

            if (startPos.Equals(endPos) && startRot == endRot && startScale.Equals(endScale))
                continue;

            var capturedId = goId;
            var sPos = startPos; var sRot = startRot; var sScale = startScale;
            var ePos = endPos; var eRot = endRot; var eScale = endScale;

            records.Add(new ActionRecord(
                undo: () =>
                {
                    var g = FindGO(capturedId);
                    if (g == null) return;
                    g.Transform.LocalPosition = sPos;
                    g.Transform.LocalRotation = sRot;
                    g.Transform.LocalScale = sScale;
                },
                redo: () =>
                {
                    var g = FindGO(capturedId);
                    if (g == null) return;
                    g.Transform.LocalPosition = ePos;
                    g.Transform.LocalRotation = eRot;
                    g.Transform.LocalScale = eScale;
                }));
        }

        if (records.Count > 0)
        {
            PushStep(new UndoStep(_continuousDescription, records));
        }

        _isContinuous = false;
        _continuousStartState = null;
    }

    /// <summary>
    /// Cancel a continuous operation without pushing an undo step.
    /// Restores all targets to their start state.
    /// </summary>
    public static void CancelContinuous()
    {
        if (!_isContinuous || _continuousStartState == null) return;

        foreach (var (goId, startPos, startRot, startScale) in _continuousStartState)
        {
            var go = FindGO(goId);
            if (go == null) continue;
            go.Transform.LocalPosition = startPos;
            go.Transform.LocalRotation = startRot;
            go.Transform.LocalScale = startScale;
        }

        _isContinuous = false;
        _continuousStartState = null;
    }

    // ================================================================
    //  Grouping
    // ================================================================

    /// <summary>
    /// Force a new group boundary. Flushes any pending records into a step,
    /// so the next RecordObject starts a fresh step.
    /// </summary>
    public static void IncrementGroup()
    {
        FlushPendingRecords();
    }

    // ================================================================
    //  Execute Undo/Redo
    // ================================================================

    public static void PerformUndo()
    {
        if (Application.IsPlaying) return;
        CommitNumericEdits();

        // If continuous mode is active, cancel it instead of undoing
        if (_isContinuous)
        {
            CancelContinuous();
            return;
        }

        // Flush any pending records first
        FlushPendingRecords();

        if (_undoStack.Count == 0) return;

        var step = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        // Undo in reverse order
        for (int i = step.Records.Count - 1; i >= 0; i--)
        {
            try
            {
                step.Records[i].PerformUndo();
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"Undo failed for record {i}: {ex.Message}");
            }
        }

        _redoStack.Add(step);
        TrimStack(_redoStack);

        EditorSceneManager.MarkDirty();
        OnUndoRedo?.Invoke();
    }

    public static void PerformRedo()
    {
        if (Application.IsPlaying) return;
        CommitNumericEdits();
        if (_isContinuous) return;

        // Flush any pending records first
        FlushPendingRecords();

        if (_redoStack.Count == 0) return;

        var step = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        // Redo in forward order
        for (int i = 0; i < step.Records.Count; i++)
        {
            try
            {
                step.Records[i].PerformRedo();
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"Redo failed for record {i}: {ex.Message}");
            }
        }

        _undoStack.Add(step);
        TrimStack(_undoStack);

        EditorSceneManager.MarkDirty();
        OnUndoRedo?.Invoke();
    }

    // ================================================================
    //  Lifecycle
    // ================================================================

    /// <summary>
    /// Called once per frame by EditorApplication after all UI has been drawn.
    /// Flushes pending property snapshots into undo steps.
    /// </summary>
    internal static void FlushFrame()
    {
        FlushNumericLifetimes();
        // Observe release even when no Inspector / PropertyGrid was drawn this frame.
        UpdateSelectionSnapshotSuppression();
        FlushPendingRecords();
    }

    /// <summary>
    /// Clear all undo/redo history. Called on scene load.
    /// </summary>
    public static void Clear()
    {
        AbandonNumericEdits();
        _undoStack.Clear();
        _redoStack.Clear();
        _pendingSnapshots.Clear();
        _pendingActions.Clear();
        _pendingActionGroups.Clear();
        _pendingStructural.Clear();
        _pendingStructuralGroups.Clear();
        _isContinuous = false;
        _continuousStartState = null;
        s_ignoreInteractiveSnapshotAtVersion = long.MinValue;
        s_selectionMouseButtons = 0;
    }

    // ================================================================
    //  Internal Helpers
    // ================================================================

    private static void FlushPendingRecords()
    {
        // Flush deferred structural operations first (serializes created GOs now that components are
        // added). Each record is guarded and the pending lists are always cleared: this runs at the
        // top of every BeginGui, so a component whose serialization throws must cost one log line,
        // not a permanently re-thrown exception that blanks the whole editor GUI.
        try
        {
            foreach (var (go, desc, isCreate) in _pendingStructural)
            {
                if (go == null || go.IsDisposed) continue;
                try
                {
                    if (isCreate)
                        FlushCreatedObject(go, desc);
                }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"Undo: failed to record created object '{go.Name}' for '{desc}' — it will not be undoable: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
        finally
        {
            _pendingStructural.Clear();
        }

        try
        {
            foreach (var (gos, desc) in _pendingStructuralGroups)
            {
                var records = new List<UndoRecord>(gos.Count);
                foreach (var go in gos)
                {
                    if (go == null || go.IsDisposed) continue;
                    try
                    {
                        records.Add(CreateCreatedObjectRecord(go));
                    }
                    catch (Exception ex)
                    {
                        Runtime.Debug.LogError($"Undo: failed to record created object '{go.Name}' for '{desc}' — it will not be undoable: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                if (records.Count > 0)
                    _pendingActionGroups.Add((new UndoStep(desc, records, isCoalescable: false), coalesce: false));
            }
        }
        finally
        {
            _pendingStructuralGroups.Clear();
        }

        // Flush action records FIRST as separate steps (never merge with property changes)
        // Each action is its own undo step (Add Component, Toggle Enabled, Reparent, etc.)
        bool hasActions = _pendingActions.Count > 0 || _pendingActionGroups.Count > 0;

        // Grouped actions push as a single multi-record step (coalescing continuous edits).
        foreach (var (step, coalesce) in _pendingActionGroups)
        {
            if (coalesce && TryCoalesceActionGroup(step)) { _redoStack.Clear(); continue; }
            PushStep(step);
        }
        _pendingActionGroups.Clear();

        foreach (var (desc, record) in _pendingActions)
            PushStep(new UndoStep(desc, [record], isCoalescable: false));
        _pendingActions.Clear();

        // If explicit actions were registered this frame, discard property snapshots.
        // The actions already handle their changes the Snapshot diff would create duplicates
        // (e.g., Component Enabled toggle: RegisterAction changes _enabled, Snapshot also detects it).
        if (hasActions)
        {
            _pendingSnapshots.Clear();
            return;
        }

        // Build property records from snapshots.
        // ★ Layer 0 (A0-cont): the Equals short-circuit stays — it costs an O(n) tree compare that
        // returns false on no-change frames, so we only pay the CreateDelta cost (also O(n), plus
        // operation-list allocation) on frames that actually mutated a target. The resulting
        // provenance-bearing delta is raised via OnPropertyChanged for the journal (A2) / MCP (B3).
        var propertyRecords = new List<PropertyRecord>();
        var changeEvents = _onPropertyChangedHasSubscribers ? new List<PropertyChangedEventArgs>() : null;
        bool sceneChanged = false;

        try
        {
            foreach (var (target, before) in _pendingSnapshots)
            {
                if (target is EngineObject eo && eo.IsDisposed) continue;

                try
                {
                    var after = Serializer.Serialize(target.GetType(), target);

                    if (!before.Equals(after))
                    {
                        propertyRecords.Add(new PropertyRecord(target, before, after));
                        var targetScene = target is MonoBehaviour component ? component.Scene
                            : target is GameObject gameObject ? gameObject.Scene : null;
                        if (targetScene != null && ReferenceEquals(targetScene, Scene.Current)) sceneChanged = true;

                        // Only build the structured delta + provenance when someone is listening, so the
                        // editor pays zero cost when the journal / MCP server aren't attached.
                        if (changeEvents is not null)
                        {
                            Guid targetId = target is MonoBehaviour mb ? mb.Identifier : Guid.Empty;
                            EchoObject delta = ProvenancedDelta.Create(before, after, CurrentProvenance);
                            changeEvents.Add(new PropertyChangedEventArgs
                            {
                                Target = target,
                                TargetIdentifier = targetId,
                                Delta = delta,
                                Description = "Modify Properties",
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"Undo: failed to snapshot '{target.GetType().Name}' — this change will not be undoable: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
        finally
        {
            _pendingSnapshots.Clear();
        }

        // PropertyGrid setters mutate components directly. Creating an Undo step alone does not
        // dirty the scene; also notify on coalesced changes while it is already dirty.
        if (sceneChanged) EditorSceneManager.MarkDirty();

        // Raise change events (outside the snapshot loop so handlers can't observe mid-iteration state).
        if (changeEvents is not null)
        {
            foreach (var evt in changeEvents)
            {
                try
                {
                    _onPropertyChanged?.Invoke(evt);
                }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"Undo: OnPropertyChanged subscriber threw: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        // Push property records as a coalescable step (separate from actions)
        if (propertyRecords.Count > 0 && TryCoalesce(propertyRecords))
        {
            _redoStack.Clear();
        }
        else if (propertyRecords.Count > 0)
        {
            PushStep(new UndoStep("Modify Properties", propertyRecords.Cast<UndoRecord>().ToList(), isCoalescable: true));
        }
    }

    /// <summary>
    /// Try to merge new property records into the previous undo step.
    /// Uses time-based coalescing: merges if same targets, continuous edit chain,
    /// and the previous step was created/updated within 300ms.
    /// </summary>
    private const long CoalesceWindowMs = 300;

    private static bool TryCoalesce(List<PropertyRecord> newRecords)
    {
        if (_undoStack.Count == 0) return false;

        var prev = _undoStack[^1];
        if (!prev.IsCoalescable) return false;
        if (prev.Records.Count != newRecords.Count) return false;

        // Time-based: only coalesce within the time window
        long now = Environment.TickCount64;
        if (now - prev.Timestamp > CoalesceWindowMs) return false;

        for (int i = 0; i < newRecords.Count; i++)
        {
            if (prev.Records[i] is not PropertyRecord prevPR) return false;
            var newPR = newRecords[i];

            // Same target? Compare by identifier for MonoBehaviour, by fallback ref for others
            if (prevPR.ComponentIdentifier != Guid.Empty || newPR.ComponentIdentifier != Guid.Empty)
            {
                if (prevPR.ComponentIdentifier != newPR.ComponentIdentifier) return false;
            }
            else
            {
                var prevTarget = prevPR.ResolveTarget();
                var newTarget = newPR.ResolveTarget();
                if (prevTarget == null || newTarget == null || !ReferenceEquals(prevTarget, newTarget)) return false;
            }

            // Continuous edit chain: previous "after" must match new "before"
            if (!prevPR.AfterState.Equals(newPR.BeforeState)) return false;
        }

        // Coalesce: update AfterState and refresh timestamp
        for (int i = 0; i < newRecords.Count; i++)
            ((PropertyRecord)prev.Records[i]).AfterState = newRecords[i].AfterState;
        prev.Timestamp = now;
        return true;
    }

    /// <summary>Merge a grouped action step into the previous step when it matches (continuous edits).</summary>
    private static bool TryCoalesceActionGroup(UndoStep step)
    {
        if (_undoStack.Count == 0) return false;

        var prev = _undoStack[^1];
        if (prev.Description != step.Description) return false;
        if (prev.Records.Count != step.Records.Count) return false;
        if (Environment.TickCount64 - prev.Timestamp > CoalesceWindowMs) return false;

        for (int i = 0; i < prev.Records.Count; i++)
            if (prev.Records[i] is not ActionRecord || step.Records[i] is not ActionRecord) return false;

        // Keep each original undo, adopt the latest redo.
        for (int i = 0; i < prev.Records.Count; i++)
            prev.Records[i] = new ActionRecord(((ActionRecord)prev.Records[i]).UndoAction, ((ActionRecord)step.Records[i]).RedoAction);
        prev.Timestamp = Environment.TickCount64;
        return true;
    }

    private static void PushStep(UndoStep step)
    {
        _undoStack.Add(step);
        _redoStack.Clear(); // New action invalidates redo stack
        TrimStack(_undoStack);
    }

    private static void TrimStack(List<UndoStep> stack)
    {
        while (stack.Count > MaxSteps)
            stack.RemoveAt(0);
    }

    // Fields that must never be overwritten by undo they are identity/internal state
    private static readonly HashSet<string> _undoSkipFields = new()
    {
        "_identifier",        // MonoBehaviour identity must be preserved
        "_instanceID",        // EngineObject instance ID
        "_enabledInHierarchy",// Derived state, not user-settable
        "_go",                // GameObject back-reference (not serialized, but just in case)
        "_hasStarted",        // Lifecycle flags
        "_hasBeenEnabled",
        "_executeAlwaysCached",
        "AssetID",            // Asset identity
        "AssetPath",          // Asset path
        "<IsDisposed>k__BackingField", // Disposed state
    };

    /// <summary>
    /// Copy serializable fields from an EchoObject onto a live object.
    /// Deserializes into a temp then copies fields, preserving the live reference.
    /// Skips identity and internal state fields.
    /// </summary>
    private static void CopyFieldsFromEcho(object target, Type type, EchoObject echo, IReadOnlySet<string>? fields = null)
    {
        try
        {
            var temp = Serializer.Deserialize(echo, type);
            if (temp == null) return;

            // Walk up the hierarchy to get all serializable fields (matching Echo's behavior)
            var currentType = type;
            while (currentType != null && currentType != typeof(object))
            {
                foreach (var field in currentType.GetFields(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly))
                {
                    // Skip identity/internal fields
                    if (_undoSkipFields.Contains(field.Name)) continue;
                    if (fields != null && !fields.Contains(field.Name)) continue;

                    bool shouldSerialize = field.IsPublic || field.GetCustomAttribute<Echo.SerializeFieldAttribute>() != null;
                    bool shouldIgnore = field.GetCustomAttribute<Echo.SerializeIgnoreAttribute>() != null
                                     || field.GetCustomAttribute<NonSerializedAttribute>() != null;
                    if (!shouldSerialize || shouldIgnore) continue;

                    try
                    {
                        var value = field.GetValue(temp);
                        field.SetValue(target, value);
                    }
                    catch
                    {
                        // Skip fields that fail
                    }
                }
                currentType = currentType.BaseType;
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Failed to restore object state: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively restore identifiers on a deserialized GO tree from the stored EchoObject.
    /// </summary>
    internal static void RestoreIdentifiers(GameObject go, EchoObject serializedGO)
    {
        // Restore GO identifier
        var idStr = serializedGO.Get("Identifier")?.StringValue;
        if (Guid.TryParse(idStr, out var goId))
            go.SetIdentifier(goId);

        // Restore the prefab-source pairing so a restored instance still pairs against its asset
        if (Guid.TryParse(serializedGO.Get("SourceIdentifier")?.StringValue, out var srcId))
            go.SetSourceIdentifier(srcId);

        // Restore component identifiers
        var comps = serializedGO.Get("Components")?.List;
        if (comps != null)
        {
            // Component source pairings travel as a list parallel to Components (written only
            // when at least one pairing exists).
            var compSrcTags = serializedGO.Get("CompSourceIdentifiers")?.List;

            var liveComps = go.GetComponents().ToArray();
            for (int i = 0; i < Math.Min(comps.Count, liveComps.Length); i++)
            {
                var compEcho = comps[i];
                // MonoBehaviour._identifier has [SerializeField] so it's in the EchoObject
                var compIdField = compEcho.Get("_identifier");
                if (compIdField != null)
                {
                    // _identifier is a Guid serialized by Echo may be stored as string or via type wrapper
                    if (Guid.TryParse(compIdField.StringValue, out var compId))
                        liveComps[i].Identifier = compId;
                    else if (compIdField.TryGet("$value", out var innerVal) && Guid.TryParse(innerVal.StringValue, out var compId2))
                        liveComps[i].Identifier = compId2;
                }

                if (compSrcTags != null && i < compSrcTags.Count && Guid.TryParse(compSrcTags[i].StringValue, out var compSrc))
                    liveComps[i].SetSourceIdentifier(compSrc);
            }
        }

        // Recurse children
        var children = serializedGO.Get("Children")?.List;
        if (children != null)
        {
            for (int i = 0; i < Math.Min(children.Count, go.Children.Count); i++)
                RestoreIdentifiers(go.Children[i], children[i]);
        }
    }

    // ================================================================
    //  Identifier-Based Resolution (for undo lambdas)
    // ================================================================

    /// <summary>
    /// Find a GameObject by identifier in the current scene. Returns null if not found.
    /// Use this in undo/redo lambdas instead of capturing GO references directly.
    /// </summary>
    public static GameObject? FindGO(Guid identifier)
    {
        var scene = Scene.Current;
        if (scene == null) return null;
        foreach (var root in scene.RootObjects)
        {
            var found = root.FindChildByIdentifier(identifier);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Find a MonoBehaviour component by identifier across all GOs in the current scene.
    /// </summary>
    public static MonoBehaviour? FindComponent(Guid identifier)
    {
        var scene = Scene.Current;
        if (scene == null) return null;
        foreach (var go in scene.AllObjects)
        {
            var comp = go.GetComponentByIdentifier(identifier);
            if (comp != null) return comp;
        }
        return null;
    }

    // Private alias used by PropertyRecord
    private static MonoBehaviour? FindComponentByIdentifier(Guid identifier) => FindComponent(identifier);

    private static GameObject? FindGameObjectByIdentifier(Scene scene, Guid identifier) => FindGO(identifier);
}
