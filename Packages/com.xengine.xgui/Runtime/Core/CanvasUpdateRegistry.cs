using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

using XEngine.Runtime;
using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>Central queues for package UI invalidation. Queues are de-duplicated by reference.</summary>
public static class CanvasUpdateRegistry
{
    private static readonly DirtyQueue s_layout = new();
    private static readonly DirtyQueue s_vertices = new();
    private static readonly DirtyQueue s_material = new();
    private static readonly DirtyQueue s_clip = new();
    private static readonly DirtyQueue s_hierarchy = new();
    private static readonly List<UIBehaviour> s_scratch = new(16);
    private static readonly List<UIBehaviour> s_layoutParticipants = new(8);
    private static readonly List<GameObject> s_layoutRoots = new(8);
    private static readonly StringBuilder s_cycleMessage = new(256);
    private static GameCanvas? s_layoutRootsCanvas;
    private static readonly Driver s_driver = new();
    private static IDisposable? s_registration;

    public static CanvasUpdatePhase LastPhase { get; private set; }
    public static int PhaseExecutionCount { get; private set; }

    public static int LayoutCount => s_layout.Count;
    public static int VerticesCount => s_vertices.Count;
    public static int MaterialCount => s_material.Count;
    public static int ClipCount => s_clip.Count;
    public static int HierarchyCount => s_hierarchy.Count;
    /// <summary>Highest effective layout roots observed for the canvas rebuilt in the current frame.</summary>
    public static int LayoutRootCount { get; private set; }
    /// <summary>Most recent bounded layout-cycle report, retained for the Diagnostics panel.</summary>
    public static string LastLayoutCycleDiagnostic { get; private set; } = string.Empty;

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -900)]
    internal static void RegisterDriver()
    {
        s_registration?.Dispose();
        s_registration = CanvasUpdateDispatcher.Register(s_driver);
    }

    [OnAssemblyUnload]
    internal static void ReleaseDriver()
    {
        s_registration?.Dispose();
        s_registration = null;
        Clear();
        GraphicRegistry.Reset();
    }

    /// <summary>Re-installs the package canvas driver after a collectible package reload.</summary>
    public static void RebindDriver() => RegisterDriver();

    public static void RegisterLayout(UIBehaviour element) => s_layout.Add(element);
    public static void RegisterVertices(UIBehaviour element) => s_vertices.Add(element);
    public static void RegisterMaterial(UIBehaviour element) => s_material.Add(element);
    public static void RegisterClip(UIBehaviour element) => s_clip.Add(element);
    public static void RegisterHierarchy(UIBehaviour element) => s_hierarchy.Add(element);

    internal static void Register(UIBehaviour element, UIDirtyFlags flags)
    {
        if ((flags & UIDirtyFlags.Layout) != 0) s_layout.Add(element);
        if ((flags & UIDirtyFlags.Vertices) != 0) s_vertices.Add(element);
        if ((flags & UIDirtyFlags.Material) != 0) s_material.Add(element);
        if ((flags & UIDirtyFlags.Clip) != 0) s_clip.Add(element);
        if ((flags & UIDirtyFlags.Hierarchy) != 0) s_hierarchy.Add(element);
    }

    internal static void Unregister(UIBehaviour element, UIDirtyFlags flags)
    {
        if ((flags & UIDirtyFlags.Layout) != 0) s_layout.Remove(element);
        if ((flags & UIDirtyFlags.Vertices) != 0) s_vertices.Remove(element);
        if ((flags & UIDirtyFlags.Material) != 0) s_material.Remove(element);
        if ((flags & UIDirtyFlags.Hierarchy) != 0) s_hierarchy.Remove(element);
        if ((flags & UIDirtyFlags.Clip) != 0) s_clip.Remove(element);
    }

    public static void Clear()
    {
        s_layout.Clear();
        s_vertices.Clear();
        s_material.Clear();
        s_clip.Clear();
        s_hierarchy.Clear();
        s_scratch.Clear();
        s_layoutParticipants.Clear();
        s_layoutRoots.Clear();
        s_layoutRootsCanvas = null;
        LayoutRootCount = 0;
        LastLayoutCycleDiagnostic = string.Empty;
        PhaseExecutionCount = 0;
        LastPhase = default;
    }

    private sealed class Driver : ICanvasUpdateDriver, ICanvasLayoutDiagnosticDriver, ICanvasLayoutRootProvider
    {
        public void Execute(GameCanvas canvas, CanvasUpdatePhase phase)
        {
            LastPhase = phase;
            PhaseExecutionCount++;

            if (phase == CanvasUpdatePhase.PreLayout)
            {
                PruneDisposed(s_layout);
                PruneDisposed(s_vertices);
                PruneDisposed(s_material);
                PruneDisposed(s_clip);
                PruneDisposed(s_hierarchy);
                CaptureLayoutRoots(canvas);
                return;
            }

            switch (phase)
            {
                // The host owns the actual tree walks, while XGUI owns invalidation queues. Vertex
                // entries are deliberately completed by GameCanvas.EnsureBaked: an element that is
                // clipped out must stay queued until it has genuinely rebuilt its mesh.
                case CanvasUpdatePhase.LayoutControl:
                    Consume(canvas, s_layout, UIDirtyFlags.Layout);
                    break;
                case CanvasUpdatePhase.ClipUpdate:
                    Consume(canvas, s_clip, UIDirtyFlags.Clip);
                    break;
                case CanvasUpdatePhase.GraphicRebuild:
                    Consume(canvas, s_hierarchy, UIDirtyFlags.Hierarchy);
                    break;
                case CanvasUpdatePhase.MaterialUpdate:
                    Consume(canvas, s_material, UIDirtyFlags.Material);
                    break;
            }
        }

        public void ReportNonConvergentLayout(GameCanvas canvas, int passCount)
            => BuildLayoutCycleDiagnostic(canvas, passCount);

        public void CollectLayoutRoots(GameCanvas canvas, List<GameObject> destination)
        {
            if (!ReferenceEquals(s_layoutRootsCanvas, canvas)) return;
            for (int i = 0; i < s_layoutRoots.Count; i++)
                destination.Add(s_layoutRoots[i]);
        }
    }

    private static void CaptureLayoutRoots(GameCanvas canvas)
    {
        s_layoutParticipants.Clear();
        s_layoutRoots.Clear();
        s_layoutRootsCanvas = canvas;

        for (int i = 0; i < s_layout.Count; i++)
        {
            UIBehaviour element = s_layout[i];
            if (!ReferenceEquals(element.GetCanvas(), canvas)) continue;
            AddLayoutParticipant(element);
            AddLayoutRoot(FindHighestLayoutRoot(element, canvas));
        }

        LayoutRootCount = s_layoutRoots.Count;
    }

    private static void AddLayoutParticipant(UIBehaviour element)
    {
        for (int i = 0; i < s_layoutParticipants.Count; i++)
            if (ReferenceEquals(s_layoutParticipants[i], element)) return;
        s_layoutParticipants.Add(element);
    }

    private static GameObject FindHighestLayoutRoot(UIBehaviour element, GameCanvas canvas)
    {
        GameObject root = element.GameObject;
        for (GameObject? current = root; current != null; current = current.Parent)
        {
            if (HasLayoutController(current)) root = current;
            if (ReferenceEquals(current, canvas.GameObject)) break;
        }
        return root;
    }

    private static bool HasLayoutController(GameObject gameObject)
    {
        foreach (MonoBehaviour component in gameObject.GetComponents<MonoBehaviour>())
        {
            if (!component.EnabledInHierarchy) continue;
            if (component is ILayoutGroup || component is ILayoutSelfController)
                return true;
        }
        return false;
    }

    private static bool IsLayoutParticipant(MonoBehaviour component)
        => component is UIBehaviour && component.EnabledInHierarchy
            && (component is ILayoutElement || component is ILayoutGroup || component is ILayoutSelfController);

    private static void AddLayoutRoot(GameObject candidate)
    {
        for (int i = s_layoutRoots.Count - 1; i >= 0; i--)
        {
            GameObject existing = s_layoutRoots[i];
            if (ReferenceEquals(existing, candidate) || candidate.IsChildOf(existing)) return;
            if (existing.IsChildOf(candidate)) s_layoutRoots.RemoveAt(i);
        }
        s_layoutRoots.Add(candidate);
    }

    private static void BuildLayoutCycleDiagnostic(GameCanvas canvas, int passCount)
    {
        if (s_layoutParticipants.Count == 0)
            CollectLayoutParticipants(canvas.GameObject, canvas);
        if (s_layoutRoots.Count == 0)
            s_layoutRoots.Add(canvas.GameObject);

        LayoutRootCount = s_layoutRoots.Count;
        s_cycleMessage.Clear();
        s_cycleMessage.Append("[XGUI] Layout did not converge after ");
        s_cycleMessage.Append(passCount);
        s_cycleMessage.Append(" passes. Layout roots: ");
        AppendRoots(s_cycleMessage);
        s_cycleMessage.Append(". Participants: ");
        AppendParticipants(s_cycleMessage);
        LastLayoutCycleDiagnostic = s_cycleMessage.ToString();
        Runtime.Debug.LogWarning(LastLayoutCycleDiagnostic);
    }

    private static void CollectLayoutParticipants(GameObject gameObject, GameCanvas canvas)
    {
        if (!ReferenceEquals(gameObject, canvas.GameObject)
            && gameObject.GetComponent<GameCanvas>() is { } nested && !ReferenceEquals(nested, canvas))
            return;

        foreach (MonoBehaviour component in gameObject.GetComponents<MonoBehaviour>())
        {
            if (!IsLayoutParticipant(component)) continue;
            AddLayoutParticipant((UIBehaviour)component);
            AddLayoutRoot(FindHighestLayoutRoot((UIBehaviour)component, canvas));
        }

        foreach (GameObject child in gameObject.Children)
            CollectLayoutParticipants(child, canvas);
    }

    private static void AppendRoots(StringBuilder message)
    {
        for (int i = 0; i < s_layoutRoots.Count; i++)
        {
            if (i > 0) message.Append(", ");
            AppendGameObjectPath(message, s_layoutRoots[i]);
        }
    }

    private static void AppendParticipants(StringBuilder message)
    {
        if (s_layoutParticipants.Count == 0)
        {
            message.Append("<none registered>");
            return;
        }

        for (int i = 0; i < s_layoutParticipants.Count; i++)
        {
            if (i > 0) message.Append(", ");
            UIBehaviour participant = s_layoutParticipants[i];
            AppendGameObjectPath(message, participant.GameObject);
            message.Append(" [");
            message.Append(participant.GetType().Name);
            message.Append(']');
        }
    }

    private static void AppendGameObjectPath(StringBuilder message, GameObject gameObject)
    {
        if (gameObject.Parent != null)
        {
            AppendGameObjectPath(message, gameObject.Parent);
            message.Append('/');
        }
        message.Append(gameObject.Name);
    }

    private static void PruneDisposed(DirtyQueue queue)
    {
        for (int i = queue.Count - 1; i >= 0; i--)
        {
            UIBehaviour element = queue[i];
            if (element.IsDisposed || !element.EnabledInHierarchy)
                queue.RemoveAt(i);
        }
        s_scratch.Clear();
    }

    private static void Consume(GameCanvas canvas, DirtyQueue queue, UIDirtyFlags flags)
    {
        for (int i = 0; i < queue.Count; i++)
        {
            UIBehaviour element = queue[i];
            if (ReferenceEquals(element.GetCanvas(), canvas))
                s_scratch.Add(element);
        }

        for (int i = 0; i < s_scratch.Count; i++)
        {
            UIBehaviour element = s_scratch[i];
            queue.Remove(element);
            element.ClearDirtyFlags(flags);
            element.OnCanvasRebuildCompleted(flags);
        }
        s_scratch.Clear();
    }

    /// <summary>Stable, allocation-free-after-warmup dirty queue.</summary>
    private sealed class DirtyQueue
    {
        private readonly List<UIBehaviour> _items = new(16);
        private readonly HashSet<UIBehaviour> _set = new();

        public int Count => _set.Count;
        public UIBehaviour this[int index] => _items[index];

        public void Add(UIBehaviour element)
        {
            if (_set.Add(element)) _items.Add(element);
        }

        public void Remove(UIBehaviour element)
        {
            if (!_set.Remove(element)) return;
            RemoveItem(element);
        }

        public void RemoveAt(int index)
        {
            UIBehaviour element = _items[index];
            _items.RemoveAt(index);
            _set.Remove(element);
        }

        public void Clear()
        {
            _items.Clear();
            _set.Clear();
        }

        private void RemoveItem(UIBehaviour element)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (!ReferenceEquals(_items[i], element)) continue;
                _items.RemoveAt(i);
                return;
            }
        }
    }
}
