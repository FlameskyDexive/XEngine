// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Animation;

/// <summary>Parameter type stored in an <see cref="AnimatorControllerConstant"/>.</summary>
public enum AnimatorParameterType : byte
{
    Float = 0,
    Int = 1,
    Bool = 2,
    Trigger = 3,
}

/// <summary>
/// How a <see cref="ConditionDef"/> compares a parameter's value to its threshold. Mirrors Unity
/// Mecanim's transition condition modes (ExitTime is modelled separately on <see cref="TransitionDef"/>
/// rather than as a condition).
/// </summary>
public enum ConditionMode : byte
{
    /// <summary>Bool/Trigger is true (or int != 0).</summary>
    If = 0,
    /// <summary>Bool/Trigger is false (or int == 0).</summary>
    IfNot = 1,
    /// <summary>Float &gt; threshold.</summary>
    Greater = 2,
    /// <summary>Float &lt; threshold.</summary>
    Less = 3,
    /// <summary>Int == threshold.</summary>
    Equals = 4,
    /// <summary>Int != threshold.</summary>
    NotEqual = 5,
}

/// <summary>Who may interrupt an in-progress transition (Unity 5 <c>TransitionInterruptionSource</c>).</summary>
public enum TransitionInterruptionSource : byte
{
    None = 0,
    CurrentState = 1,
    NextState = 2,
    CurrentStateThenNextState = 3,
    NextStateThenCurrentState = 4,
}

/// <summary>A named, typed parameter slot on the controller.</summary>
public struct ParameterDef
{
    /// <summary>FNV-1a hash of the parameter name (see <see cref="AnimationNameHash"/>).</summary>
    public int NameHash;
    public AnimatorParameterType Type;
    public float DefaultFloat;
    public int DefaultInt;
    public bool DefaultBool;
}

/// <summary>One comparison in a transition's condition list.</summary>
public struct ConditionDef
{
    public ConditionMode Mode;
    public int ParamIndex;
    public float Threshold;
}

/// <summary>
/// A directed transition from one state to another. Transitions are stored flat in
/// <see cref="AnimatorControllerConstant.Transitions"/> and referenced by each state via a
/// [Start, Start+Count) range, mirroring Unity Mecanim's compiled-blob layout.
/// </summary>
public struct TransitionDef
{
    /// <summary>Index into <see cref="AnimatorControllerConstant.States"/>.</summary>
    public int DestinationStateIndex;
    /// <summary>Crossfade duration in seconds.</summary>
    public float Duration;
    /// <summary>Destination state's start position as a normalized time in [0,1].</summary>
    public float Offset;
    /// <summary>True when the transition should wait for <see cref="ExitTime"/> before firing.</summary>
    public bool HasExitTime;
    /// <summary>Normalized time in [0,1] the current state must reach before this transition can fire.</summary>
    public float ExitTime;
    /// <summary>When any non-muted Solo transition exists in a state's slice, only Solo ones may fire.</summary>
    public bool Solo;
    /// <summary>Muted transitions are never considered.</summary>
    public bool Mute;
    /// <summary>True when <see cref="Duration"/> is seconds; false when it is a fraction of the source clip.</summary>
    public bool HasFixedDuration;
    /// <summary>Which state's outgoing transitions may interrupt this one while it is fading.</summary>
    public TransitionInterruptionSource InterruptionSource;
    /// <summary>When true, only earlier (higher-priority) transitions in the same slice may interrupt.</summary>
    public bool OrderedInterruption;
    /// <summary>AnyState only: allow a transition whose destination is the current state.</summary>
    public bool CanTransitionToSelf;
    /// <summary>Start index into <see cref="AnimatorControllerConstant.Conditions"/>.</summary>
    public int ConditionsStart;
    /// <summary>Number of conditions; all must pass for the transition to fire.</summary>
    public int ConditionsCount;
}

/// <summary>
/// One compiled mixer layer: a slice of <see cref="AnimatorControllerConstant.States"/> plus
/// blend mode, weight, and optional mask. Layer 0 is the Override base.
/// </summary>
public struct LayerDef
{
    /// <summary>FNV-1a hash of the layer name.</summary>
    public int NameHash;
    /// <summary>Mixer weight in [0,1].</summary>
    public float Weight;
    /// <summary>M51 — parameter index driving the mixer weight (-1 = fixed <see cref="Weight"/>).</summary>
    public int WeightParamIndex;
    public AnimatorLayerBlendingMode Blending;
    /// <summary>Resolved AvatarMask, or null for the full body.</summary>
    public AvatarMask? Mask;
    /// <summary>Index into <see cref="AnimatorControllerConstant.States"/> for this layer's default state.</summary>
    public int DefaultStateIndex;
    /// <summary>Start index into <see cref="AnimatorControllerConstant.States"/>.</summary>
    public int StatesStart;
    /// <summary>Number of states in this layer.</summary>
    public int StatesCount;
    /// <summary>Start index into <see cref="AnimatorControllerConstant.Transitions"/> for AnyState.</summary>
    public int AnyStateTransitionsStart;
    /// <summary>Number of AnyState transitions for this layer.</summary>
    public int AnyStateTransitionsCount;
    /// <summary>-1 = independent FSM. Otherwise follow that layer's current local state.</summary>
    public int SyncedLayerIndex;
    public bool SyncedLayerAffectsTiming;
    public bool IKPass;
}

/// <summary>A node in the state machine: a motion plus its outbound transitions.</summary>
public struct StateDef
{
    /// <summary>FNV-1a hash of the state name.</summary>
    public int NameHash;
    /// <summary>FNV-1a hash of the state's tag (0 = no tag).</summary>
    public int TagHash;
    /// <summary>Playback speed multiplier.</summary>
    public float Speed;
    /// <summary>Index into <see cref="AnimatorControllerConstant.MotionClips"/> (-1 = empty state).</summary>
    public int MotionIndex;
    /// <summary>Whether the state's motion wraps when it reaches its duration.</summary>
    public bool IsLooping;
    /// <summary>Start index into <see cref="AnimatorControllerConstant.Transitions"/>.</summary>
    public int TransitionsStart;
    /// <summary>Number of outbound transitions.</summary>
    public int TransitionsCount;
    public bool WriteDefaults;
    public bool Mirror;
    public float CycleOffset;
    public bool IKOnFeet;
    /// <summary>Parameter table index, or -1.</summary>
    public int SpeedMultiplierParameterIndex;
    public int MirrorParameterIndex;
    public int CycleOffsetParameterIndex;
    public int TimeParameterIndex;
    /// <summary>Start index into <see cref="AnimatorControllerConstant.BehaviourTypeIndices"/>.</summary>
    public int BehavioursStart;
    /// <summary>Number of behaviours on this state.</summary>
    public int BehavioursCount;
    /// <summary>M61 — parameter indices driven by <see cref="CurveParameterValues"/> while in this state.</summary>
    public int[] CurveParameterIndices = System.Array.Empty<int>();
    /// <summary>M61 — the curves driving those parameters (parallel to <see cref="CurveParameterIndices"/>).</summary>
    public AnimationCurve[] CurveParameterValues = System.Array.Empty<AnimationCurve>();

    public StateDef()
    {
        SpeedMultiplierParameterIndex = -1;
        MirrorParameterIndex = -1;
        CycleOffsetParameterIndex = -1;
        TimeParameterIndex = -1;
    }
}

/// <summary>
/// The compiled, read-only, flat-arrayed form of an AnimatorController — XEngine's analogue of
/// Unity Mecanim's <c>StateMachineConstant</c> blob. Built once (at import, M4) from the authored
/// graph and evaluated by <see cref="AnimatorControllerRuntime"/> each frame. No GameObject refs, no
/// strings — only integer indices and pre-hashed names, so the per-frame evaluation is cache-friendly
/// and free of dictionary lookups.
/// </summary>
public sealed class AnimatorControllerConstant
{
    /// <summary>All parameters, indexed by <see cref="ParameterDef.NameHash"/> via <see cref="GetParameterIndex"/>.</summary>
    public ParameterDef[] Parameters = System.Array.Empty<ParameterDef>();

    /// <summary>All states. <see cref="DefaultStateIndex"/> is the entry point.</summary>
    public StateDef[] States = System.Array.Empty<StateDef>();

    /// <summary>Flat transition pool; each state references its slice via TransitionsStart/Count.</summary>
    public TransitionDef[] Transitions = System.Array.Empty<TransitionDef>();

    /// <summary>Flat condition pool; each transition references its slice via ConditionsStart/Count.</summary>
    public ConditionDef[] Conditions = System.Array.Empty<ConditionDef>();

    /// <summary>
    /// Motion table — one clip per state for clip states. Indexed parallel to <see cref="States"/>.
    /// Null for BlendTree states (use <see cref="MotionBlendTrees"/>).
    /// </summary>
    public AnimationClip?[] MotionClips = System.Array.Empty<AnimationClip?>();

    /// <summary>Per-state motion kind: 0 = clip, 1 = BlendTree.</summary>
    public byte[] MotionKind = System.Array.Empty<byte>();

    /// <summary>Per-state BlendTree motion (null for clip states). Indexed parallel to <see cref="States"/>.</summary>
    public BlendTree?[] MotionBlendTrees = System.Array.Empty<BlendTree?>();

    /// <summary>Per-state resolved parameter index for the BlendTree's blend parameter (-1 if clip/unresolved).</summary>
    public int[] MotionBlendTreeParamIndex = System.Array.Empty<int>();

    /// <summary>★ M43 — Per-state resolved Y parameter index for 2D BlendTrees (-1 if unused).</summary>
    public int[] MotionBlendTreeParamIndexY = System.Array.Empty<int>();

    /// <summary>The state entered when the controller starts (or re-enters from AnyState-less idle).</summary>
    public int DefaultStateIndex;

    /// <summary>Start index into <see cref="Transitions"/> for AnyState transitions.</summary>
    public int AnyStateTransitionsStart;
    /// <summary>Number of AnyState transitions.</summary>
    public int AnyStateTransitionsCount;

    /// <summary>
    /// Compiled mixer layers. A single-layer (legacy) controller has one entry covering the full
    /// <see cref="States"/> array. Empty means treat the whole constant as layer 0 (hand-built tests).
    /// </summary>
    public LayerDef[] Layers = System.Array.Empty<LayerDef>();

    /// <summary>Unique behaviour type names (assembly-qualified or full name).</summary>
    public string[] BehaviourTypeNames = System.Array.Empty<string>();
    /// <summary>Flat pool of indices into <see cref="BehaviourTypeNames"/>; each state owns a slice.</summary>
    public int[] BehaviourTypeIndices = System.Array.Empty<int>();

    /// <summary>Linear scan for the parameter index whose name hashes to <paramref name="nameHash"/>. -1 if absent.</summary>
    public int GetParameterIndex(int nameHash)
    {
        ParameterDef[] p = Parameters;
        int n = p.Length;
        for (int i = 0; i < n; i++)
            if (p[i].NameHash == nameHash)
                return i;
        return -1;
    }
}
