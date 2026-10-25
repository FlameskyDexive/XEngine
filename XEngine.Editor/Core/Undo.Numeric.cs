using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XEngine.Data.Provenance;
using XEngine.Echo;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Editor.GUI.SceneView;

namespace XEngine.Editor.Core;

public static partial class Undo
{
    private static readonly ConditionalWeakTable<Paper, NumericSurface> s_numericSurfaces = new();
    private static readonly List<NumericSession> s_numericSessions = new();
    private static NumericSession? s_numericMutation;
    private static long s_multiNumericIdentity;

    private sealed class NumericSurface
    {
        internal readonly ConditionalWeakTable<object, NumericContext> Contexts = new();
        internal MultiNumericTargets? MultiTargets;
        internal NumericContext? MultiContext;
        internal bool MultiUsed;
        internal readonly List<MultiComponentTargets> Components = new();
        internal NumericSurface(Paper paper)
        {
            // BeginGui runs AFTER Paper.BeginFrame cleared its element tree. Disappearance
            // can only be tested once this surface has finished rebuilding its controls.
            paper.OnEndOfFramePostLayout += () =>
            {
                for (int i = s_numericSessions.Count - 1; i >= 0; i--)
                    s_numericSessions[i].CheckSurface(paper);
                // A hidden/cleared Inspector must release its last displayed object set.
                if (!MultiUsed) { MultiTargets = null; MultiContext = null; }
                MultiUsed = false;
                for (int i = Components.Count - 1; i >= 0; i--)
                    if (!Components[i].Used) Components.RemoveAt(i);
                    else Components[i].Used = false;
            };
        }
    }

    private sealed class NumericContext(object target, Paper paper)
    {
        internal readonly string Identity = target is EngineObject eo ? eo.InstanceID.ToString()
            : target is MultiNumericTargets multi ? multi.Identity
            : target is MultiComponentTargets components ? components.Identity : "numeric";
        internal readonly Func<ITextInputEditSession> Factory = () => new NumericSession(target, paper);
        internal readonly Func<IInputEditSession> DragFactory = () => new NumericSession(target, paper, pointer: true);
    }

    private static bool IsLiveComponent(MonoBehaviour component) => !component.IsDisposed
        && ReferenceEquals(component.Scene, Scene.Current)
        && ReferenceEquals(FindComponent(component.Identifier), component);

    private sealed class MultiComponentTargets
    {
        internal readonly string Key, Identity;
        internal readonly object[] Objects;
        internal readonly NumericContext Context;
        internal bool Used;
        internal MultiComponentTargets(string key, IReadOnlyList<object> objects, Paper paper)
        {
            Key = key;
            Identity = "components_" + System.Threading.Interlocked.Increment(ref s_multiNumericIdentity)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            Objects = new object[objects.Count];
            for (int i = 0; i < objects.Count; i++)
                Objects[i] = objects[i] is MonoBehaviour ? objects[i]
                    : throw new ArgumentException("Numeric component scopes require MonoBehaviour targets.", nameof(objects));
            Context = new NumericContext(this, paper);
        }
        internal bool Matches(IReadOnlyList<object> objects)
        {
            if (objects.Count != Objects.Length) return false;
            for (int i = 0; i < Objects.Length; i++)
                if (!ReferenceEquals(objects[i], Objects[i])) return false;
            return true;
        }
        internal bool Owns(object candidate)
        {
            for (int i = 0; i < Objects.Length; i++)
                if (ReferenceEquals(Objects[i], candidate)) return true;
            return false;
        }
        internal bool IsLive()
        {
            for (int i = 0; i < Objects.Length; i++)
                if (!IsLiveComponent((MonoBehaviour)Objects[i])) return false;
            return true;
        }
    }

    private sealed class MultiNumericTargets
    {
        internal readonly GameObject[] Objects;
        internal readonly string Identity;
        internal MultiNumericTargets(IReadOnlyList<GameObject> objects)
        {
            Objects = new GameObject[objects.Count];
            for (int i = 0; i < objects.Count; i++)
                Objects[i] = objects[i];
            // Exact instance comparison controls reuse; a unique compact context ID
            // avoids repeating an O(target-count) string in all nine field IDs each frame.
            Identity = "multi_" + System.Threading.Interlocked.Increment(ref s_multiNumericIdentity)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        internal bool Matches(IReadOnlyList<GameObject> objects)
        {
            if (objects.Count != Objects.Length) return false;
            for (int i = 0; i < Objects.Length; i++)
                if (!ReferenceEquals(objects[i], Objects[i])) return false;
            return true;
        }
        internal bool Owns(object target)
        {
            for (int i = 0; i < Objects.Length; i++)
                if (ReferenceEquals(Objects[i], target)) return true;
            return false;
        }
        internal bool IsLive()
        {
            for (int i = 0; i < Objects.Length; i++)
                if (Objects[i].IsDisposed || !ReferenceEquals(Objects[i].Scene, Scene.Current)
                    || !ReferenceEquals(FindGO(Objects[i].Identifier), Objects[i])) return false;
            return true;
        }
    }

    /// <summary>Factories capture the numeric target and owning Paper for later text/drag events.</summary>
    public static NumericFieldScope BeginNumericFields(object target, Paper paper)
    {
        var contexts = s_numericSurfaces.GetValue(paper, static p => new(p)).Contexts;
        // Avoid allocating a capturing factory on settled inspector frames.
        if (!contexts.TryGetValue(target, out var context))
        {
            context = new NumericContext(target, paper);
            contexts.Add(target, context);
        }
        return new NumericFieldScope(context.Factory, context.DragFactory, context.Identity);
    }

    /// <summary>Owns a snapshot of the displayed set; the Inspector may reuse its input list.</summary>
    public static NumericFieldScope BeginNumericFields(IReadOnlyList<GameObject> targets, Paper paper)
    {
        var surface = s_numericSurfaces.GetValue(paper, static p => new(p));
        surface.MultiUsed = true;
        if (surface.MultiTargets == null || !surface.MultiTargets.Matches(targets))
        {
            surface.MultiTargets = new MultiNumericTargets(targets);
            surface.MultiContext = new NumericContext(surface.MultiTargets, paper);
        }
        var context = surface.MultiContext!;
        return new NumericFieldScope(context.Factory, context.DragFactory, context.Identity, surface.MultiTargets.Objects);
    }

    public readonly struct NumericFieldScope : IDisposable
    {
        private readonly Func<ITextInputEditSession>? _previous;
        private readonly Func<IInputEditSession>? _previousDrag;
        public string Identity { get; }
        public IReadOnlyList<GameObject>? Targets { get; }
        public IReadOnlyList<object>? ComponentTargets { get; }
        internal NumericFieldScope(Func<ITextInputEditSession> factory, Func<IInputEditSession> dragFactory, string identity,
            IReadOnlyList<GameObject>? targets = null, IReadOnlyList<object>? components = null)
        {
            _previous = NumericSubmit.EditSessionFactory;
            _previousDrag = NumericSubmit.DragEditSessionFactory;
            Identity = identity;
            Targets = targets;
            ComponentTargets = components;
            if (!Application.IsPlaying)
            {
                NumericSubmit.EditSessionFactory = factory;
                NumericSubmit.DragEditSessionFactory = dragFactory;
            }
        }
        public void Dispose()
        {
            NumericSubmit.EditSessionFactory = _previous;
            NumericSubmit.DragEditSessionFactory = _previousDrag;
        }
    }

    /// <summary>One captured set per visible component section on this Paper.</summary>
    public static NumericFieldScope BeginComponentNumericFields(string key, IReadOnlyList<object> targets, Paper paper)
    {
        var surface = s_numericSurfaces.GetValue(paper, static p => new(p));
        MultiComponentTargets? group = null;
        for (int i = 0; i < surface.Components.Count; i++)
            if (surface.Components[i].Key == key)
            {
                group = surface.Components[i];
                if (!group.Matches(targets))
                    surface.Components[i] = group = new MultiComponentTargets(key, targets, paper);
                break;
            }
        if (group == null)
        {
            group = new MultiComponentTargets(key, targets, paper);
            surface.Components.Add(group);
        }
        group.Used = true;
        return new NumericFieldScope(group.Context.Factory, group.Context.DragFactory, group.Identity,
            components: group.Objects);
    }

    private sealed class ComponentNumericDelta(MonoBehaviour target)
    {
        internal EchoObject? Before, After;
        private EchoObject? _liveBefore;
        internal bool Changed => Before != null && After != null && !Before.Equals(After);
        internal void CaptureBefore() => _liveBefore = Serializer.Serialize(target.GetType(), target);
        internal void CaptureAfter()
        {
            var after = Serializer.Serialize(target.GetType(), target);
            if (!_liveBefore!.Equals(after))
            {
                Before ??= _liveBefore;
                After = After == null ? after : EchoObject.ApplyDelta(After, EchoObject.CreateDelta(_liveBefore, after));
            }
            _liveBefore = null;
        }
        internal UndoRecord Record() => new NumericDeltaRecord(target, Before!, After!);
        internal void Cancel() { if (Changed && IsLiveComponent(target)) Record().PerformUndo(); }
        internal void Notify(ChangeProvenance? provenance)
        {
            if (Changed && _onPropertyChangedHasSubscribers)
                try
                {
                    _onPropertyChanged?.Invoke(new PropertyChangedEventArgs
                    {
                        Target = target, TargetIdentifier = target.Identifier,
                        Delta = ProvenancedDelta.Create(Before!, After!, provenance ?? ChangeProvenance.ForEditor()),
                        Description = "Edit Numeric Value",
                    });
                }
                catch (Exception ex) { Runtime.Debug.LogError($"Undo: numeric change subscriber failed: {ex.Message}"); }
        }
    }

    private sealed class NumericDeltaRecord(object target, EchoObject before, EchoObject after) : UndoRecord
    {
        private readonly PropertyRecord _target = new(target, before, after);
        private readonly EchoObject _undo = EchoObject.CreateDelta(after, before);
        private readonly EchoObject _redo = EchoObject.CreateDelta(before, after);
        private readonly HashSet<string> _fields = ChangedRootFields(before, after);
        public override void PerformUndo() => Restore(_undo);
        public override void PerformRedo() => Restore(_redo);
        private void Restore(EchoObject delta)
        {
            var live = _target.ResolveTarget();
            if (live == null) return;
            var state = Serializer.Serialize(live.GetType(), live);
            CopyFieldsFromEcho(live, live.GetType(), EchoObject.ApplyDelta(state, delta), _fields);
            (live as EngineObject)?.OnValidate();
        }
        private static HashSet<string> ChangedRootFields(EchoObject from, EchoObject to)
        {
            var fields = new HashSet<string>();
            foreach (var entry in to.Tags)
                if (!from.TryGet(entry.Key, out var old) || !entry.Value.Equals(old)) fields.Add(entry.Key);
            return fields;
        }
    }

    private interface INumericAction
    {
        bool Changed { get; }
        UndoRecord ToRecord();
        void Cancel();
    }

    private sealed class NumericAction<T>(GameObject target, T before, T after, Action<GameObject, T> apply, Func<GameObject, T>? get) : INumericAction
    {
        private readonly Guid id = target.Identifier;
        private T _after = after;
        private readonly EchoObject? _beforeEcho = get == null ? null : Serializer.Serialize(typeof(T), before);
        private EchoObject? _afterEcho = get == null ? null : Serializer.Serialize(typeof(T), after);
        internal void Update(T oldValue, T newValue)
        {
            _after = newValue;
            if (_afterEcho != null)
                _afterEcho = EchoObject.ApplyDelta(_afterEcho, EchoObject.CreateDelta(
                    Serializer.Serialize(typeof(T), oldValue), Serializer.Serialize(typeof(T), newValue)));
        }
        public bool Changed => _beforeEcho != null ? !_beforeEcho.Equals(_afterEcho) : !EqualityComparer<T>.Default.Equals(before, _after);
        public void Cancel()
        {
            // Preview cancellation must never mutate a replacement instance with the same GUID.
            if (Changed && !target.IsDisposed && ReferenceEquals(FindGO(id), target)) ToRecord().PerformUndo();
        }
        public UndoRecord ToRecord()
        {
            T end = _after;
            if (get != null && _beforeEcho != null && _afterEcho != null)
            {
                var undo = EchoObject.CreateDelta(_afterEcho, _beforeEcho);
                var redo = EchoObject.CreateDelta(_beforeEcho, _afterEcho);
                void Restore(EchoObject delta)
                {
                    var go = FindGO(id);
                    if (go == null) return;
                    var current = Serializer.Serialize(typeof(T), get(go));
                    apply(go, (T)Serializer.Deserialize(EchoObject.ApplyDelta(current, delta), typeof(T))!);
                }
                return new ActionRecord(() => Restore(undo), () => Restore(redo));
            }
            return new ActionRecord(
                () => { var go = FindGO(id); if (go != null) apply(go, before); },
                () => { var go = FindGO(id); if (go != null) apply(go, end); });
        }
    }

    private sealed class NumericSession(object target, Paper paper, bool pointer = false) : ITextInputEditSession
    {
        internal bool Owns(object candidate) => ReferenceEquals(target, candidate)
            || target is MultiNumericTargets multi && multi.Owns(candidate)
            || target is MultiComponentTargets components && components.Owns(candidate);
        private readonly Scene? _scene = Scene.Current;
        private readonly long _selection = Selection.ChangeVersion;
        private readonly int _control = pointer ? paper.ActiveElementId : paper.FocusedElementId;
        private EchoObject? _before, _after;
        private ComponentNumericDelta[]? _components;
        private readonly Dictionary<(Guid id, string description), INumericAction> _actions = new();
        private bool _active, _abandoned;
        private ChangeProvenance? _provenance;

        public void Apply(Action mutation)
        {
            if (_abandoned || target is EngineObject { IsDisposed: true } || !ReferenceEquals(_scene, Scene.Current)
                || target is MultiNumericTargets multi && !multi.IsLive()
                || target is MultiComponentTargets components && !components.IsLive()) return;
            FlushPendingRecords();
            if (!_active)
            {
                _active = true;
                s_numericSessions.Add(this);
                _provenance = CurrentProvenance;
                if (target is MultiComponentTargets componentTargets)
                {
                    _components = new ComponentNumericDelta[componentTargets.Objects.Length];
                    for (int i = 0; i < _components.Length; i++)
                        _components[i] = new ComponentNumericDelta((MonoBehaviour)componentTargets.Objects[i]);
                }
            }
            EchoObject? liveBefore = target is GameObject or MultiNumericTargets or MultiComponentTargets
                ? null : Serializer.Serialize(target.GetType(), target);
            if (_components != null) for (int i = 0; i < _components.Length; i++) _components[i].CaptureBefore();
            var previous = s_numericMutation;
            s_numericMutation = this;
            try { mutation(); }
            finally { s_numericMutation = previous; }
            if (_components != null) for (int i = 0; i < _components.Length; i++) _components[i].CaptureAfter();
            if (liveBefore != null)
            {
                var liveAfter = Serializer.Serialize(target.GetType(), target);
                if (!liveBefore.Equals(liveAfter))
                {
                    _before ??= liveBefore;
                    _after = _after == null ? liveAfter : EchoObject.ApplyDelta(_after, EchoObject.CreateDelta(liveBefore, liveAfter));
                }
            }
            paper.RequestFollowUpFrame();
        }

        internal void Record<T>(GameObject go, string description, T before, T after, Action<GameObject, T> apply, Func<GameObject, T>? get)
        {
            var key = (go.Identifier, description);
            if (_actions.TryGetValue(key, out var existing) && existing is NumericAction<T> action) action.Update(before, after);
            else _actions.Add(key, new NumericAction<T>(go, before, after, apply, get));
        }

        public void Complete(bool cancel)
        {
            if (!_active) return;
            _active = false;
            s_numericSessions.Remove(this);
            if (_abandoned || target is EngineObject { IsDisposed: true } || !ReferenceEquals(_scene, Scene.Current)) return;
            // Finish any unrelated pre-draw snapshots BEFORE a cancel restores its values.
            FlushPendingRecords();
            cancel |= target is MultiNumericTargets multi && !multi.IsLive();
            cancel |= target is MultiComponentTargets components && !components.IsLive();
            var records = new List<UndoRecord>();
            if (_before != null && _after != null && !_before.Equals(_after)) records.Add(new NumericDeltaRecord(target, _before, _after));
            if (_components != null) for (int i = 0; i < _components.Length; i++)
                if (_components[i].Changed) records.Add(_components[i].Record());
            foreach (var action in _actions.Values) if (action.Changed) records.Add(action.ToRecord());
            if (cancel)
            {
                if (_before != null && _after != null && !_before.Equals(_after)) records[0].PerformUndo();
                foreach (var action in _actions.Values) action.Cancel();
                if (_components != null) for (int i = 0; i < _components.Length; i++) _components[i].Cancel();
            }
            else if (records.Count > 0)
            {
                PushStep(new UndoStep("Edit Numeric Value", records));
                var scene = target is MultiNumericTargets or MultiComponentTargets ? _scene
                    : target is MonoBehaviour mb ? mb.Scene : (target as GameObject)?.Scene;
                if (scene != null && ReferenceEquals(scene, Scene.Current)) EditorSceneManager.MarkDirty();
                if (_onPropertyChangedHasSubscribers && _before != null && _after != null && !_before.Equals(_after))
                {
                    try
                    {
                        _onPropertyChanged?.Invoke(new PropertyChangedEventArgs
                        {
                            Target = target,
                            TargetIdentifier = target is MonoBehaviour component ? component.Identifier : Guid.Empty,
                            Delta = ProvenancedDelta.Create(_before, _after, _provenance ?? ChangeProvenance.ForEditor()),
                            Description = "Edit Numeric Value",
                        });
                    }
                    catch (Exception ex) { Runtime.Debug.LogError($"Undo: numeric change subscriber failed: {ex.Message}"); }
                }
                if (_components != null) for (int i = 0; i < _components.Length; i++) _components[i].Notify(_provenance);
            }
            _before = _after = null;
            _components = null;
            _actions.Clear();
            paper.RequestFollowUpFrame();
        }

        internal void CheckLifetime()
        {
            if (_selection != Selection.ChangeVersion || _control != (pointer ? paper.ActiveElementId : paper.FocusedElementId)
                || target is EngineObject { IsDisposed: true }
                || target is MultiNumericTargets multi && !multi.IsLive()
                || target is MultiComponentTargets components && !components.IsLive()
                || !ReferenceEquals(_scene, Scene.Current)) Complete(cancel: false);
        }
        internal void CheckSurface(Paper surface)
        {
            if (ReferenceEquals(paper, surface) && !surface.FindElementByID(_control).IsValid) Complete(cancel: false);
        }
        internal void Abandon() { _abandoned = true; _active = false; _before = _after = null; _components = null; _actions.Clear(); }
    }

    /// <summary>Save/Undo/Redo boundaries commit live previews before operating on the scene.</summary>
    public static void CommitNumericEdits()
    {
        while (s_numericSessions.Count > 0) s_numericSessions[^1].Complete(cancel: false);
    }

    private static void FlushNumericLifetimes()
    {
        for (int i = s_numericSessions.Count - 1; i >= 0; i--) s_numericSessions[i].CheckLifetime();
    }
    private static void AbandonNumericEdits()
    {
        foreach (var session in s_numericSessions) session.Abandon();
        s_numericSessions.Clear();
        s_numericMutation = null;
    }
}
