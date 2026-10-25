// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Layer- and state-machine-path aware read/write over an <see cref="AnimatorController"/>.
/// Empty <see cref="AnimatorController.Layers"/> is treated as a single Base Layer backed by the root
/// graph; the first Layers-tab mutation migrates that graph into <c>Layers[0]</c>. Nested machines live
/// on <see cref="AnimatorController.Layer.Root"/>; a null Root still reads the legacy flat lists.
/// </summary>
internal static partial class AnimatorControllerEditGraph
{
    public static readonly IReadOnlyList<string> RootPath = Array.Empty<string>();

    public static int EffectiveLayerCount(AnimatorController controller)
        => controller.Layers.Count > 0 ? controller.Layers.Count : 1;

    public static int ClampLayer(AnimatorController controller, int layer)
    {
        int n = EffectiveLayerCount(controller);
        if (n <= 0) return 0;
        if (layer < 0) return 0;
        if (layer >= n) return n - 1;
        return layer;
    }

    public static string LayerName(AnimatorController controller, int layer)
    {
        layer = ClampLayer(controller, layer);
        if (controller.Layers.Count == 0)
            return "Base Layer";
        string name = controller.Layers[layer].Name;
        return string.IsNullOrEmpty(name) ? "Base Layer" : name;
    }

    /// <summary>
    /// The machine currently being edited. Does not mutate the asset; a missing Root is wrapped
    /// around the legacy flat lists (same list instances).
    /// </summary>
    public static AnimatorController.StateMachine Machine(
        AnimatorController controller, int layer, IReadOnlyList<string>? path = null)
    {
        AnimatorController.StateMachine sm = EffectiveRoot(controller, layer);
        if (path == null || path.Count == 0)
            return sm;
        for (int i = 0; i < path.Count; i++)
        {
            AnimatorController.StateMachine? child = FindChild(sm, path[i]);
            if (child == null)
                throw new KeyNotFoundException($"State machine '{path[i]}' not found.");
            sm = child;
        }
        return sm;
    }

    public static AnimatorController.StateMachine EffectiveRoot(AnimatorController controller, int layer)
    {
        layer = ClampLayer(controller, layer);
        if (controller.Layers.Count == 0)
        {
            return new AnimatorController.StateMachine
            {
                Name = "Base Layer",
                DefaultStateName = controller.DefaultStateName,
                States = controller.States,
                Transitions = controller.Transitions,
                AnyStateTransitions = controller.AnyStateTransitions,
            };
        }

        AnimatorController.Layer L = controller.Layers[layer];
        if (L.Root != null)
            return L.Root;
        return new AnimatorController.StateMachine
        {
            Name = string.IsNullOrEmpty(L.Name) ? "Base Layer" : L.Name,
            DefaultStateName = L.DefaultStateName,
            States = L.States,
            Transitions = L.Transitions,
            AnyStateTransitions = L.AnyStateTransitions,
        };
    }

    /// <summary>
    /// Moves the layer's flat lists into <see cref="AnimatorController.Layer.Root"/> so nested
    /// machines can be stored. No-op when Root already exists.
    /// </summary>
    public static AnimatorController.StateMachine EnsureRoot(AnimatorController controller, int layer)
    {
        MigrateRootToLayers(controller);
        AnimatorController.Layer L = controller.Layers[ClampLayer(controller, layer)];
        if (L.Root != null)
            return L.Root;
        L.Root = new AnimatorController.StateMachine
        {
            Name = string.IsNullOrEmpty(L.Name) ? "Base Layer" : L.Name,
            DefaultStateName = L.DefaultStateName,
            States = L.States,
            Transitions = L.Transitions,
            AnyStateTransitions = L.AnyStateTransitions,
        };
        L.States = new List<AnimatorController.State>();
        L.Transitions = new List<AnimatorController.Transition>();
        L.AnyStateTransitions = new List<AnimatorController.Transition>();
        return L.Root;
    }

    public static List<AnimatorController.State> States(
        AnimatorController controller, int layer, IReadOnlyList<string>? path = null)
        => Machine(controller, layer, path).States;

    public static List<AnimatorController.Transition> Transitions(
        AnimatorController controller, int layer, IReadOnlyList<string>? path = null)
        => Machine(controller, layer, path).Transitions;

    public static List<AnimatorController.Transition> AnyStateTransitions(
        AnimatorController controller, int layer, IReadOnlyList<string>? path = null)
        => Machine(controller, layer, path).AnyStateTransitions;

    public static List<AnimatorController.StateMachine> ChildMachines(
        AnimatorController controller, int layer, IReadOnlyList<string>? path = null)
        => Machine(controller, layer, path).StateMachines;

    public static string GetDefaultStateName(
        AnimatorController controller, int layer, IReadOnlyList<string>? path = null)
        => Machine(controller, layer, path).DefaultStateName;

    public static void SetDefaultStateName(
        AnimatorController controller, int layer, string name, IReadOnlyList<string>? path = null)
    {
        AnimatorController.StateMachine sm = Machine(controller, layer, path);
        sm.DefaultStateName = name;
        if (path != null && path.Count > 0)
            return;
        if (controller.Layers.Count == 0)
        {
            controller.DefaultStateName = name;
            return;
        }
        AnimatorController.Layer L = controller.Layers[ClampLayer(controller, layer)];
        L.DefaultStateName = name;
        if (L.Root != null)
            L.Root.DefaultStateName = name;
    }

    /// <summary>Moves the root graph into <c>Layers[0]</c> so subsequent layer edits persist.</summary>
    public static void MigrateRootToLayers(AnimatorController controller)
    {
        if (controller.Layers.Count > 0)
            return;
        controller.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            Weight = 1f,
            Blending = AnimatorLayerBlendingMode.Override,
            DefaultStateName = controller.DefaultStateName,
            States = controller.States,
            Transitions = controller.Transitions,
            AnyStateTransitions = controller.AnyStateTransitions,
        });
        controller.States = new List<AnimatorController.State>();
        controller.Transitions = new List<AnimatorController.Transition>();
        controller.AnyStateTransitions = new List<AnimatorController.Transition>();
        controller.DefaultStateName = string.Empty;
    }

    public static AnimatorController.Layer MutableLayer(AnimatorController controller, int layer)
    {
        MigrateRootToLayers(controller);
        return controller.Layers[ClampLayer(controller, layer)];
    }

    public static void AddLayer(AnimatorController controller)
    {
        MigrateRootToLayers(controller);
        controller.Layers.Add(new AnimatorController.Layer
        {
            Name = UniqueLayerName(controller, "New Layer"),
            Weight = 1f,
            Blending = AnimatorLayerBlendingMode.Override,
        });
    }

    /// <summary>Removes a non-base layer. Layer 0 (Base Layer) is kept.</summary>
    public static bool RemoveLayer(AnimatorController controller, int layer)
    {
        MigrateRootToLayers(controller);
        if (controller.Layers.Count <= 1)
            return false;
        if (layer <= 0 || layer >= controller.Layers.Count)
            return false;
        controller.Layers.RemoveAt(layer);
        return true;
    }

    public static AnimatorController.StateMachine CreateStateMachine(
        AnimatorController controller, int layer, IReadOnlyList<string>? path, string? baseName, float posX, float posY)
    {
        EnsureRoot(controller, layer);
        AnimatorController.StateMachine parent = Machine(controller, layer, path);
        string name = UniqueMachineName(parent, string.IsNullOrEmpty(baseName) ? "New StateMachine" : baseName);
        var sm = new AnimatorController.StateMachine
        {
            Name = name,
            PosX = posX,
            PosY = posY,
        };
        parent.StateMachines.Add(sm);
        return sm;
    }

    public static bool DeleteStateMachine(
        AnimatorController controller, int layer, IReadOnlyList<string>? path, string name)
    {
        EnsureRoot(controller, layer);
        AnimatorController.StateMachine parent = Machine(controller, layer, path);
        int idx = IndexOfChild(parent, name);
        if (idx < 0)
            return false;
        parent.StateMachines.RemoveAt(idx);
        var outs = parent.StateMachineTransitions;
        for (int i = outs.Count - 1; i >= 0; i--)
        {
            if (outs[i].SourceStateMachineName == name || outs[i].DestinationStateName == name)
                outs.RemoveAt(i);
        }
        return true;
    }

    /// <summary>Moves a state from <paramref name="fromPath"/> into the child machine <paramref name="targetMachine"/>.</summary>
    public static bool MoveStateInto(
        AnimatorController controller, int layer, IReadOnlyList<string>? fromPath,
        string stateName, string targetMachine)
    {
        EnsureRoot(controller, layer);
        AnimatorController.StateMachine src = Machine(controller, layer, fromPath);
        AnimatorController.StateMachine? dest = FindChild(src, targetMachine);
        if (dest == null)
            return false;
        AnimatorController.State? state = FindStateIn(src.States, stateName);
        if (state == null)
            return false;

        src.States.Remove(state);
        dest.States.Add(state);

        var keep = new List<AnimatorController.Transition>();
        var srcTrans = src.Transitions;
        for (int i = srcTrans.Count - 1; i >= 0; i--)
        {
            var t = srcTrans[i];
            bool owned = false;
            for (int n = 0; n < state.TransitionNames.Count; n++)
            {
                if (state.TransitionNames[n] == t.Name) { owned = true; break; }
            }
            if (!owned)
                continue;
            srcTrans.RemoveAt(i);
            keep.Add(t);
        }
        for (int i = keep.Count - 1; i >= 0; i--)
            dest.Transitions.Add(keep[i]);

        if (src.DefaultStateName == stateName)
            src.DefaultStateName = src.States.Count > 0 ? src.States[0].Name : string.Empty;
        if (string.IsNullOrEmpty(dest.DefaultStateName))
            dest.DefaultStateName = stateName;
        return true;
    }

    public static string UniqueLayerName(AnimatorController controller, string baseName)
    {
        string name = baseName;
        int n = 1;
        while (LayerNameExists(controller, name))
            name = $"{baseName} {++n}";
        return name;
    }

    private static bool LayerNameExists(AnimatorController controller, string name)
    {
        var layers = controller.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            if (layers[i].Name == name)
                return true;
        }
        return false;
    }

    public static string UniqueStateName(
        AnimatorController controller, int layer, string baseName, IReadOnlyList<string>? path = null)
    {
        var states = States(controller, layer, path);
        var machines = ChildMachines(controller, layer, path);
        string name = baseName;
        int n = 1;
        while (StateNameExists(states, name) || ChildNameExists(machines, name))
            name = $"{baseName} {++n}";
        return name;
    }

    public static string UniqueMachineName(AnimatorController.StateMachine parent, string baseName)
    {
        string name = baseName;
        int n = 1;
        while (StateNameExists(parent.States, name) || ChildNameExists(parent.StateMachines, name))
            name = $"{baseName} {++n}";
        return name;
    }

    private static bool StateNameExists(List<AnimatorController.State> states, string name)
    {
        for (int i = 0; i < states.Count; i++)
        {
            if (states[i].Name == name)
                return true;
        }
        return false;
    }

    private static bool ChildNameExists(List<AnimatorController.StateMachine> machines, string name)
    {
        for (int i = 0; i < machines.Count; i++)
        {
            if (machines[i].Name == name)
                return true;
        }
        return false;
    }

    public static string UniqueTransitionName(
        AnimatorController controller, int layer, string from, string to, IReadOnlyList<string>? path = null)
    {
        var sm = Machine(controller, layer, path);
        string name = $"{from}->{to}";
        int n = 1;
        while (TransitionNameExists(sm.Transitions, name)
               || TransitionNameExists(sm.AnyStateTransitions, name)
               || TransitionNameExists(sm.EntryTransitions, name)
               || TransitionNameExists(sm.StateMachineTransitions, name))
            name = $"{from}->{to} {++n}";
        return name;
    }

    /// <summary>
    /// Move <paramref name="transitionName"/> by <paramref name="delta"/> slots in the state's
    /// <see cref="AnimatorController.State.TransitionNames"/> (runtime priority order).
    /// </summary>
    public static bool MoveTransition(
        AnimatorController controller, int layer, IReadOnlyList<string>? path,
        string stateName, string transitionName, int delta)
    {
        var state = FindState(controller, layer, stateName, path);
        if (state == null) return false;
        var names = state.TransitionNames;
        int i = names.IndexOf(transitionName);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= names.Count) return false;
        names.RemoveAt(i);
        names.Insert(j, transitionName);
        return true;
    }

    private static bool TransitionNameExists(List<AnimatorController.Transition> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Name == name)
                return true;
        }
        return false;
    }

    public static AnimatorController.State? FindState(
        AnimatorController controller, int layer, string name, IReadOnlyList<string>? path = null)
        => FindStateIn(States(controller, layer, path), name);

    public static AnimatorController.StateMachine? FindChild(AnimatorController.StateMachine parent, string name)
    {
        var children = parent.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Name == name)
                return children[i];
        }
        return null;
    }

    public static AnimatorController.Transition? FindTransition(
        AnimatorController controller, int layer, string name, IReadOnlyList<string>? path = null)
    {
        var sm = Machine(controller, layer, path);
        return FindTransitionIn(sm.Transitions, name)
            ?? FindTransitionIn(sm.AnyStateTransitions, name)
            ?? FindTransitionIn(sm.EntryTransitions, name)
            ?? FindTransitionIn(sm.StateMachineTransitions, name);
    }

    private static AnimatorController.State? FindStateIn(List<AnimatorController.State> states, string name)
    {
        for (int i = 0; i < states.Count; i++)
        {
            if (states[i].Name == name)
                return states[i];
        }
        return null;
    }

    private static int IndexOfChild(AnimatorController.StateMachine parent, string name)
    {
        var children = parent.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Name == name)
                return i;
        }
        return -1;
    }

    private static AnimatorController.Transition? FindTransitionIn(List<AnimatorController.Transition> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Name == name)
                return list[i];
        }
        return null;
    }
}
