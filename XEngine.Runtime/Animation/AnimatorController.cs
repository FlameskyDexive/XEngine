// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Animation;

/// <summary>
/// The human-readable, Echo-serialized author form of an animation state machine — what the
/// Animator Window (M5) reads and writes, and what lives on disk under <c>Assets/*.controller</c>.
/// The <see cref="AnimatorControllerCompiler"/> flattens this into the runtime
/// <see cref="AnimatorControllerConstant"/> blob.
/// </summary>
/// <remarks>
/// Names are stored as strings here (diff-friendly, editor-friendly); the compiler hashes them.
/// Transitions live in one flat list and are referenced from states by name, so reordering states
/// doesn't invalidate transition identity.
/// </remarks>
[CreateAssetMenu("Animation/Animator Controller", Extension = ".controller", Order = 90)]
public class AnimatorController : EngineObject
{
    /// <summary>
    /// Sentinel destination for a transition into the visual Exit node. In a nested machine the
    /// compiler rewrites Exit to the parent entry (or a <see cref="StateMachine.StateMachineTransitions"/>
    /// outgoing). At the layer root it falls back to that machine's default state.
    /// </summary>
    public const string ExitStateName = "__exit__";

    /// <summary>Sentinel destination for a transition into the visual Entry node of the current machine.</summary>
    public const string EntryStateName = "__entry__";

    /// <summary>The state entered when the controller starts.</summary>
    public string DefaultStateName = string.Empty;

    /// <summary>All parameters.</summary>
    public List<Parameter> Parameters = new();

    /// <summary>All states.</summary>
    public List<State> States = new();

    /// <summary>All transitions, referenced from states via <see cref="State.TransitionNames"/>.</summary>
    public List<Transition> Transitions = new();

    /// <summary>Transitions that can fire from any state.</summary>
    public List<Transition> AnyStateTransitions = new();

    /// <summary>
    /// State machines blended by <see cref="AnimationLayerMixerPlayable"/>. Empty means the root
    /// <see cref="States"/> / <see cref="Transitions"/> / <see cref="AnyStateTransitions"/> /
    /// <see cref="DefaultStateName"/> are the Base Layer (legacy single-layer assets).
    /// </summary>
    public List<Layer> Layers = new();

    /// <summary>Linear scan for a parameter by name. -1 if absent.</summary>
    public int GetParameterIndex(string name)
    {
        var p = Parameters;
        int n = p.Count;
        for (int i = 0; i < n; i++)
            if (p[i].Name == name)
                return i;
        return -1;
    }

    /// <summary>
    /// Every <see cref="AnimationClip"/> reference this controller can reach — one per clip state, plus
    /// every child clip of each BlendTree state. Used for asset-bundle dependency extraction and for
    /// pre-loading (streaming) clips before the controller plays them, so playback doesn't stall.
    /// </summary>
    public virtual List<AssetRef<AnimationClip>> GetClipReferences()
    {
        var refs = new List<AssetRef<AnimationClip>>();
        var layers = Layers;
        if (layers.Count > 0)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                Layer layer = layers[i];
                if (layer.Root != null)
                    CollectClipReferences(layer.Root, refs);
                else
                    CollectClipReferences(layer.States, refs);
            }
        }
        else
        {
            CollectClipReferences(States, refs);
        }
        return refs;
    }

    private static void CollectClipReferences(StateMachine sm, List<AssetRef<AnimationClip>> refs)
    {
        CollectClipReferences(sm.States, refs);
        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
            CollectClipReferences(children[i], refs);
    }

    private static void CollectClipReferences(List<State> states, List<AssetRef<AnimationClip>> refs)
    {
        for (int i = 0; i < states.Count; i++)
        {
            State s = states[i];
            BlendTree? bt = s.BlendTree?.Res;
            if (bt != null)
            {
                CollectClipReferences(bt, refs);
            }
            else
            {
                refs.Add(s.Motion);
                var extra = s.Motions;
                for (int m = 0; m < extra.Count; m++)
                    refs.Add(extra[m]);
            }
        }
    }

    private static void CollectClipReferences(BlendTree bt, List<AssetRef<AnimationClip>> refs)
    {
        var children = bt.Children;
        for (int c = 0; c < children.Count; c++)
        {
            refs.Add(children[c].Clip);
            BlendTree? nested = children[c].Tree.Res;
            if (nested != null)
                CollectClipReferences(nested, refs);
        }
    }

    /// <summary>One mixer input: a state machine, blend mode, weight, and optional AvatarMask.</summary>
    public class Layer
    {
        public string Name = "Base Layer";
        public float Weight = 1f;
        public AnimatorLayerBlendingMode Blending = AnimatorLayerBlendingMode.Override;
        public AssetRef<AvatarMask> Mask;
        /// <summary>M51 — controller parameter whose value drives this layer's mixer weight each frame (empty = fixed <see cref="Weight"/>).</summary>
        public string WeightParameterName = "";
        /// <summary>-1 = not synced. Otherwise this layer follows <see cref="AnimatorController.Layers"/>[index].</summary>
        public int SyncedLayerIndex = -1;
        /// <summary>When synced, also copy the source layer's normalized time.</summary>
        public bool SyncedLayerAffectsTiming;
        /// <summary>Reserved IK pass flag (solver is v2).</summary>
        public bool IKPass;
        public string DefaultStateName = string.Empty;
        public List<State> States = new();
        public List<Transition> Transitions = new();
        public List<Transition> AnyStateTransitions = new();
        /// <summary>Nested graph. When set, the compiler and editor walk this tree instead of the flat lists.</summary>
        public StateMachine? Root;
    }

    /// <summary>
    /// One graph of states, with optional nested child machines. Each machine has its own Entry / Exit
    /// (Unity 5 semantics). The compiler flattens the tree into the runtime state/transition arrays.
    /// </summary>
    public class StateMachine
    {
        public string Name = string.Empty;
        public float PosX, PosY;
        public string DefaultStateName = string.Empty;
        public List<State> States = new();
        public List<StateMachine> StateMachines = new();
        public List<Transition> Transitions = new();
        public List<Transition> AnyStateTransitions = new();
        /// <summary>Conditional branches from the Entry node. If none match, the machine uses <see cref="DefaultStateName"/>.</summary>
        public List<Transition> EntryTransitions = new();
        /// <summary>
        /// Outgoing transitions that fire when a named child machine hits Exit.
        /// <see cref="Transition.SourceStateMachineName"/> identifies the child.
        /// </summary>
        public List<Transition> StateMachineTransitions = new();
    }

    /// <summary>A named, typed controller parameter with a default value.</summary>
    public class Parameter
    {
        public string Name = string.Empty;
        public AnimatorParameterType Type = AnimatorParameterType.Float;
        public float DefaultFloat;
        public int DefaultInt;
        public bool DefaultBool;
    }

    /// <summary>A state node: a motion plus the transitions leaving it.</summary>
    public class State
    {
        public string Name = string.Empty;
        public string Tag = string.Empty;
        public float Speed = 1f;
        public bool IsLooping = true;
        /// <summary>Reset properties that this state's motion does not cover (Unity Write Defaults).</summary>
        public bool WriteDefaults = true;
        /// <summary>Sample through Left/Right bone-name mirrors when a match exists.</summary>
        public bool Mirror;
        /// <summary>Normalized start time added on enter (wrapped to [0,1)).</summary>
        public float CycleOffset;
        /// <summary>Reserved foot-IK flag (solver is v2).</summary>
        public bool IKOnFeet;
        public string SpeedMultiplierParameter = string.Empty;
        public string MirrorParameter = string.Empty;
        public string CycleOffsetParameter = string.Empty;
        public string TimeParameter = string.Empty;
        /// <summary>The clip this state plays when <see cref="BlendTree"/> is null.</summary>
        public AssetRef<AnimationClip> Motion;
        /// <summary>Optional motion set for synced layers; compiler uses <see cref="Motion"/> or Motions[0].</summary>
        public List<AssetRef<AnimationClip>> Motions = new();

        /// <summary>
        /// Optional BlendTree motion. When set, the state plays a parameter-driven blend of child
        /// clips instead of <see cref="Motion"/>.
        /// </summary>
        public AssetRef<BlendTree>? BlendTree;
        /// <summary>Names of transitions (in <see cref="AnimatorController.Transitions"/>) leaving this state.</summary>
        public List<string> TransitionNames = new();
        /// <summary>Editor-canvas position (serialized for the Animator Window, M5).</summary>
        public float PosX, PosY;
        /// <summary>Assembly-qualified or full type names of <see cref="StateMachineBehaviour"/> scripts on this state.</summary>
        public List<string> Behaviours = new();
        /// <summary>
        /// M61 — parameter curves: while this state plays, each listed parameter is driven by its
        /// curve sampled at the state's normalized time (Unity state parameter curves).
        /// </summary>
        public List<StateCurve> Curves = new();
    }

    /// <summary>M61 — one (parameter name → curve) binding driving the parameter during a state.</summary>
    public class StateCurve
    {
        public string ParameterName = string.Empty;
        public AnimationCurve Curve = new();
    }

    /// <summary>A directed transition between two states, gated by optional conditions and/or an exit time.</summary>
    public class Transition
    {
        /// <summary>Unique id within the controller (so states can reference this transition by name).</summary>
        public string Name = string.Empty;
        public string DestinationStateName = string.Empty;
        /// <summary>
        /// When non-empty, this transition lives on the parent machine and fires when the named child
        /// state machine reaches Exit (Unity's stateMachineTransitions map).
        /// </summary>
        public string SourceStateMachineName = string.Empty;
        public float Duration = 0.1f;
        public float Offset;
        public bool HasExitTime;
        public float ExitTime = 0.9f;
        /// <summary>When any non-muted Solo transition exists in a slice, only Solo transitions are considered.</summary>
        public bool Solo;
        /// <summary>Muted transitions are skipped by the runtime (Unity Mute).</summary>
        public bool Mute;
        /// <summary>True (default) when <see cref="Duration"/> is seconds; false = fraction of the source clip length.</summary>
        public bool HasFixedDuration = true;
        /// <summary>Which state's outgoing transitions may interrupt this one while it is fading.</summary>
        public TransitionInterruptionSource InterruptionSource;
        /// <summary>When true, only earlier transitions in the same slice may interrupt.</summary>
        public bool OrderedInterruption = true;
        /// <summary>AnyState only: allow a transition whose destination is the current state.</summary>
        public bool CanTransitionToSelf;
        public List<Condition> Conditions = new();
    }

    /// <summary>One comparison against a parameter; all conditions on a transition must pass.</summary>
    public class Condition
    {
        public ConditionMode Mode = ConditionMode.If;
        public string ParamName = string.Empty;
        public float Threshold;
    }
}
