// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// Compiles an <see cref="AnimatorController"/> (author form: named, human-readable) into an
/// <see cref="AnimatorControllerConstant"/> (runtime form: flat arrays of value-type defs with hashed
/// names and integer indices). This is the import step — run once when the controller asset is
/// imported (or in tests), then the constant is cached and evaluated per-frame by
/// <see cref="AnimatorControllerRuntime"/>.
/// </summary>
public static partial class AnimatorControllerCompiler
{
    /// <summary>
    /// Produces the runtime constant for <paramref name="author"/>. State names are hashed, transitions
    /// and conditions flattened into contiguous arrays (each state owns a [Start,Start+Count) slice),
    /// and each state's clip is resolved from its <see cref="AnimatorController.State.Motion"/>.
    /// When <see cref="AnimatorController.Layers"/> is empty, the root graph is compiled as Base Layer.
    /// </summary>
    public static AnimatorControllerConstant Compile(AnimatorController author)
    {
        if (author is AnimatorOverrideController ov && ov.Base.Res != null)
        {
            AnimatorControllerConstant compiled = Compile(ov.Base.Res);
            ApplyOverrides(compiled, ov);
            return compiled;
        }

        var c = new AnimatorControllerConstant();

        // ---- Parameters (shared across layers) ----
        var authorParams = author.Parameters;
        c.Parameters = new ParameterDef[authorParams.Count];
        for (int i = 0; i < authorParams.Count; i++)
        {
            var p = authorParams[i];
            c.Parameters[i] = new ParameterDef
            {
                NameHash = AnimationNameHash.Hash(p.Name),
                Type = p.Type,
                DefaultFloat = p.DefaultFloat,
                DefaultInt = p.DefaultInt,
                DefaultBool = p.DefaultBool,
            };
        }

        var states = new List<StateDef>();
        var motionClips = new List<AnimationClip?>();
        var motionKind = new List<byte>();
        var motionBlendTrees = new List<BlendTree?>();
        var motionBlendTreeParamIndex = new List<int>();
        var flatTransitions = new List<TransitionDef>();
        var flatConditions = new List<ConditionDef>();
        var behaviours = new BehaviourBag();

        var layersSrc = author.Layers;
        bool layered = layersSrc.Count > 0;
        int layerCount = layered ? layersSrc.Count : 1;
        var layerDefs = new LayerDef[layerCount];

        for (int li = 0; li < layerCount; li++)
        {
            string defaultName;
            List<AnimatorController.State> layerStates;
            List<AnimatorController.Transition> layerTransitions;
            List<AnimatorController.Transition> layerAny;
            string layerName;
            float weight;
            AnimatorLayerBlendingMode blending;
            AvatarMask? mask;
            int weightParamIndex = -1;
            int syncedLayerIndex = -1;
            bool syncedTiming = false;
            bool ikPass = false;

            if (!layered)
            {
                defaultName = author.DefaultStateName;
                layerStates = author.States;
                layerTransitions = author.Transitions;
                layerAny = author.AnyStateTransitions;
                layerName = "Base Layer";
                weight = 1f;
                blending = AnimatorLayerBlendingMode.Override;
                mask = null;
            }
            else
            {
                AnimatorController.Layer L = layersSrc[li];
                defaultName = L.DefaultStateName;
                layerStates = L.States;
                layerTransitions = L.Transitions;
                layerAny = L.AnyStateTransitions;
                layerName = string.IsNullOrEmpty(L.Name) ? "Base Layer" : L.Name;
                weight = L.Weight;
                blending = L.Blending;
                mask = L.Mask.Res;
                weightParamIndex = string.IsNullOrEmpty(L.WeightParameterName)
                    ? -1
                    : author.GetParameterIndex(L.WeightParameterName);
                syncedLayerIndex = L.SyncedLayerIndex;
                syncedTiming = L.SyncedLayerAffectsTiming;
                ikPass = L.IKPass;
            }

            int statesStart = states.Count;
            if (layered && layersSrc[li].Root != null)
            {
                CompileStateMachineTree(
                    author, layersSrc[li].Root!,
                    states, motionClips, motionKind, motionBlendTrees, motionBlendTreeParamIndex,
                    flatTransitions, flatConditions, behaviours,
                    out int nestedDefault, out int nestedAnyStart, out int nestedAnyCount);
                layerDefs[li] = new LayerDef
                {
                    NameHash = AnimationNameHash.Hash(layerName),
                    Weight = weight,
                    WeightParamIndex = weightParamIndex,
                    Blending = blending,
                    Mask = mask,
                    DefaultStateIndex = nestedDefault,
                    StatesStart = statesStart,
                    StatesCount = states.Count - statesStart,
                    AnyStateTransitionsStart = nestedAnyStart,
                    AnyStateTransitionsCount = nestedAnyCount,
                    SyncedLayerIndex = syncedLayerIndex,
                    SyncedLayerAffectsTiming = syncedTiming,
                    IKPass = ikPass,
                };
                continue;
            }

            CompileStateMachine(
                author, defaultName, layerStates, layerTransitions, layerAny,
                states, motionClips, motionKind, motionBlendTrees, motionBlendTreeParamIndex,
                flatTransitions, flatConditions, behaviours,
                out int defaultIdx, out int anyStart, out int anyCount);

            layerDefs[li] = new LayerDef
            {
                NameHash = AnimationNameHash.Hash(layerName),
                Weight = weight,
                Blending = blending,
                Mask = mask,
                DefaultStateIndex = defaultIdx,
                StatesStart = statesStart,
                StatesCount = states.Count - statesStart,
                AnyStateTransitionsStart = anyStart,
                AnyStateTransitionsCount = anyCount,
                SyncedLayerIndex = syncedLayerIndex,
                SyncedLayerAffectsTiming = syncedTiming,
                IKPass = ikPass,
            };
        }

        c.States = states.ToArray();
        c.MotionClips = motionClips.ToArray();
        c.MotionKind = motionKind.ToArray();
        c.MotionBlendTrees = motionBlendTrees.ToArray();
        c.MotionBlendTreeParamIndex = motionBlendTreeParamIndex.ToArray();
        var paramY = new int[c.MotionBlendTrees.Length];
        for (int i = 0; i < paramY.Length; i++)
        {
            BlendTree? bt = c.MotionBlendTrees[i];
            paramY[i] = bt != null && !string.IsNullOrEmpty(bt.ParameterNameY)
                ? author.GetParameterIndex(bt.ParameterNameY)
                : -1;
        }
        c.MotionBlendTreeParamIndexY = paramY;
        c.Transitions = flatTransitions.ToArray();
        c.Conditions = flatConditions.ToArray();
        c.Layers = layerDefs;
        c.BehaviourTypeNames = behaviours.Types.ToArray();
        c.BehaviourTypeIndices = behaviours.Slots.ToArray();
        c.DefaultStateIndex = layerDefs[0].DefaultStateIndex;
        c.AnyStateTransitionsStart = layerDefs[0].AnyStateTransitionsStart;
        c.AnyStateTransitionsCount = layerDefs[0].AnyStateTransitionsCount;
        return c;
    }

    private static void CompileStateMachine(
        AnimatorController author,
        string defaultStateName,
        List<AnimatorController.State> authorStates,
        List<AnimatorController.Transition> authorTransitions,
        List<AnimatorController.Transition> anyStateTransitions,
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
        var stateIndex = new Dictionary<string, int>(authorStates.Count);
        for (int i = 0; i < authorStates.Count; i++)
        {
            var s = authorStates[i];
            int globalIndex = states.Count;
            stateIndex[s.Name] = globalIndex;
            states.Add(MakeStateDef(author, s, globalIndex));
            behaviours.Bind(states, globalIndex, s);

            BlendTree? bt = s.BlendTree?.Res;
            if (bt != null)
            {
                motionKind.Add(1); // BlendTree.
                motionClips.Add(null);
                motionBlendTrees.Add(bt);
                motionBlendTreeParamIndex.Add(string.IsNullOrEmpty(bt.ParameterName)
                    ? -1
                    : author.GetParameterIndex(bt.ParameterName));
            }
            else
            {
                motionKind.Add(0); // clip.
                motionClips.Add(ResolveStateClip(s));
                motionBlendTrees.Add(null);
                motionBlendTreeParamIndex.Add(-1);
            }
        }

        var transByName = new Dictionary<string, AnimatorController.Transition>(authorTransitions.Count);
        for (int i = 0; i < authorTransitions.Count; i++)
        {
            var t = authorTransitions[i];
            if (!string.IsNullOrEmpty(t.Name))
                transByName[t.Name] = t;
        }

        int machineStart = states.Count - authorStates.Count;
        for (int i = 0; i < authorStates.Count; i++)
        {
            var s = authorStates[i];
            int start = flatTransitions.Count;
            AppendTransitions(author, s.TransitionNames, transByName, stateIndex, defaultStateName, machineStart,
                flatTransitions, flatConditions);
            StateDef def = states[machineStart + i];
            def.TransitionsStart = start;
            def.TransitionsCount = flatTransitions.Count - start;
            states[machineStart + i] = def;
        }

        anyStateTransitionsStart = flatTransitions.Count;
        AppendTransitionsDirect(anyStateTransitions, author, stateIndex, defaultStateName, machineStart,
            flatTransitions, flatConditions);
        anyStateTransitionsCount = flatTransitions.Count - anyStateTransitionsStart;

        defaultStateIndex = stateIndex.TryGetValue(defaultStateName, out int dsi) ? dsi : machineStart;
    }

    private static void AppendTransitions(
        AnimatorController author,
        List<string> transitionNames,
        Dictionary<string, AnimatorController.Transition> transByName,
        Dictionary<string, int> stateIndex,
        string defaultStateName,
        int fallbackDefaultIndex,
        List<TransitionDef> outTransitions,
        List<ConditionDef> outConditions)
    {
        for (int i = 0; i < transitionNames.Count; i++)
        {
            if (!transByName.TryGetValue(transitionNames[i], out var t))
                continue;
            AppendOne(t, author, stateIndex, defaultStateName, fallbackDefaultIndex, outTransitions, outConditions);
        }
    }

    private static void AppendTransitionsDirect(
        List<AnimatorController.Transition> transitions,
        AnimatorController author,
        Dictionary<string, int> stateIndex,
        string defaultStateName,
        int fallbackDefaultIndex,
        List<TransitionDef> outTransitions,
        List<ConditionDef> outConditions)
    {
        for (int i = 0; i < transitions.Count; i++)
            AppendOne(transitions[i], author, stateIndex, defaultStateName, fallbackDefaultIndex,
                outTransitions, outConditions);
    }

    private static void AppendOne(
        AnimatorController.Transition t,
        AnimatorController author,
        Dictionary<string, int> stateIndex,
        string defaultStateName,
        int fallbackDefaultIndex,
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

        int dest;
        if (t.DestinationStateName == AnimatorController.ExitStateName)
            dest = stateIndex.TryGetValue(defaultStateName, out int ex) ? ex : fallbackDefaultIndex;
        else
            dest = stateIndex.TryGetValue(t.DestinationStateName, out int di) ? di : -1;

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

    internal static StateDef MakeStateDef(AnimatorController author, AnimatorController.State s, int motionIndex)
    {
        var def = new StateDef
        {
            NameHash = AnimationNameHash.Hash(s.Name),
            TagHash = string.IsNullOrEmpty(s.Tag) ? 0 : AnimationNameHash.Hash(s.Tag),
            Speed = s.Speed,
            MotionIndex = motionIndex,
            IsLooping = s.IsLooping,
            WriteDefaults = s.WriteDefaults,
            Mirror = s.Mirror,
            CycleOffset = s.CycleOffset,
            IKOnFeet = s.IKOnFeet,
            SpeedMultiplierParameterIndex = ParamIndex(author, s.SpeedMultiplierParameter),
            MirrorParameterIndex = ParamIndex(author, s.MirrorParameter),
            CycleOffsetParameterIndex = ParamIndex(author, s.CycleOffsetParameter),
            TimeParameterIndex = ParamIndex(author, s.TimeParameter),
        };

        // M61 — parameter curves resolved into parallel index/value arrays.
        if (s.Curves.Count > 0)
        {
            var indices = new List<int>(s.Curves.Count);
            var values = new List<AnimationCurve>(s.Curves.Count);
            for (int i = 0; i < s.Curves.Count; i++)
            {
                AnimatorController.StateCurve sc = s.Curves[i];
                int idx = ParamIndex(author, sc.ParameterName);
                if (idx < 0 || sc.Curve == null)
                    continue;
                indices.Add(idx);
                values.Add(sc.Curve);
            }
            def.CurveParameterIndices = indices.ToArray();
            def.CurveParameterValues = values.ToArray();
        }
        return def;
    }

    internal static AnimationClip? ResolveStateClip(AnimatorController.State s)
    {
        AnimationClip? clip = s.Motion.Res;
        if (clip != null) return clip;
        var extra = s.Motions;
        return extra.Count > 0 ? extra[0].Res : null;
    }

    private static int ParamIndex(AnimatorController author, string name)
        => string.IsNullOrEmpty(name) ? -1 : author.GetParameterIndex(name);

    /// <summary>Dedupes behaviour type names into a flat index pool while compiling states.</summary>
    private sealed class BehaviourBag
    {
        public readonly List<string> Types = new();
        public readonly Dictionary<string, int> Index = new(StringComparer.Ordinal);
        public readonly List<int> Slots = new();

        public void Bind(List<StateDef> states, int globalIndex, AnimatorController.State s)
        {
            int start = Slots.Count;
            List<string> list = s.Behaviours;
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (!Index.TryGetValue(name, out int ti))
                {
                    ti = Types.Count;
                    Types.Add(name);
                    Index[name] = ti;
                }
                Slots.Add(ti);
            }
            StateDef def = states[globalIndex];
            def.BehavioursStart = start;
            def.BehavioursCount = Slots.Count - start;
            states[globalIndex] = def;
        }
    }
}
