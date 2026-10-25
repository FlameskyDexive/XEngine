namespace XEngine.Ai.Protocol;

/// <summary>★ M31 — Structured read of an AnimatorController (.controller).</summary>
public sealed record AnimatorInspectResult(
    string TargetAsset,
    string Name,
    string DefaultStateName,
    IReadOnlyList<AnimatorLayerInfo> Layers,
    IReadOnlyList<AnimatorStateInfo> States,
    IReadOnlyList<AnimatorTransitionInfo> Transitions,
    IReadOnlyList<AnimatorTransitionInfo> AnyStateTransitions,
    IReadOnlyList<AnimatorParameterInfo> Parameters,
    IReadOnlyList<BlendTreeSummary> BlendTrees,
    IReadOnlyList<AnimatorStateMachineInfo> StateMachines,
    string ContentHash);

public sealed record AnimatorLayerInfo(
    string Name,
    float Weight,
    string Blending,
    string DefaultStateName,
    int StateCount,
    AnimatorStateMachineInfo? Root = null,
    int SyncedLayerIndex = -1,
    bool SyncedLayerAffectsTiming = false,
    bool IKPass = false);

public sealed record AnimatorStateInfo(
    string Name,
    string Tag,
    float Speed,
    bool IsLooping,
    string? MotionGuid,
    string? BlendTreeParameter,
    IReadOnlyList<string> TransitionNames,
    float PosX,
    float PosY,
    bool WriteDefaults = true,
    bool Mirror = false,
    float CycleOffset = 0f,
    bool IKOnFeet = false,
    string SpeedMultiplierParameter = "",
    string MirrorParameter = "",
    string CycleOffsetParameter = "",
    string TimeParameter = "",
    IReadOnlyList<string>? Behaviours = null);

public sealed record AnimatorTransitionInfo(
    string Name,
    string DestinationStateName,
    float Duration,
    float Offset,
    bool HasExitTime,
    float ExitTime,
    IReadOnlyList<AnimatorConditionInfo> Conditions,
    string SourceStateMachineName = "",
    bool Solo = false,
    bool Mute = false,
    bool HasFixedDuration = true,
    string InterruptionSource = "None",
    bool OrderedInterruption = true,
    bool CanTransitionToSelf = false);

public sealed record AnimatorConditionInfo(string Mode, string ParamName, float Threshold);

public sealed record AnimatorParameterInfo(string Name, string Type, float DefaultFloat, int DefaultInt, bool DefaultBool);

public sealed record BlendTreeSummary(
    string OwnerState,
    string Type,
    string ParameterName,
    IReadOnlyList<BlendTreeChildInfo> Children);

public sealed record BlendTreeChildInfo(
    string? ClipGuid, float Threshold, float X, float Y,
    string? TreeGuid = null, string DirectParameterName = "");

public sealed record AnimatorStateMachineInfo(
    string Name,
    string DefaultStateName,
    float PosX,
    float PosY,
    IReadOnlyList<AnimatorStateInfo> States,
    IReadOnlyList<AnimatorStateMachineInfo> StateMachines,
    IReadOnlyList<AnimatorTransitionInfo> Transitions,
    IReadOnlyList<AnimatorTransitionInfo> AnyStateTransitions,
    IReadOnlyList<AnimatorTransitionInfo> EntryTransitions,
    IReadOnlyList<AnimatorTransitionInfo> StateMachineTransitions);

/// <summary>★ M31 — Structured read of an AnimationClip (.anim).</summary>
public sealed record ClipInspectResult(
    string TargetAsset,
    string Name,
    float Duration,
    float TicksPerSecond,
    string Wrap,
    IReadOnlyList<ClipBindingInfo> Bindings,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> AvailableClipGuids,
    string ContentHash);

/// <summary>★ Gen-P4.2 — How many humanoid bones were renamed, plus tracks that did not resolve.</summary>
public sealed record ClipRetargetStats(int Remapped, IReadOnlyList<string> Unmapped);

public sealed record ClipBindingInfo(
    string Path,
    string Channel,
    int KeyCount,
    float MinValue,
    float MaxValue,
    string TypeName = "",
    string Property = "");

/// <summary>★ M31 — Structured read of a BlendTree (.motion or embedded on a state).</summary>
public sealed record BlendTreeInspectResult(
    string TargetAsset,
    string? OwnerState,
    string Type,
    string ParameterName,
    IReadOnlyList<BlendTreeChildInfo> Children,
    string ContentHash,
    string ParameterNameY = "",
    bool UseAutomaticThresholds = false);

/// <summary>★ M37 — Structured diagnostics for an AnimatorController.</summary>
public sealed record AnimatorValidateResult(
    string TargetAsset,
    IReadOnlyList<AnimatorDiagnostic> Diagnostics,
    string ContentHash);

public sealed record AnimatorDiagnostic(
    string Code,
    string Severity,
    string Message,
    string? State = null,
    string? Transition = null,
    string? Layer = null,
    string? Parameter = null);

/// <summary>★ M44 — Structured read of an AnimatorOverrideController (.overridecontroller).</summary>
public sealed record OverrideControllerInspectResult(
    string TargetAsset,
    string Name,
    string? BaseGuid,
    IReadOnlyList<ClipOverrideInfo> Overrides,
    string ContentHash);

public sealed record ClipOverrideInfo(string? FromGuid, string? ToGuid);

/// <summary>★ M44 — Structured read of an AvatarMask (.mask).</summary>
public sealed record AvatarMaskInspectResult(
    string TargetAsset,
    string Name,
    IReadOnlyList<AvatarMaskEntryInfo> Entries,
    IReadOnlyList<AvatarMaskHumanoidPartInfo> HumanoidParts,
    string ContentHash);

public sealed record AvatarMaskEntryInfo(string Path, float Weight);

public sealed record AvatarMaskHumanoidPartInfo(string Name, bool Included);
