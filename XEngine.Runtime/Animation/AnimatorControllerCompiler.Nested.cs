// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>M32: flatten nested <see cref="AnimatorController.StateMachine"/> trees into the runtime arrays.</summary>
public static partial class AnimatorControllerCompiler
{
    private static void CompileStateMachineTree(
        AnimatorController author,
        AnimatorController.StateMachine root,
        List<StateDef> states,
        List<AnimationClip?> motionClips,
        List<byte> motionKind,
        List<BlendTree?> motionBlendTrees,
        List<int> motionBlendTreeParamIndex,
        List<TransitionDef> flatTransitions,
        List<ConditionDef> flatConditions,
        BehaviourBag behaviours,
        out int defaultStateIndex,
        out int anyStateTransitionsStart,
        out int anyStateTransitionsCount)
    {
        var parentOf = new Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?>();
        var localIndex = new Dictionary<AnimatorController.StateMachine, Dictionary<string, int>>();
        var defaultOf = new Dictionary<AnimatorController.StateMachine, int>();

        CollectNestedStates(author, root, parent: null, parentOf, localIndex, defaultOf,
            states, motionClips, motionKind, motionBlendTrees, motionBlendTreeParamIndex, behaviours);

        WireNestedMachine(author, root, parentOf, localIndex, defaultOf, states, flatTransitions, flatConditions);

        anyStateTransitionsStart = flatTransitions.Count;
        AppendNestedList(root.AnyStateTransitions, author, root, parentOf, localIndex, defaultOf,
            flatTransitions, flatConditions);
        anyStateTransitionsCount = flatTransitions.Count - anyStateTransitionsStart;

        defaultStateIndex = ResolveNestedEntry(root, parentOf, localIndex, defaultOf);
        if (defaultStateIndex < 0)
            defaultStateIndex = 0;
    }

    private static void CollectNestedStates(
        AnimatorController author,
        AnimatorController.StateMachine sm,
        AnimatorController.StateMachine? parent,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf,
        List<StateDef> states,
        List<AnimationClip?> motionClips,
        List<byte> motionKind,
        List<BlendTree?> motionBlendTrees,
        List<int> motionBlendTreeParamIndex,
        BehaviourBag behaviours)
    {
        parentOf[sm] = parent;
        var map = new Dictionary<string, int>(sm.States.Count);
        localIndex[sm] = map;

        var authorStates = sm.States;
        for (int i = 0; i < authorStates.Count; i++)
        {
            var s = authorStates[i];
            int globalIndex = states.Count;
            map[s.Name] = globalIndex;
            states.Add(MakeStateDef(author, s, globalIndex));
            behaviours.Bind(states, globalIndex, s);

            BlendTree? bt = s.BlendTree?.Res;
            if (bt != null)
            {
                motionKind.Add(1);
                motionClips.Add(null);
                motionBlendTrees.Add(bt);
                motionBlendTreeParamIndex.Add(string.IsNullOrEmpty(bt.ParameterName)
                    ? -1
                    : author.GetParameterIndex(bt.ParameterName));
            }
            else
            {
                motionKind.Add(0);
                motionClips.Add(ResolveStateClip(s));
                motionBlendTrees.Add(null);
                motionBlendTreeParamIndex.Add(-1);
            }
        }

        defaultOf[sm] = map.TryGetValue(sm.DefaultStateName, out int dsi)
            ? dsi
            : (authorStates.Count > 0 ? map[authorStates[0].Name] : -1);

        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
            CollectNestedStates(author, children[i], sm, parentOf, localIndex, defaultOf,
                states, motionClips, motionKind, motionBlendTrees, motionBlendTreeParamIndex, behaviours);
    }

    private static void WireNestedMachine(
        AnimatorController author,
        AnimatorController.StateMachine sm,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf,
        List<StateDef> states,
        List<TransitionDef> flatTransitions,
        List<ConditionDef> flatConditions)
    {
        var transByName = new Dictionary<string, AnimatorController.Transition>(sm.Transitions.Count);
        for (int i = 0; i < sm.Transitions.Count; i++)
        {
            var t = sm.Transitions[i];
            if (!string.IsNullOrEmpty(t.Name))
                transByName[t.Name] = t;
        }

        var authorStates = sm.States;
        var map = localIndex[sm];
        for (int i = 0; i < authorStates.Count; i++)
        {
            var s = authorStates[i];
            int start = flatTransitions.Count;
            if (map.TryGetValue(s.Name, out int self) && defaultOf.TryGetValue(sm, out int defIdx)
                && self == defIdx)
            {
                var entries = sm.EntryTransitions;
                for (int e = 0; e < entries.Count; e++)
                {
                    if (entries[e].Conditions.Count == 0)
                        continue;
                    AppendNestedOne(entries[e], author, sm, parentOf, localIndex, defaultOf,
                        flatTransitions, flatConditions);
                }
            }
            var names = s.TransitionNames;
            for (int n = 0; n < names.Count; n++)
            {
                if (!transByName.TryGetValue(names[n], out var t))
                    continue;
                AppendNestedOne(t, author, sm, parentOf, localIndex, defaultOf, flatTransitions, flatConditions);
            }
            if (!map.TryGetValue(s.Name, out int global))
                continue;
            StateDef defState = states[global];
            defState.TransitionsStart = start;
            defState.TransitionsCount = flatTransitions.Count - start;
            states[global] = defState;
        }

        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
            WireNestedMachine(author, children[i], parentOf, localIndex, defaultOf, states,
                flatTransitions, flatConditions);
    }

    private static void AppendNestedList(
        List<AnimatorController.Transition> transitions,
        AnimatorController author,
        AnimatorController.StateMachine sm,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf,
        List<TransitionDef> outTransitions,
        List<ConditionDef> outConditions)
    {
        for (int i = 0; i < transitions.Count; i++)
            AppendNestedOne(transitions[i], author, sm, parentOf, localIndex, defaultOf,
                outTransitions, outConditions);
    }

    private static void AppendNestedOne(
        AnimatorController.Transition t,
        AnimatorController author,
        AnimatorController.StateMachine sm,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf,
        List<TransitionDef> outTransitions,
        List<ConditionDef> outConditions)
    {
        if (t.DestinationStateName == AnimatorController.ExitStateName
            && parentOf.TryGetValue(sm, out AnimatorController.StateMachine? parent)
            && parent != null)
        {
            var outs = parent.StateMachineTransitions;
            bool any = false;
            for (int i = 0; i < outs.Count; i++)
            {
                if (outs[i].SourceStateMachineName != sm.Name)
                    continue;
                any = true;
                EmitNested(outs[i], author,
                    ResolveNestedDest(outs[i].DestinationStateName, parent, parentOf, localIndex, defaultOf),
                    outTransitions, outConditions);
            }
            if (any)
                return;
        }

        int dest = ResolveNestedDest(t.DestinationStateName, sm, parentOf, localIndex, defaultOf);
        EmitNested(t, author, dest, outTransitions, outConditions);
    }

    private static void EmitNested(
        AnimatorController.Transition t,
        AnimatorController author,
        int dest,
        List<TransitionDef> outTransitions,
        List<ConditionDef> outConditions)
    {
        int condStart = outConditions.Count;
        var conds = t.Conditions;
        for (int i = 0; i < conds.Count; i++)
        {
            var cond = conds[i];
            outConditions.Add(new ConditionDef
            {
                Mode = cond.Mode,
                ParamIndex = author.GetParameterIndex(cond.ParamName),
                Threshold = cond.Threshold,
            });
        }

        outTransitions.Add(new TransitionDef
        {
            DestinationStateIndex = dest,
            Duration = t.Duration,
            Offset = t.Offset,
            HasExitTime = t.HasExitTime,
            ExitTime = t.ExitTime,
            Solo = t.Solo,
            Mute = t.Mute,
            HasFixedDuration = t.HasFixedDuration,
            InterruptionSource = t.InterruptionSource,
            OrderedInterruption = t.OrderedInterruption,
            CanTransitionToSelf = t.CanTransitionToSelf,
            ConditionsStart = condStart,
            ConditionsCount = outConditions.Count - condStart,
        });
    }

    private static int ResolveNestedDest(
        string destName,
        AnimatorController.StateMachine sm,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf)
    {
        if (string.IsNullOrEmpty(destName))
            return -1;
        if (destName == AnimatorController.ExitStateName)
            return ResolveNestedExit(sm, parentOf, localIndex, defaultOf);
        if (destName == AnimatorController.EntryStateName)
            return ResolveNestedEntry(sm, parentOf, localIndex, defaultOf);

        if (localIndex.TryGetValue(sm, out Dictionary<string, int>? map)
            && map.TryGetValue(destName, out int idx))
            return idx;

        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i].Name == destName)
                return ResolveNestedEntry(children[i], parentOf, localIndex, defaultOf);
        }
        return -1;
    }

    private static int ResolveNestedEntry(
        AnimatorController.StateMachine sm,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf)
    {
        var entries = sm.EntryTransitions;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Conditions.Count != 0)
                continue;
            int dest = ResolveNestedDest(entries[i].DestinationStateName, sm, parentOf, localIndex, defaultOf);
            if (dest >= 0)
                return dest;
        }
        return defaultOf.TryGetValue(sm, out int d) ? d : -1;
    }

    private static int ResolveNestedExit(
        AnimatorController.StateMachine sm,
        Dictionary<AnimatorController.StateMachine, AnimatorController.StateMachine?> parentOf,
        Dictionary<AnimatorController.StateMachine, Dictionary<string, int>> localIndex,
        Dictionary<AnimatorController.StateMachine, int> defaultOf)
    {
        if (!parentOf.TryGetValue(sm, out AnimatorController.StateMachine? parent) || parent == null)
            return defaultOf.TryGetValue(sm, out int self) ? self : -1;

        var outs = parent.StateMachineTransitions;
        for (int i = 0; i < outs.Count; i++)
        {
            if (outs[i].SourceStateMachineName != sm.Name)
                continue;
            if (outs[i].Conditions.Count != 0)
                continue;
            int dest = ResolveNestedDest(outs[i].DestinationStateName, parent, parentOf, localIndex, defaultOf);
            if (dest >= 0)
                return dest;
        }
        return ResolveNestedEntry(parent, parentOf, localIndex, defaultOf);
    }
}
