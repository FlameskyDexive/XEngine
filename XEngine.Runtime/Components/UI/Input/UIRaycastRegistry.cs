// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Runtime-neutral registry for drawable UI hit-test candidates.
///
/// The XGUI package owns the public <c>GraphicRegistry</c> facade, while the host keeps this
/// registry free of package references so legacy UI can continue to work when XGUI is absent.
/// Candidates are registered during component lifecycle and receive their final render-order and
/// clip state during the Canvas rebuild. Pointer input therefore consumes a stable list instead of
/// walking the scene hierarchy on every event-system tick.
/// </summary>
internal static class UIRaycastRegistry
{
    private readonly struct State
    {
        public readonly long SortKey;
        public readonly CanvasClipState Clip;
        public readonly bool BlocksRaycasts;
        public readonly Rect? RaycastRect;
        public readonly long RegistrationOrder;

        public State(long sortKey, CanvasClipState clip, bool blocksRaycasts, Rect? raycastRect,
            long registrationOrder)
        {
            SortKey = sortKey;
            Clip = clip;
            BlocksRaycasts = blocksRaycasts;
            RaycastRect = raycastRect;
            RegistrationOrder = registrationOrder;
        }
    }

    private static readonly Dictionary<GameCanvas, List<UIBehaviour>> s_candidates = new();
    private static readonly Dictionary<UIBehaviour, GameCanvas?> s_owners = new();
    private static readonly Dictionary<UIBehaviour, State> s_states = new();
    private static readonly List<UIBehaviour> s_empty = new(0);
    private static readonly List<UIBehaviour> s_combined = new(16);
    private static readonly HashSet<GameCanvas> s_orderDirty = new();
    private static readonly CandidateComparer s_candidateComparer = new();
    private static long s_nextRegistrationOrder;
    private static IUIRaycastProvider? s_externalProvider;

    internal static IUIRaycastProvider? ExternalProvider => s_externalProvider;

    /// <summary>
    /// Installs the active package raycast provider. The provider is optional so the Runtime can
    /// continue to run scenes containing only legacy UI when XGUI is not installed.
    /// </summary>
    public static void SetExternalProvider(IUIRaycastProvider? provider)
        => s_externalProvider = provider;

    public static void Register(UIBehaviour element)
    {
        if (!element.IsRaycastCandidate) return;

        GameCanvas? canvas = element.GetCanvas();
        if (s_owners.TryGetValue(element, out GameCanvas? previous))
        {
            if (ReferenceEquals(previous, canvas)) return;
            RemoveFromCanvas(element, previous);
        }

        s_owners[element] = canvas;
        if (!s_states.ContainsKey(element))
            s_states.Add(element, new State(long.MinValue, CanvasClipState.None, true, null,
                s_nextRegistrationOrder++));
        if (canvas == null) return;

        if (!s_candidates.TryGetValue(canvas, out List<UIBehaviour>? list))
            s_candidates.Add(canvas, list = new List<UIBehaviour>(8));
        if (!list.Contains(element))
        {
            list.Add(element);
            s_orderDirty.Add(canvas);
        }
    }

    public static void Unregister(UIBehaviour element)
    {
        if (!s_owners.TryGetValue(element, out GameCanvas? canvas)) return;
        RemoveFromCanvas(element, canvas);
        s_owners.Remove(element);
        s_states.Remove(element);
    }

    public static void UpdateState(UIBehaviour element, long sortKey, bool blocksRaycasts,
        in CanvasClipState clip, Rect? raycastRect = null)
    {
        Register(element);
        if (!s_owners.ContainsKey(element)) return;
        State previous = s_states[element];
        s_states[element] = new State(sortKey, clip, blocksRaycasts, raycastRect,
            previous.RegistrationOrder);
        if (previous.SortKey != sortKey && s_owners[element] is { } canvas)
            s_orderDirty.Add(canvas);
    }

    public static IReadOnlyList<UIBehaviour> GetCandidates(GameCanvas canvas)
    {
        List<UIBehaviour>? list = GetLegacyCandidates(canvas);
        if (s_externalProvider is not { } provider)
            return list ?? s_empty;

        IReadOnlyList<UIBehaviour> packageCandidates = provider.GetRaycastCandidates(canvas);
        if (packageCandidates.Count == 0)
            return list ?? s_empty;
        if (list == null || list.Count == 0)
            return packageCandidates;

        // Both registries publish back-to-front lists. Merge them linearly so mixed legacy/package
        // scenes do not pay an O(n log n) sort on every pointer query.
        s_combined.Clear();
        int legacyIndex = 0;
        int packageIndex = 0;
        while (legacyIndex < list.Count || packageIndex < packageCandidates.Count)
        {
            if (packageIndex >= packageCandidates.Count)
            {
                s_combined.Add(list[legacyIndex++]);
                continue;
            }
            if (legacyIndex >= list.Count)
            {
                s_combined.Add(packageCandidates[packageIndex++]);
                continue;
            }

            UIBehaviour legacy = list[legacyIndex];
            UIBehaviour package = packageCandidates[packageIndex];
            if (ReferenceEquals(legacy, package))
            {
                s_combined.Add(legacy);
                legacyIndex++;
                packageIndex++;
                continue;
            }
            if (s_combinedComparer.Compare(legacy, package) <= 0)
            {
                s_combined.Add(legacy);
                legacyIndex++;
            }
            else
            {
                s_combined.Add(package);
                packageIndex++;
            }
        }
        return s_combined;
    }

    private static List<UIBehaviour>? GetLegacyCandidates(GameCanvas canvas)
    {
        if (!s_candidates.TryGetValue(canvas, out List<UIBehaviour>? list)) return null;

        // Reparenting an active object does not invoke OnEnable. Remove entries that still point at
        // the previous Canvas before input consumes the list; the destination Canvas will register
        // the element during its next rebuild. This is a bounded per-canvas cleanup, not a scene walk.
        for (int i = list.Count - 1; i >= 0; i--)
        {
            UIBehaviour element = list[i];
            if (!element.IsDisposed && ReferenceEquals(element.GetCanvas(), canvas)) continue;
            list.RemoveAt(i);
            s_owners.Remove(element);
            s_states.Remove(element);
            s_orderDirty.Add(canvas);
        }
        if (list.Count == 0)
        {
            s_candidates.Remove(canvas);
            s_orderDirty.Remove(canvas);
            return null;
        }
        if (s_orderDirty.Remove(canvas)) list.Sort(s_candidateComparer);
        return list;
    }

    public static bool TryGetState(UIBehaviour element, out long sortKey, out CanvasClipState clip,
        out bool blocksRaycasts, out Rect? raycastRect)
    {
        if (s_externalProvider is { } provider && provider.TryGetRaycastState(element, out sortKey,
            out clip, out blocksRaycasts, out raycastRect))
            return true;

        if (s_states.TryGetValue(element, out State state))
        {
            sortKey = state.SortKey;
            clip = state.Clip;
            blocksRaycasts = state.BlocksRaycasts;
            raycastRect = state.RaycastRect;
            return true;
        }

        sortKey = long.MinValue;
        clip = CanvasClipState.None;
        blocksRaycasts = true;
        raycastRect = null;
        return false;
    }

    public static void Reset()
    {
        s_candidates.Clear();
        s_combined.Clear();
        s_owners.Clear();
        s_states.Clear();
        s_orderDirty.Clear();
        s_nextRegistrationOrder = 0;
    }

    private static void RemoveFromCanvas(UIBehaviour element, GameCanvas? canvas)
    {
        if (canvas == null || !s_candidates.TryGetValue(canvas, out List<UIBehaviour>? list)) return;
        list.Remove(element);
        if (list.Count == 0)
        {
            s_candidates.Remove(canvas);
            s_orderDirty.Remove(canvas);
            return;
        }
        s_orderDirty.Add(canvas);
    }

    /// <summary>Orders candidates from back to front. Input walks this list backwards and stops at the first hit.</summary>
    private sealed class CandidateComparer : IComparer<UIBehaviour>
    {
        public int Compare(UIBehaviour? left, UIBehaviour? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            long leftSort = s_states.TryGetValue(left, out State leftState)
                ? leftState.SortKey : long.MinValue;
            long rightSort = s_states.TryGetValue(right, out State rightState)
                ? rightState.SortKey : long.MinValue;
            int sort = leftSort.CompareTo(rightSort);
            if (sort != 0) return sort;

            long leftOrder = s_states.TryGetValue(left, out leftState)
                ? leftState.RegistrationOrder : long.MinValue;
            long rightOrder = s_states.TryGetValue(right, out rightState)
                ? rightState.RegistrationOrder : long.MinValue;
            return leftOrder.CompareTo(rightOrder);
        }
    }

    private sealed class CombinedCandidateComparer : IComparer<UIBehaviour>
    {
        public int Compare(UIBehaviour? left, UIBehaviour? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;

            GetState(left, out long leftSort, out long leftRegistration);
            GetState(right, out long rightSort, out long rightRegistration);
            int sort = leftSort.CompareTo(rightSort);
            return sort != 0 ? sort : leftRegistration.CompareTo(rightRegistration);
        }

        private static void GetState(UIBehaviour element, out long sortKey, out long registrationOrder)
        {
            if (s_externalProvider?.TryGetRaycastState(element, out sortKey, out _, out _, out _) == true)
            {
                registrationOrder = element.InstanceID;
                return;
            }

            if (s_states.TryGetValue(element, out State state))
            {
                sortKey = state.SortKey;
                registrationOrder = state.RegistrationOrder;
                return;
            }

            sortKey = long.MinValue;
            registrationOrder = element.InstanceID;
        }
    }

    private static readonly CombinedCandidateComparer s_combinedComparer = new();
}

/// <summary>
/// Runtime-neutral bridge for package-owned UI hit-test registries. Implementations must return
/// candidates from back to front; the raycaster walks the list backwards to select the top-most hit.
/// </summary>
public interface IUIRaycastProvider
{
    IReadOnlyList<UIBehaviour> GetRaycastCandidates(GameCanvas canvas);

    bool TryGetRaycastState(UIBehaviour element, out long sortKey, out CanvasClipState clip,
        out bool blocksRaycasts, out Rect? raycastRect);
}

/// <summary>Public package boundary for installing a raycast provider without exposing host storage.</summary>
public static class UIRaycastProviderRegistration
{
    public static void Register(IUIRaycastProvider provider)
        => UIRaycastRegistry.SetExternalProvider(provider);

    public static void Unregister(IUIRaycastProvider provider)
    {
        // A package unload must not clear a newer provider installed by another package.
        if (ReferenceEquals(provider, UIRaycastRegistry.ExternalProvider))
            UIRaycastRegistry.SetExternalProvider(null);
    }
}
