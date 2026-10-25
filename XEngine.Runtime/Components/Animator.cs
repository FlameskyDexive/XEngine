// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Animation.Humanoid;
using XEngine.Animation.Transitions;
using XEngine.Playables;

namespace XEngine.Runtime;

/// <summary>
/// Drives skinned-mesh animation via a <see cref="PlayableGraph"/>. Code-driven API (M2): maintains a
/// weighted mixer "layer" of <see cref="AnimationClipPlayable"/> states keyed by clip, with
/// <see cref="Play(AnimationClip, float)"/> / <see cref="CrossFade"/> / <see cref="Stop"/> and
/// per-state normalized-time events. The FSM controller (M3) will layer on top of this same graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>State model (Animancer-style).</b> Each clip played gets one reusable
/// <see cref="AnimationClipPlayable"/> registered in a dictionary; calling <c>Play</c> on a clip
/// already seen re-uses its state. The layer is an <see cref="AnimationMixerPlayable"/> blending all
/// active states by weight; <see cref="Playable.StartFade"/> drives crossfades.
/// </para>
/// <para>
/// <b>Output.</b> <see cref="AnimationPlayableOutput"/> writes bone local TRS to Transforms and
/// blend-shape weights to <see cref="SkinnedMeshRenderer"/>s; the renderer's <c>Transform.Version</c>
/// dirty check re-skins automatically.
/// </para>
/// <para>
/// <b>Tick.</b> <see cref="LateUpdate"/> advances the graph by <see cref="Time.DeltaTime"/> so
/// gameplay <c>Update</c> writes happen before animation overrides them.
/// </para>
/// </remarks>
[AddComponentMenu("Animation/Animator")]
[ComponentIcon("\uf008")] // Film
public partial class Animator : MonoBehaviour
{
    /// <summary>Clip played on enable when <see cref="PlayAutomatically"/> is set (code-driven mode).</summary>
    public AssetRef<AnimationClip> Clip;

    /// <summary>
    /// The FSM controller asset (author form). When set, the controller is compiled to its runtime
    /// constant and drives state selection each frame. Takes precedence over <see cref="Clip"/>.
    /// </summary>
    public AssetRef<AnimatorController> Controller;

    /// <summary>
    /// The authored state machine for this Animator. When <see cref="Controller"/> is an
    /// <see cref="AnimatorOverrideController"/>, this is the override's <see cref="AnimatorOverrideController.Base"/>.
    /// </summary>
    public AnimatorController? AuthoringController
    {
        get
        {
            AnimatorController? c = Controller.Res;
            if (c is AnimatorOverrideController ov && ov.Base.Res != null)
                return ov.Base.Res;
            return c;
        }
    }

    /// <summary>Auto-play <see cref="Clip"/> on enable.</summary>
    public bool PlayAutomatically = true;

    /// <summary>
    /// M50 — optional transition library consulted by <see cref="PlayWithLibrary(AnimationClip, string)"/>
    /// and <see cref="PlayWithLibrary(TransitionAsset, string)"/>: per (from → to) fade/start/speed
    /// overrides. Explicit play arguments always win.
    /// </summary>
    public AssetRef<TransitionLibraryAsset> TransitionLibrary;

    /// <summary>
    /// M53 — the humanoid rig description. When set and a played clip carries humanoid data (M54+),
    /// playback routes through muscle-space retargeting onto THIS avatar's rig; generic clips are
    /// unaffected.
    /// </summary>
    public AssetRef<Avatar> Avatar;

    /// <summary>Playback speed multiplier applied to new states.</summary>
    public float Speed = 1f;

    /// <summary>
    /// Optional maximum live animation step, in seconds; zero or negative means unlimited.
    /// LateUpdate advances the controller and graph by the same capped duration, so a delayed
    /// frame cannot consume a large root-motion interval while gameplay moves only a short step.
    /// Explicit Inspector <see cref="EvaluateGraph"/> sampling keeps its caller-supplied interval.
    /// </summary>
    public float MaximumDeltaTime;

    /// <summary>Wrap mode applied to new states.</summary>
    public AnimationWrapMode Wrap = AnimationWrapMode.Loop;

    /// <summary>
    /// When true, weighted base-layer clips' root-motion increments (see <see cref="AnimationClip.RootBonePath"/>)
    /// are extracted each frame and applied to this GameObject's Transform. Imported skinned rigs can
    /// also use a ground trajectory named Root beside the skeleton carrier. Extracted translation is
    /// removed from the visual carrier; <see cref="RootMotionPlanar"/> and <see cref="ApplyRootMotionRotation"/>
    /// control which channels remain authored bone animation. When
    /// <see cref="OnAnimatorMove"/> is subscribed, the delta is passed to the handler instead (manual
    /// application, e.g. via a CharacterController).
    /// </summary>
    public bool ApplyRootMotion;

    /// <summary>
    /// Optional origin for skeleton paths. Configure while disabled. A separate visual origin
    /// lets model-axis correction live outside the animated bones and their retarget reference,
    /// while the Animator and gameplay controller remain on the same actor GameObject.
    /// </summary>
    public XEngine.Vector.Transform? SkeletonRoot;

    /// <summary>
    /// Optional manual root-motion handler receiving (positionDelta, rotationDelta) in body space. When
    /// set, the Animator does not auto-apply root motion; the handler is responsible for moving the body.
    /// </summary>
    [NonSerialized] public Action<XEngine.Vector.Float3, XEngine.Vector.Quaternion>? OnAnimatorMove;

    // Root-motion extraction state.
    private bool _rootBindCaptured;
    // True when this animator drives a skinned mesh — the only case the bind pin applies to.
    private bool _pinToBindPose;
    private XEngine.Vector.Float3 _rootBindPos;
    // Reused travel-projection buffers. Clip-local descriptors are built at bind time; the
    // per-frame path unions directions from every weighted base-layer state without allocating.
    private TravelPinAccumulator[] _travelPinAccumulators = Array.Empty<TravelPinAccumulator>();
    private int[] _travelPinBoneIndices = Array.Empty<int>();

    /// <summary>Whether the most-recently-played state is advancing.</summary>
    [NonSerialized] public bool IsPlaying;

    /// <summary>The most-recently-played clip, or <c>null</c>.</summary>
    [NonSerialized] public AnimationClip? CurrentClip;

    private PlayableGraph _graph = null!;
    private AnimationPlayableOutput _output = null!;
    private SkeletonBinding? _binding;
    // Stable destination reference pose retained independently from the live graph binding.
    // OnDisable leaves bones in their last evaluated pose, so rebuilding from live Transforms on
    // the next OnEnable would make that animated frame the new retargeting reference. Ordinary
    // graph rebuilds copy this snapshot into their fresh hierarchy binding instead.
    private SkeletonBinding? _referenceBinding;
    private AnimationMixerPlayable? _layer;            // base layer's clip-state mixer
    private AnimationLayerMixerPlayable? _layerMixer;  // graph root: blends base + overlay layers
    private readonly Dictionary<AnimationClip, AnimationClipPlayable> _states = new();

    // FadeMode.FromStart clones (Animancer weightless states): fading the SAME clip into itself
    // requires a second playable so the outgoing instance can keep playing while a fresh copy
    // fades in from time zero. Capped per clip; settled clones are reused.
    private const float WeightlessThreshold = 0.1f;
    private const int MaxCloneCount = 3;
    private readonly Dictionary<AnimationClip, List<AnimationClipPlayable>> _weightlessClones = new();

    // Transition (Animancer-style) states, cached by ITransition.Key or by the wrapping
    // TransitionAsset so repeated Play(transition) reuses one state per identity.
    private readonly Dictionary<object, AnimationPlayable> _transitionStates = new();

    // M48 — code-driven parameter store + per-mixer parameter bindings (hash-resolved). Lazy list;
    // rebuilt/cleared together with the graph because the mixers it points at die with the graph.
    private List<MixerParameterBinding>? _mixerBindings;

    private struct ParameterRef
    {
        public bool Has;   // Hashes are arbitrary ints (often negative), so presence is an explicit flag.
        public int Hash;
    }

    private struct MixerParameterBinding
    {
        public BlendTreePlayable Mixer;
        public ParameterRef X;
        public ParameterRef Y;
        public ParameterRef[]? Direct;
    }

    /// <summary>
    /// M48 — code-driven parameter store (Animancer parameter dictionary equivalent): floats with
    /// optional damped targets, keyed by <see cref="AnimationNameHash"/>. Mixer transitions bound
    /// here are pushed into their blend values every frame before the graph evaluates. The FSM
    /// controller keeps its own compiled parameter table and is unaffected.
    /// </summary>
    public readonly AnimatorParameterStore Parameters = new();

    /// <summary>
    /// M49 — named animation events (Animancer <c>Graph.Events</c> equivalent): handlers bound by
    /// name, fired whenever any queued event carrying that name flushes at the end of the frame.
    /// </summary>
    public readonly AnimatorNamedEvents Events = new();

    // Cached router handed to the shared event queue while this animator's graph evaluates.
    private Action<int>? _namedRouter;

    private AnimationClipPlayable? _current;
    private bool _pendingAutoPlay;
    // Controller asset assigned but still streaming at OnEnable — LateUpdate retries the bind
    // until it resolves (async loading returns a transient null from AssetRef.Res).
    private bool _pendingControllerBind;

    // FSM (asset-driven) mode. When set, the runtime decides what plays each frame; the code-driven
    // API above still works for ad-hoc overrides. M4 wires this from an AnimatorController asset.
    private AnimatorControllerRuntime? _runtime;
    private AnimatorControllerRuntime[]? _layerRuntimes;
    // M51 — the compiled controller's shared parameter slots (kept for layer-weight parameter driving).
    private float[]? _sharedParameters;
    private int _bindAttempts;

    /// <summary>The active FSM runtime, or <c>null</c> in pure code-driven mode.</summary>
    public AnimatorControllerRuntime? Runtime => _runtime;

    public override void OnEnable()
    {
        var onEnableSw = System.Diagnostics.Stopwatch.StartNew();
        RebuildGraph();
        long rebuildMs = onEnableSw.ElapsedMilliseconds;

        // FSM mode: compile + activate the controller asset if it's loaded and every motion it
        // references is resolvable (see <see cref="TryActivateController"/>). When clips are still
        // importing we deliberately stay unbound — LateUpdate retries until the FSM can be built
        // complete, because a half-resolved compile would silently play nothing forever.
        AnimatorController? controller = ResolveControllerNow();
        long resolveMs = onEnableSw.ElapsedMilliseconds - rebuildMs;
        if (controller != null)
        {
            if (TryActivateController(controller))
                PrefetchController(controller);
            if (onEnableSw.ElapsedMilliseconds > 200)
                Debug.LogWarning($"[PlayInit] Animator '{GameObject.Name}' OnEnable: rebuild={rebuildMs}ms " +
                    $"resolve={resolveMs}ms activate={onEnableSw.ElapsedMilliseconds - rebuildMs - resolveMs}ms total={onEnableSw.ElapsedMilliseconds}ms");
            return;
        }

        // Referenced but genuinely unresolvable right now: retry in LateUpdate. Without it the FSM
        // runtime is never built and every name-based Play/CrossFade silently no-ops.
        _pendingControllerBind = Controller.AssetID != System.Guid.Empty;
        if (_pendingControllerBind)
            return;

        if (!PlayAutomatically)
            return;

        AnimationClip? clip = Clip.Res;
        if (clip != null)
            Play(clip);
        else
            _pendingAutoPlay = true; // clip still streaming; defer to LateUpdate
    }

    public override void OnDisable()
    {
        UnsubscribeAssetPublication();
        // Drop un-flushed events rather than firing handlers into a torn-down graph.
        PlayableEventQueue.Shared.Clear();
        _graph?.Destroy();
        // Keep _referenceBinding: disabling destroys playback state, not the skeleton's authored
        // reference pose. The live Transforms deliberately remain at their last animated frame.
        _binding = null;
        _layer = null;
        _layerMixer = null;
        _states.Clear();
        _weightlessClones.Clear();
        _transitionStates.Clear();
        _mixerBindings?.Clear();
        _current = null;
        CurrentClip = null;
        IsPlaying = false;
        _runtime = null;
        _layerRuntimes = null;
        _pendingAutoPlay = false;
        _pendingControllerBind = false;
        _bindAttempts = 0;
        _output = null!;
    }

    public override void LateUpdate()
    {
        if (_pendingAutoPlay)
        {
            AnimationClip? clip = Clip.Res;
            if (clip != null)
            {
                _pendingAutoPlay = false;
                Play(clip);
            }
        }

        // Deferred FSM bind: OnEnable either saw no reference at all (deserialized later — e.g. the
        // component was enabled as part of a scene batch before its AssetRef block filled in) or a
        // transient streaming null. Retry whenever the FSM runtime still doesn't exist, not just
        // while an OnEnable-time flag survived. After the initial streaming window, back off
        // unresolved references, but still allow a later import/repair to become playable.
        if (_runtime == null && _layerRuntimes == null && Controller.AssetID != System.Guid.Empty
            && (_bindAttempts < 300 || _bindAttempts++ % 60 == 0))
        {
            AnimatorController? controller = ResolveControllerNow();
            if (controller != null && TryActivateController(controller))
            {
                PrefetchController(controller);
                _bindAttempts = 0;
                _pendingControllerBind = false;
            }
            else if (controller != null)
            {
                // Complete author present but clips still unresolvable: keep waiting for the
                // importer/streaming to finish, log once per second-ish so the cause is visible.
                _bindAttempts++;
                if (_bindAttempts % 60 == 0)
                {
                    List<AssetRef<AnimationClip>> refs = controller.GetClipReferences();
                    int missing = 0;
                    for (int i = 0; i < refs.Count; i++)
                        if (!IsMotionResolvable(refs[i]))
                            missing++;
                    Debug.LogWarning($"[Animator] '{GameObject.Name}' waiting for controller clips " +
                                     $"({controller.Name}) — {missing} unresolved.");
                }
            }
            else if (++_bindAttempts == 300)
            {
                Debug.LogWarning($"[Animator] '{GameObject.Name}' is still waiting for controller " +
                                 $"{Controller.AssetID}; reducing streaming retry frequency.");
            }
        }

        // Capture the root bone's bind local pos before this frame's animation overwrites it (used to
        // pin the bone and measure body-space delta). Captured once per clip.
        CaptureBindPoseIfNeeded();

        // The FSM (if any) decides what plays this frame before the graph advances, so any CrossFade
        // it issues applies in this same frame's graph evaluation.
        float deltaTime = Time.DeltaTime;
        if (MaximumDeltaTime > 0f)
            deltaTime = Math.Min(deltaTime, MaximumDeltaTime);

        // M48 — smoothed parameters converge and mixer blend inputs refresh before evaluation so a
        // parameter change this frame is visible in this frame's pose.
        Parameters.Tick(deltaTime);
        ApplyMixerParameters();

        EvaluateRuntimes(deltaTime);

        PrepareRootMotionPolicy();

        if (_graph.Root != null)
        {
            // Route this animator's named events while its graph owns the flush at evaluate end.
            _namedRouter ??= Events.Invoke;
            PlayableEventQueue.Shared.NamedRouter = _namedRouter;
            _graph.Evaluate(deltaTime);
            PlayableEventQueue.Shared.NamedRouter = null;
        }

        if (ApplyRootMotion)
            ExtractRootMotion();
        else
            PinRootBoneToBind();

        // M57 — the IK pass runs after pose application and root handling; the renderer's lazy
        // Transform.Version re-skinning picks the edited bones up at draw time.
        RunIKPass();

        // M61 — active MatchTargets blend the body across the current state's window.
        UpdateMatchTarget();

        FlushExitWatchers();
    }

    /// <summary>
    /// Advances the playable graph by <paramref name="deltaTime"/> without extracting root motion.
    /// Used by the editor Inspector preview so the isolated preview scene can tick independently
    /// of <see cref="Scene.Current"/>.
    /// </summary>
    /// <summary>
    /// Root-pin snapshot derived from the binding's immutable destination reference pose. Both the
    /// gameplay path (LateUpdate) and the isolated preview path (EvaluateGraph) call this. Reading
    /// <see cref="SkeletonBinding.ReferencePose"/> instead of live Transforms is essential after an
    /// OnDisable/OnEnable cycle, because those Transforms still contain the last animated frame.
    /// </summary>
    private void CaptureBindPoseIfNeeded()
    {
        if (_rootBindCaptured)
            return;
        SkeletonBinding? binding = _binding;
        if (binding == null)
            return;
        if (!TrySelectRootBindReference(binding))
            return;
        _rootBindCaptured = true;
        // The bind pin exists to protect SKINNED meshes: root-travel curves displace the
        // skeleton from the bind pose the mesh vertices are skinned in (shredding/strobing).
        // Rigs without a SkinnedMeshRenderer (transform-only animation: props, cameras, test
        // skeletons) have no bind pose to protect — pinning them would wrongly freeze
        // legitimate root translation curves, so the pin stays off.
        _pinToBindPose = GameObject != null
            && GameObject.GetComponentInChildren<SkinnedMeshRenderer>(includeSelf: true, includeInactive: true) != null;
    }

    private bool TrySelectRootBindReference(SkeletonBinding binding)
    {
        XEngine.Vector.Transform? rootBone = ResolveRootBone();
        if (rootBone == null)
            return false;
        int rootIndex = IndexOfBoundTransform(binding, rootBone);
        if (rootIndex < 0)
            return false;
        BonePose rootReference = binding.ReferencePose[rootIndex];
        _rootBindPos = rootReference.LocalPosition;
        return true;
    }

    private static int IndexOfBoundTransform(SkeletonBinding binding, XEngine.Vector.Transform target)
    {
        XEngine.Vector.Transform?[] bones = binding.Bones;
        for (int i = 0; i < bones.Length; i++)
            if (ReferenceEquals(bones[i], target))
                return i;
        return -1;
    }

    public void EvaluateGraph(float deltaTime)
    {
        if (_graph == null) return;
        CaptureBindPoseIfNeeded();
        Parameters.Tick(deltaTime);
        ApplyMixerParameters();
        EvaluateRuntimes(deltaTime);
        PrepareRootMotionPolicy();
        if (_graph.Root != null)
        {
            _namedRouter ??= Events.Invoke;
            PlayableEventQueue.Shared.NamedRouter = _namedRouter;
            _graph.Evaluate(deltaTime);
            PlayableEventQueue.Shared.NamedRouter = null;
        }
        // The isolated preview path (Inspector FBX/model preview) advances the graph WITHOUT
        // LateUpdate, so the in-place pin must run here too — otherwise clips with root travel
        // (Take 001's 3.68 m/cycle) displace the skeleton and shred the preview mesh.
        if (!ApplyRootMotion)
            PinRootBoneToBind();
        else
            ExtractRootMotion(applyToBody: false);
    }

    /// <summary>Resolves the root-motion bone from the current clip's <see cref="AnimationClip.RootBonePath"/>, or null.</summary>
    private XEngine.Vector.Transform? ResolveRootBone()
    {
        AnimationClip? clip = _current?.Clip;
        if (_current != null && _current.BoundRootMotionPolicy != ClipRootMotionPolicy.LegacyAutomatic)
            return null; // Configured clips pin only their own sampled pose before mixing.
        string? path = clip?.RootBonePath;
        if (string.IsNullOrEmpty(path) || _binding == null)
            return ResolveTopmostBone();
        int idx = _binding.IndexOfBonePath(path);
        if ((uint)idx >= (uint)_binding.Bones.Length)
            return ResolveTopmostBone();
        return _binding.Bones[idx];
    }

    /// <summary>
    /// Fallback root-motion bone: the topmost transform in the resolved bone set (its parent is
    /// not itself a bound bone — typically the rig root, e.g. "Bip001"). Imported clips rarely
    /// carry <c>RootBonePath</c>, so root-motion pinning/extract would silently no-op without
    /// this while the clip's root translation curves still drove the whole skeleton around.
    /// </summary>
    private XEngine.Vector.Transform? ResolveTopmostBone()
    {
        if (_binding == null)
            return null;
        var bones = _binding.Bones;
        for (int i = 0; i < bones.Length; i++)
        {
            XEngine.Vector.Transform? b = bones[i];
            if (b == null)
                continue;
            XEngine.Vector.Transform? parent = b.Parent;
            bool parentIsBound = false;
            for (int j = 0; j < bones.Length; j++)
            {
                if (j == i || bones[j] == null)
                    continue;
                if (ReferenceEquals(bones[j], parent))
                {
                    parentIsBound = true;
                    break;
                }
            }
            if (!parentIsBound)
                return b;
        }
        return null;
    }

    /// <summary>
    /// In-place playback (ApplyRootMotion=false): clips may still carry root translation curves
    /// (e.g. the ZZZ Run clip travels ~3 m per cycle). Without pinning, the whole skeleton rides
    /// those curves — the skinned matrices then place every bone metres away from the bind pose
    /// the mesh vertices are bound in, shredding the character into debris, and the per-loop
    /// snap-back reads as strobing. Pin the root bone back to its bind offset each frame;
    /// child-bone motion (legs, arms, breathing) is untouched.
    /// </summary>
    private void PinRootBoneToBind()
    {
        if (_binding?.PinRootMotionPerState == true)
            return;
        if (!_rootBindCaptured || !_pinToBindPose)
            return;
        XEngine.Vector.Transform? rootBone = ResolveRootBone();
        if (rootBone == null)
            return;
        // Travel pin: remove only the target-space locomotion directions detected at clip bind.
        // Every state with non-zero mixer weight participates, so a fading Run cannot leak metres
        // of root travel after _current has already switched to Idle/Attack. Orthogonal bob/sway
        // remains animated instead of being lost to an XYZ reset.
        bool projectedRootTravel = PinActiveTravelAxes(rootBone);

        // Preserve the historical in-place fallback for clips that do not expose a large travel
        // direction. When the selected root is itself the detected carrier, the projection above
        // already removed locomotion while intentionally retaining its orthogonal bob/sway.
        // Rotation is never pinned: turn-in-place and lean remain valid animation.
        if (!projectedRootTravel)
            rootBone.LocalPosition = _rootBindPos;
    }

    [HotPath]
    private bool PinActiveTravelAxes(XEngine.Vector.Transform rootBone)
    {
        AnimationMixerPlayable? layer = _layer;
        SkeletonBinding? binding = _binding;
        if (layer == null || binding == null)
            return false;

        int usedCount = 0;
        bool projectedRootTravel = false;
        int inputCount = layer.InputCount;
        for (int i = 0; i < inputCount; i++)
        {
            if (layer.GetInput(i) is not AnimationClipPlayable clip || clip.Weight <= 1e-5f
                || !clip.TryGetTravelProjection(out AnimationClipPlayable.TravelProjection projection))
                continue;

            int boneIndex = projection.BoneIndex;
            if ((uint)boneIndex >= (uint)_travelPinAccumulators.Length)
                continue;
            XEngine.Vector.Transform? bone = binding.Bones[boneIndex];
            if (bone == null)
                continue;
            if (ReferenceEquals(bone, rootBone))
                projectedRootTravel = true;

            ref TravelPinAccumulator accumulator = ref _travelPinAccumulators[boneIndex];
            if (accumulator.AxisCount == 0)
                _travelPinBoneIndices[usedCount++] = boneIndex;
            for (int axisIndex = 0; axisIndex < projection.AxisCount; axisIndex++)
                accumulator.AddAxis(projection.GetAxis(axisIndex));
        }

        for (int i = 0; i < usedCount; i++)
        {
            int boneIndex = _travelPinBoneIndices[i];
            ref TravelPinAccumulator accumulator = ref _travelPinAccumulators[boneIndex];
            XEngine.Vector.Transform? bone = binding.Bones[boneIndex];
            if (bone != null)
            {
                XEngine.Vector.Float3 bindPosition = binding.ReferencePose[boneIndex].LocalPosition;
                XEngine.Vector.Float3 delta = bone.LocalPosition - bindPosition;
                accumulator.RemoveFrom(ref delta);
                bone.LocalPosition = bindPosition + delta;
            }
            accumulator.AxisCount = 0;
        }
        return projectedRootTravel;
    }

    private struct TravelPinAccumulator
    {
        internal byte AxisCount;
        private XEngine.Vector.Float3 _axis0;
        private XEngine.Vector.Float3 _axis1;
        private XEngine.Vector.Float3 _axis2;

        [HotPath]
        internal void AddAxis(XEngine.Vector.Float3 axis)
        {
            if (AxisCount > 0) axis -= _axis0 * XEngine.Vector.Float3.Dot(axis, _axis0);
            if (AxisCount > 1) axis -= _axis1 * XEngine.Vector.Float3.Dot(axis, _axis1);
            if (AxisCount > 2) axis -= _axis2 * XEngine.Vector.Float3.Dot(axis, _axis2);
            float lengthSquared = XEngine.Vector.Float3.LengthSquared(axis);
            if (lengthSquared <= 1e-8f || AxisCount >= 3)
                return;
            axis /= MathF.Sqrt(lengthSquared);
            if (AxisCount == 0) _axis0 = axis;
            else if (AxisCount == 1) _axis1 = axis;
            else _axis2 = axis;
            AxisCount++;
        }

        [HotPath]
        internal readonly void RemoveFrom(ref XEngine.Vector.Float3 delta)
        {
            if (AxisCount > 0) delta -= _axis0 * XEngine.Vector.Float3.Dot(delta, _axis0);
            if (AxisCount > 1) delta -= _axis1 * XEngine.Vector.Float3.Dot(delta, _axis1);
            if (AxisCount > 2) delta -= _axis2 * XEngine.Vector.Float3.Dot(delta, _axis2);
        }
    }

    /// <summary>
    /// Plays <paramref name="clip"/> from the start, optionally crossfading over
    /// <paramref name="fadeDuration"/> seconds. Returns the (reused or newly created) state, on which
    /// the caller can set the playback <c>Speed</c>, register events, etc. A
    /// non-positive <paramref name="fadeDuration"/> snaps: the new state takes full weight instantly
    /// and all others are zeroed.
    /// </summary>
    public AnimationClipPlayable Play(AnimationClip clip, float fadeDuration = 0f)
    {
        EnsureGraph();
        return PlayClipOnMixer(clip, fadeDuration, _layer!);
    }

    /// <summary>
    /// Plays <paramref name="clip"/> with explicit fade timing (<see cref="FadeMode"/>) and an optional
    /// easing curve. <see cref="FadeMode.FromStart"/> plays a fresh weightless clone when the same clip
    /// is already active, so the outgoing instance fades out while the new one restarts from time zero.
    /// </summary>
    public AnimationClipPlayable Play(AnimationClip clip, float fadeDuration, FadeMode mode, FadeEasing easing = FadeEasing.Linear)
    {
        EnsureGraph();
        if (FadeModes.IsFromStart(mode)
            && TryPlayFromStart(clip, fadeDuration, mode, easing, out AnimationClipPlayable? clone))
        {
            return clone;
        }
        return PlayClipOnMixer(clip, fadeDuration, _layer!, mode: FadeModes.TimingOf(mode), easing: easing);
    }

    /// <summary>Equivalent to <see cref="Play(AnimationClip, float)"/> with a fade duration.</summary>
    public AnimationClipPlayable CrossFade(AnimationClip clip, float fadeDuration) => Play(clip, fadeDuration);

    /// <summary>Equivalent to <see cref="Play(AnimationClip, float, FadeMode, FadeEasing)"/> with a fade duration.</summary>
    public AnimationClipPlayable CrossFade(AnimationClip clip, float fadeDuration, FadeMode mode, FadeEasing easing = FadeEasing.Linear)
        => Play(clip, fadeDuration, mode, easing);

    /// <summary>
    /// Plays a transition (Animancer-style): creates-or-reuses the state cached under
    /// <paramref name="transition"/>'s key, applies its speed / start time, and fades per its
    /// FadeDuration/FadeMode/FadeEasing. The transition's clip must be resident (stream it ahead like
    /// controller motions).
    /// </summary>
    public AnimationPlayable Play(ITransition transition)
    {
        EnsureGraph();
        return PlayTransitionCore(transition, transition.Key);
    }

    /// <summary>
    /// Plays the transition wrapped by <paramref name="asset"/>. The state is cached under the asset,
    /// so every play of the same asset reuses one state regardless of how many fields reference it.
    /// </summary>
    public AnimationPlayable Play(TransitionAsset asset)
    {
        AnimationTransition? transition = asset.Transition;
        if (transition == null)
            throw new InvalidOperationException(
                $"TransitionAsset '{asset.Name}' has no transition payload; assign one in its inspector.");
        EnsureGraph();
        return PlayTransitionCore(transition, asset);
    }

    /// <summary>Returns the cached state for <paramref name="transition"/>'s key, or null if never played.</summary>
    public AnimationPlayable? GetState(ITransition transition)
        => _transitionStates.TryGetValue(transition.Key, out AnimationPlayable? state) ? state : null;

    /// <summary>
    /// M50 — plays <paramref name="clip"/> consulting <see cref="TransitionLibrary"/> for the
    /// (<paramref name="fromKey"/> or the current clip's name → clip name) pair: the matched modifier
    /// supplies fade duration / normalized start / speed where the caller left them unset. Without a
    /// match (or no library) this behaves like <see cref="Play(AnimationClip, float)"/> with a snap.
    /// </summary>
    public AnimationClipPlayable PlayWithLibrary(AnimationClip clip, string? fromKey = null)
    {
        EnsureGraph();
        TransitionLibraryAsset.Modifier? m = ResolveLibraryModifier(fromKey ?? CurrentClip?.Name, clip.Name);
        float fade = m?.FadeDuration >= 0f ? m!.FadeDuration : 0f;
        AnimationClipPlayable state = PlayClipOnMixer(clip, fade, _layer!);
        if (m != null)
        {
            if (!float.IsNaN(m.NormalizedStartTime) && state.Duration > 0d)
                state.Time = (double)m.NormalizedStartTime * state.Duration;
            if (m.Speed > 0f)
                state.Speed = m.Speed;
        }
        return state;
    }

    /// <summary>
    /// M50 — plays <paramref name="asset"/>'s wrapped transition with library overrides layered on top
    /// of the transition's own settings (matched values replace; unset values keep the transition's).
    /// </summary>
    public AnimationPlayable PlayWithLibrary(TransitionAsset asset, string? fromKey = null)
    {
        AnimationTransition? transition = asset.Transition;
        if (transition == null)
            throw new InvalidOperationException(
                $"TransitionAsset '{asset.Name}' has no transition payload; assign one in its inspector.");

        TransitionLibraryAsset.Modifier? m = ResolveLibraryModifier(
            fromKey ?? CurrentClip?.Name,
            transition is ClipTransition ct ? ct.Clip.Res?.Name : transition.GetType().Name);
        if (m != null)
        {
            if (m.FadeDuration >= 0f)
                transition.FadeDuration = m.FadeDuration;
            if (!float.IsNaN(m.NormalizedStartTime))
                transition.NormalizedStartTime = m.NormalizedStartTime;
            if (m.Speed > 0f)
                transition.Speed = m.Speed;
        }
        return Play(asset);
    }

    private TransitionLibraryAsset.Modifier? ResolveLibraryModifier(string? fromKey, string? toKey)
    {
        TransitionLibraryAsset? library = TransitionLibrary.Res;
        return library?.FindBest(fromKey, toKey);
    }

    private AnimationPlayable PlayTransitionCore(ITransition transition, object key)
    {
        if (!_transitionStates.TryGetValue(key, out AnimationPlayable? state))
        {
            state = transition.CreateState(this);
            _transitionStates[key] = state;
        }

        state.Play();
        transition.Apply(state, this);

        // M49 — the transition's named events own the state's runtime events (replaced each play).
        if (transition is AnimationTransition { Events.Count: > 0 } authored && state is AnimationClipPlayable eventState)
        {
            eventState.ClearEvents();
            for (int i = 0; i < authored.Events.Count; i++)
            {
                TransitionEvent te = authored.Events[i];
                eventState.AddEvent(te.NormalizedTime, null, te.Name);
            }
        }

        if (transition.FadeDuration > 0f)
        {
            state.StartFade(1f, transition.FadeDuration, transition.FadeMode, transition.FadeEasing);
            FadeOutOthers(_layer!, state, transition.FadeDuration, transition.FadeMode, transition.FadeEasing);
        }
        else
        {
            state.Weight = 1f;
            ZeroOutOthers(_layer!, state);
        }

        if (state is AnimationClipPlayable clipState)
        {
            _current = clipState;
            CurrentClip = clipState.Clip;
            IsPlaying = true;
            if (_rootBindCaptured && _binding != null)
                TrySelectRootBindReference(_binding);
        }
        else
        {
            // Non-clip transition states (mixers, ...) drive motion without a single current clip;
            // root-motion resolution falls back to the topmost bound bone as usual.
            _current = null;
            CurrentClip = null;
            IsPlaying = true;
        }
        return state;
    }

    /// <summary>Creates the base-layer clip state a <see cref="ClipTransition"/> describes (binds, parents; does not play).</summary>
    internal AnimationClipPlayable CreateTransitionClipState(ClipTransition transition)
    {
        EnsureGraph();
        AnimationClip? clip = transition.Clip.Res;
        if (clip == null)
            throw new InvalidOperationException(
                $"ClipTransition's clip is not loaded (AssetID {transition.Clip.AssetID}); " +
                "pre-load it before Play, like controller motions.");
        var state = CreateClipState(clip);
        state.Bind(_binding!);
        state.Wrap = transition.Loop ? AnimationWrapMode.Loop : clip.Wrap;
        _layer!.AddInput(state);
        return state;
    }

    /// <summary>Parents an externally-built transition state (e.g. a <see cref="BlendTreePlayable"/>) onto the base layer.</summary>
    internal void AddTransitionState(AnimationPlayable state)
    {
        EnsureGraph();
        _layer!.AddInput(state);
    }

    /// <summary>
    /// M48 — builds the base-layer mixer state an inline mixer transition describes: a code-side
    /// <see cref="BlendTreePlayable"/> with one clip child per <see cref="MixerChild"/> entry.
    /// </summary>
    internal BlendTreePlayable CreateTransitionMixerState(BlendType type, List<MixerChild> children)
    {
        EnsureGraph();
        var mixer = new BlendTreePlayable { Type = type };
        mixer.Bind(_binding!);
        for (int i = 0; i < children.Count; i++)
        {
            AnimationClip? clip = children[i].Clip.Res;
            if (clip == null)
                throw new InvalidOperationException(
                    $"Mixer transition child {i}'s clip is not loaded (AssetID {children[i].Clip.AssetID}); " +
                    "pre-load it before Play, like controller motions.");
            var child = CreateClipState(clip);
            child.Bind(_binding!);
            child.Play();
            child.ResetPlaybackClock();
            mixer.AddChild(child, children[i].Threshold, children[i].Position, -1);
        }
        _layer!.AddInput(mixer);
        return mixer;
    }

    /// <summary>
    /// M48 — binds a mixer state's blend inputs to <see cref="Parameters"/> by name (X/Y plus
    /// optional per-child Direct names). Re-registering the same mixer replaces its binding. Values
    /// are pushed every frame before graph evaluation (LateUpdate / EvaluateGraph).
    /// </summary>
    public void RegisterMixerParameters(
        BlendTreePlayable mixer,
        string? parameterNameX,
        string? parameterNameY,
        string?[]? directParameterNames)
    {
        _mixerBindings ??= new List<MixerParameterBinding>(4);

        var binding = new MixerParameterBinding
        {
            Mixer = mixer,
            X = MakeParameterRef(parameterNameX),
            Y = MakeParameterRef(parameterNameY),
            Direct = null,
        };
        if (directParameterNames != null && directParameterNames.Length > 0)
        {
            binding.Direct = new ParameterRef[directParameterNames.Length];
            for (int i = 0; i < directParameterNames.Length; i++)
                binding.Direct[i] = MakeParameterRef(directParameterNames[i]);
        }

        for (int i = 0; i < _mixerBindings.Count; i++)
        {
            if (ReferenceEquals(_mixerBindings[i].Mixer, mixer))
            {
                _mixerBindings[i] = binding;
                return;
            }
        }
        _mixerBindings.Add(binding);
    }

    private static ParameterRef MakeParameterRef(string? name)
        => string.IsNullOrEmpty(name)
            ? default
            : new ParameterRef { Has = true, Hash = AnimationNameHash.Hash(name!) };

    /// <summary>Pushes bound parameter values into their mixers. Called before every graph evaluation.</summary>
    [HotPath]
    private void ApplyMixerParameters()
    {
        List<MixerParameterBinding>? bindings = _mixerBindings;
        if (bindings == null)
            return;

        for (int i = 0; i < bindings.Count; i++)
        {
            MixerParameterBinding b = bindings[i];
            if (b.X.Has)
                b.Mixer.ParameterValue = Parameters.GetFloat(b.X.Hash);
            if (b.Y.Has)
                b.Mixer.ParameterValueY = Parameters.GetFloat(b.Y.Hash);
            if (b.Direct != null)
            {
                int n = Math.Min(b.Direct.Length, b.Mixer.InputCount);
                for (int c = 0; c < n; c++)
                    if (b.Direct[c].Has)
                        b.Mixer.SetDirectWeight(c, Parameters.GetFloat(b.Direct[c].Hash));
            }
        }
    }

    /// <summary>Snaps the named state on layer 0 to full weight.</summary>
    public void Play(string stateName) => Play(AnimationNameHash.Hash(stateName), layer: 0);

    /// <summary>Snaps the named state on <paramref name="layer"/> to full weight.</summary>
    public void Play(string stateName, int layer)
        => Play(AnimationNameHash.Hash(stateName), layer);

    /// <summary>Snaps the state whose name hashes to <paramref name="stateNameHash"/> on layer 0.</summary>
    public void Play(int stateNameHash) => Play(stateNameHash, layer: 0);

    /// <summary>Snaps the state whose name hashes to <paramref name="stateNameHash"/> on <paramref name="layer"/>.</summary>
    public void Play(int stateNameHash, int layer)
        => GetLayerRuntime(layer)?.Play(stateNameHash, fadeDuration: 0f);

    /// <summary>Crossfades the named state on layer 0 over <paramref name="fadeDuration"/> seconds.</summary>
    public void CrossFade(string stateName, float fadeDuration)
        => CrossFade(AnimationNameHash.Hash(stateName), fadeDuration, layer: 0);

    /// <summary>Crossfades the named state on <paramref name="layer"/> over <paramref name="fadeDuration"/> seconds.</summary>
    public void CrossFade(string stateName, float fadeDuration, int layer)
        => CrossFade(AnimationNameHash.Hash(stateName), fadeDuration, layer);

    /// <summary>Crossfades the hashed state on layer 0 over <paramref name="fadeDuration"/> seconds.</summary>
    public void CrossFade(int stateNameHash, float fadeDuration)
        => CrossFade(stateNameHash, fadeDuration, layer: 0);

    /// <summary>Crossfades the hashed state on <paramref name="layer"/> over <paramref name="fadeDuration"/> seconds.</summary>
    public void CrossFade(int stateNameHash, float fadeDuration, int layer)
    {
        // The name/hash path skips the clip-object convenience wrappers, so make sure the
        // graph exists before asking the layer runtime to play (matches Play/PlayClipOnMixer,
        // which both EnsureGraph — silent no-op otherwise, e.g. an FSM crossfade in edit mode).
        EnsureGraph();
        GetLayerRuntime(layer)?.Play(stateNameHash, fadeDuration);
    }

    /// <summary>True if layer 0 has a state named <paramref name="stateName"/>.</summary>
    public bool HasState(string stateName) => HasState(0, AnimationNameHash.Hash(stateName));

    /// <summary>True if <paramref name="layer"/> has a state whose name hashes to <paramref name="stateNameHash"/>.</summary>
    public bool HasState(int layer, int stateNameHash)
        => GetLayerRuntime(layer)?.HasState(stateNameHash) ?? false;

    /// <summary>Current FSM state on layer 0 (empty if no controller / not yet evaluated).</summary>
    public AnimatorStateInfo GetCurrentAnimatorStateInfo() => GetCurrentAnimatorStateInfo(0);

    /// <summary>Current FSM state on <paramref name="layer"/> (empty if that layer has no runtime).</summary>
    public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer)
    {
        AnimatorControllerRuntime? rt = GetLayerRuntime(layer);
        if (rt != null) return rt.GetStateInfo();

        var info = default(AnimatorStateInfo);
        if (_current == null || CurrentClip == null) return info;
        info.nameHash = AnimationNameHash.Hash(CurrentClip.Name);
        info.length = (float)_current.Duration;
        info.normalizedTime = _current.NormalizedTime;
        info.isLooping = _current.Wrap == AnimationWrapMode.Loop;
        return info;
    }

    /// <summary>
    /// M52 — destination state snapshot for the transition in progress on <paramref name="layer"/>
    /// (empty when not transitioning). While a transition runs, the destination is the layer's
    /// current state, so this reports its name/length/normalized time.
    /// </summary>
    public AnimatorStateInfo GetNextAnimatorStateInfo(int layer = 0)
    {
        AnimatorControllerRuntime? rt = GetLayerRuntime(layer);
        if (rt != null)
            return rt.IsInTransition ? rt.GetStateInfo() : default;
        return _current != null && _current.IsFading ? GetCurrentAnimatorStateInfo(layer) : default;
    }

    /// <summary>
    /// M52 — true while the layer's current state is still crossfading in (FSM transition, or a
    /// code-driven CrossFade still ramping the current state's weight).
    /// </summary>
    public bool IsInTransition(int layer = 0)
    {
        AnimatorControllerRuntime? rt = GetLayerRuntime(layer);
        if (rt != null)
            return rt.IsInTransition;
        return _current != null && _current.IsFading;
    }

    /// <summary>
    /// M52 — advances this animator manually by <paramref name="deltaTime"/> seconds (FSM evaluate +
    /// graph evaluate + root handling), mirroring what <see cref="LateUpdate"/> does per frame. Use
    /// for cutscene-driven or simulation-stepped characters.
    /// </summary>
    public void Update(float deltaTime) => EvaluateGraph(deltaTime);

    /// <summary>
    /// M52 — registers <paramref name="callback"/> to fire once, at the end of a frame, after
    /// <paramref name="state"/>'s weight has finished fading OUT to zero (Animancer Exit Event —
    /// sequencing hook for cleanup/pooling after a crossfade fully releases a state).
    /// </summary>
    public void OnFadeOut(Playable state, Action callback)
    {
        _exitWatch ??= new List<(Playable state, Action callback)>(4);
        _exitWatch.Add((state, callback));
    }

    private List<(Playable state, Action callback)>? _exitWatch;

    /// <summary>Invokes and drops fade-out watchers whose state has settled at zero weight.</summary>
    private void FlushExitWatchers()
    {
        List<(Playable state, Action callback)>? watch = _exitWatch;
        if (watch == null || watch.Count == 0)
            return;

        int write = 0;
        for (int i = 0; i < watch.Count; i++)
        {
            var (state, callback) = watch[i];
            if (state.Weight <= 1e-5f && !state.IsFading)
            {
                callback();
                continue; // Drop fired watchers in place (swap-free compaction).
            }
            watch[write++] = (state, callback);
        }
        watch.RemoveRange(write, watch.Count - write);
    }

    /// <summary>
    /// Fades <paramref name="clip"/>'s state out (instantly when <paramref name="fadeDuration"/> &lt;= 0)
    /// and pauses it. The state stays registered for reuse on the next <see cref="Play"/>. FromStart
    /// clones of the clip are stopped alongside its main state.
    /// </summary>
    public void Stop(AnimationClip clip, float fadeDuration = 0f)
    {
        if (_states.TryGetValue(clip, out AnimationClipPlayable? state))
        {
            StopState(state, fadeDuration);
            if (_current == state)
            {
                _current = null;
                CurrentClip = null;
                IsPlaying = false;
            }
        }

        if (_weightlessClones.TryGetValue(clip, out List<AnimationClipPlayable>? clones))
        {
            for (int i = 0; i < clones.Count; i++)
            {
                AnimationClipPlayable clone = clones[i];
                StopState(clone, fadeDuration);
                if (_current == clone)
                {
                    _current = null;
                    CurrentClip = null;
                    IsPlaying = false;
                }
            }
        }
    }

    /// <summary>Snaps every state to zero weight and pauses them.</summary>
    public void StopAll()
    {
        foreach (AnimationClipPlayable s in _states.Values)
        {
            s.Weight = 0f;
            s.Pause();
        }
        foreach (List<AnimationClipPlayable> clones in _weightlessClones.Values)
        {
            for (int i = 0; i < clones.Count; i++)
            {
                clones[i].Weight = 0f;
                clones[i].Pause();
            }
        }
        _current = null;
        CurrentClip = null;
        IsPlaying = false;
    }

    /// <summary>Pauses the most-recently-played state at its current time.</summary>
    public void Pause()
    {
        _current?.Pause();
        IsPlaying = false;
    }

    /// <summary>Resumes the most-recently-played state from its current time.</summary>
    public void Resume()
    {
        _current?.Play();
        IsPlaying = true;
    }

    /// <summary>Returns the existing state for <paramref name="clip"/>, or <c>null</c> if never played.</summary>
    public AnimationClipPlayable? GetState(AnimationClip clip)
    {
        _states.TryGetValue(clip, out AnimationClipPlayable? state);
        return state;
    }

    /// <summary>
    /// Activates FSM (asset-driven) mode: builds a runtime over <paramref name="constant"/> that will
    /// drive state selection each frame. Replaces any previously active runtime. Multi-layer constants
    /// allocate one mixer input and one <see cref="AnimatorControllerRuntime"/> per <see cref="LayerDef"/>.
    /// </summary>
    public AnimatorControllerRuntime SetController(AnimatorControllerConstant constant)
    {
        BuildFromCompiled(constant);
        return _runtime!;
    }

    /// <summary>
    /// Compiles <paramref name="author"/> (the on-disk controller asset) to its runtime constant and
    /// activates FSM mode. Convenience over <see cref="SetController(AnimatorControllerConstant)"/> so
    /// callers don't need to reference the compiler directly.
    /// </summary>
    public AnimatorControllerRuntime SetControllerAsset(AnimatorController author)
    {
        // The compiler snapshots each state's Motion.Res into the constant — under async loading
        // that is a transient null, and states compiled empty NEVER re-resolve (silent dead FSM).
        // Block until every referenced clip is resident before compiling.
        List<AssetRef<AnimationClip>> clipRefs = author.GetClipReferences();
        for (int i = 0; i < clipRefs.Count; i++)
            clipRefs[i].EnsureLoaded();
        AnimatorControllerConstant constant = AnimatorControllerCompiler.Compile(author);
        LockControllerMotionsToScene(clipRefs);
        return SetController(constant);
    }

    /// <summary>
    /// Activates FSM mode ONLY when every referenced motion is actually resolvable: blocks on the
    /// clip loads, verifies none is missing, and reports false (without compiling) otherwise so the
    /// caller can retry later. A half-resolved compile is permanent — states would look alive
    /// (name hashes present) while playing nothing.
    /// </summary>
    public bool TryActivateController(AnimatorController author)
    {
        // Non-blocking activation: request every missing clip through the background streamer
        // and activate only once ALL are resident. On a warm machine this is one pass (same as
        // the old blocking loop); on a cold one (fresh clone — Library empty, import backlog
        // running) the old `EnsureLoaded()` per clip blocked the whole play start for 9-18s per
        // character while the import queue churned. LateUpdate retries this method until the
        // streamer catches up, so the FSM activates a few frames late instead of freezing.
        List<AssetRef<AnimationClip>> clipRefs = author.GetClipReferences();
        int referenced = 0;
        int resolved = 0;
        for (int i = 0; i < clipRefs.Count; i++)
        {
            Guid id = clipRefs[i].AssetID;
            if (id == System.Guid.Empty && clipRefs[i].ResWeak == null)
                continue; // intentionally-empty state (e.g. leftovers filtered at generation)
            referenced++;
            if (clipRefs[i].Res != null)
            {
                resolved++;
                continue;
            }
            if (id != System.Guid.Empty)
                AssetLoader.Request(id); // idempotent — deduped by the loader queue
        }
        if (referenced != resolved)
            return false;
        AnimatorControllerConstant constant = AnimatorControllerCompiler.Compile(author);
        LockControllerMotionsToScene(clipRefs);
        SetController(constant);
        return true;
    }

    /// <summary>True when the motion ref resolves (or legitimately carries no asset id).</summary>
    private static bool IsMotionResolvable(AssetRef<AnimationClip> r)
        => r.AssetID == System.Guid.Empty ? r.ResWeak != null : r.Res != null;

    /// <summary>
    /// Pins every controller clip to the owning scene. The compiled runtime intentionally stores
    /// raw clip pointers for allocation-free state changes; without a scene owner, an inactive
    /// state can cross the asset backend's idle timeout and leave that pointer disposed. Scene
    /// locking keeps all states switchable and releases the assets automatically on scene unload.
    /// </summary>
    private void LockControllerMotionsToScene(List<AssetRef<AnimationClip>> clipRefs)
    {
        var scene = GameObject.Scene;
        if (scene == null)
            return;

        for (int i = 0; i < clipRefs.Count; i++)
            clipRefs[i].LockToScene(scene);
    }

    /// <summary>
    /// Resolves <see cref="Controller"/> for FSM activation: the cached/async-resolved instance when
    /// resident, otherwise request streaming. OnEnable/LateUpdate must never wait behind an
    /// unrelated model on the asset worker. Explicit SetControllerAsset remains synchronous.
    /// </summary>
    private AnimatorController? ResolveControllerNow()
    {
        if (Controller.AssetID == System.Guid.Empty)
            return Controller.Res;

        return Controller.Res;
    }

    /// <summary>Rebuilds <see cref="StateMachineBehaviour"/> instances after a script reload.</summary>
    public void ReloadStateMachineBehaviours()
    {
        AnimatorControllerRuntime[]? layers = _layerRuntimes;
        if (layers != null)
        {
            for (int i = 0; i < layers.Length; i++)
                layers[i].ReloadBehaviours();
            return;
        }
        _runtime?.ReloadBehaviours();
    }

    /// <summary>
    /// Pre-loads (requests background streaming for) every clip <paramref name="controller"/> can reach,
    /// so that entering any state doesn't stall on a clip that's still streaming. Call ahead of playback
    /// (e.g. on a loading screen). Clips already loaded are a no-op; clips with no GUID (in-memory) are
    /// skipped. Backed by <see cref="AnimatorController.GetClipReferences"/>.
    /// </summary>
    public void PrefetchController(AnimatorController controller)
    {
        var refs = controller.GetClipReferences();
        for (int i = 0; i < refs.Count; i++)
        {
            System.Guid id = refs[i].AssetID;
            if (id != System.Guid.Empty)
                AssetLoader.Request(id);
        }
    }

    // ---- Parameter API (FSM mode) ----
    // Name-based overloads hash via AnimationNameHash; hash-based overloads skip the hashing.

    public void SetFloat(string name, float value) => SetFloat(AnimationNameHash.Hash(name), value);
    public void SetFloat(int nameHash, float value)
    {
        if (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            _runtime.SetFloat(idx, value);
    }

    public float GetFloat(string name) => GetFloat(AnimationNameHash.Hash(name));
    public float GetFloat(int nameHash)
        => (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            ? _runtime.GetFloat(idx) : 0f;

    public void SetBool(string name, bool value) => SetBool(AnimationNameHash.Hash(name), value);
    public void SetBool(int nameHash, bool value)
    {
        if (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            _runtime.SetBool(idx, value);
    }

    public bool GetBool(string name) => GetBool(AnimationNameHash.Hash(name));
    public bool GetBool(int nameHash)
        => _runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0 && _runtime.GetBool(idx);

    public void SetInteger(string name, int value) => SetInteger(AnimationNameHash.Hash(name), value);
    public void SetInteger(int nameHash, int value)
    {
        if (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            _runtime.SetInt(idx, value);
    }

    public int GetInteger(string name) => GetInteger(AnimationNameHash.Hash(name));
    public int GetInteger(int nameHash)
        => (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            ? _runtime.GetInt(idx) : 0;

    public void SetTrigger(string name) => SetTrigger(AnimationNameHash.Hash(name));
    public void SetTrigger(int nameHash)
    {
        if (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            _runtime.SetTrigger(idx);
    }

    public void ResetTrigger(string name) => ResetTrigger(AnimationNameHash.Hash(name));
    public void ResetTrigger(int nameHash)
    {
        if (_runtime != null && _runtime.GetParameterIndex(nameHash) is int idx and >= 0)
            _runtime.ResetTrigger(idx);
    }

    /// <summary>The current mixer layer holding all active states. Exposed for advanced/graph use.</summary>
    public AnimationMixerPlayable? Layer => _layer;

    /// <summary>The layer mixer that is the graph root (base + overlay layers). Exposed for advanced use.</summary>
    public AnimationLayerMixerPlayable? LayerMixer => _layerMixer;

    /// <summary>Number of mixer layers (controller layers in FSM mode, plus any code-driven <see cref="AddLayer"/> inputs).</summary>
    public int LayerCount => _layerMixer?.InputCount ?? 0;

    /// <summary>FSM runtime for <paramref name="layer"/>, or null if that layer has no controller FSM.</summary>
    public AnimatorControllerRuntime? GetLayerRuntime(int layer)
    {
        AnimatorControllerRuntime[]? runtimes = _layerRuntimes;
        if (runtimes != null)
        {
            if ((uint)layer >= (uint)runtimes.Length) return null;
            return runtimes[layer];
        }
        return layer == 0 ? _runtime : null;
    }

    /// <summary>Sets the mixer weight for <paramref name="layer"/>. Does not allocate.</summary>
    public void SetLayerWeight(int layer, float weight)
        => _layerMixer?.SetLayerWeight(layer, weight);

    /// <summary>Current mixer weight for <paramref name="layer"/>, or 0 if out of range.</summary>
    public float GetLayerWeight(int layer)
        => _layerMixer != null ? _layerMixer.GetLayerWeight(layer) : 0f;

    /// <summary>The skeleton binding the graph writes to. Used by the FSM runtime to build BlendTrees.</summary>
    public SkeletonBinding? Skeleton => _binding;

    /// <summary>
    /// Lower-level play: adds <paramref name="playable"/> as a new input on the base layer's mixer and
    /// fades it in (and the others out). Use this to play a non-clip motion such as a
    /// <see cref="global::XEngine.Animation.BlendTreePlayable"/>. For plain clips prefer
    /// <see cref="Play(AnimationClip, float)"/> (which reuses states).
    /// </summary>
    public AnimationPlayable PlayPlayable(AnimationPlayable playable, float fadeDuration = 0f)
    {
        EnsureGraph();
        return PlayPlayableOnMixer(playable, fadeDuration, _layer!);
    }

    /// <summary>
    /// Plays <paramref name="clip"/> on <paramref name="mixer"/>, fading other inputs on that mixer.
    /// Used by the FSM runtime so each controller layer owns its own clip states.
    /// </summary>
    public AnimationClipPlayable PlayClipOnMixer(
        AnimationClip clip,
        float fadeDuration,
        AnimationMixerPlayable mixer,
        bool skipFirstFadeUpdate = false,
        bool holdEntryFrame = false,
        float initialFadeDelta = 0f,
        FadeMode mode = FadeMode.FixedDuration,
        FadeEasing easing = FadeEasing.Linear)
    {
        EnsureGraph();
        AnimationClipPlayable state = GetOrCreateStateOnMixer(clip, mixer);
        state.Speed = Speed;
        state.Wrap = Wrap;
        state.Play();
        state.ResetPlaybackClock();
        state.HoldEntryFrame = holdEntryFrame || clip.Duration == 0d;

        if (fadeDuration > 0f)
        {
            state.StartFade(1f, fadeDuration, mode, easing, skipFirstFadeUpdate);
            FadeOutOthers(mixer, state, fadeDuration, mode, easing, skipFirstFadeUpdate);
            if (initialFadeDelta > 0f)
            {
                int count = mixer.InputCount;
                for (int i = 0; i < count; i++)
                    mixer.GetInput(i).AdvanceFadeRemainder(initialFadeDelta, deferNextUpdate: true);
            }
        }
        else
        {
            state.Weight = 1f;
            ZeroOutOthers(mixer, state);
        }

        if (mixer == _layer)
        {
            _current = state;
            CurrentClip = clip;
            IsPlaying = true;
            // The snapshot is structural to the skeleton, not per-clip. If the clip selects a
            // different root path, refresh only the selected values from the same immutable
            // reference pose; never re-capture the live, possibly animated Transform.
            if (_rootBindCaptured && _binding != null)
                TrySelectRootBindReference(_binding);
        }
        return state;
    }

    /// <summary>
    /// Adds <paramref name="playable"/> on <paramref name="mixer"/> and fades it in.
    /// </summary>
    public AnimationPlayable PlayPlayableOnMixer(AnimationPlayable playable, float fadeDuration, AnimationMixerPlayable mixer)
    {
        EnsureGraph();
        PinTreeAssets(playable);
        mixer.AddInput(playable);
        if (fadeDuration > 0f)
        {
            playable.StartFade(1f, fadeDuration);
            FadeOutOthers(mixer, playable, fadeDuration);
        }
        else
        {
            playable.Weight = 1f;
            ZeroOutOthers(mixer, playable);
        }
        return playable;
    }

    /// <summary>
    /// Appends an overlay layer and returns its (empty) clip-state mixer. Add <see cref="AnimationClipPlayable"/>
    /// inputs to the returned mixer to animate that layer. <paramref name="mask"/> selects which bones the
    /// layer affects (null = full body); <paramref name="mode"/> is Override or Additive.
    /// </summary>
    public AnimationMixerPlayable AddLayer(AvatarMask? mask, AnimatorLayerBlendingMode mode, float weight = 1f)
        => AddLayer(mask, mode, weight, additiveReferenceClip: null);

    /// <summary>
    /// M51 — <see cref="AddLayer(AvatarMask?, AnimatorLayerBlendingMode, float)"/> with an additive
    /// reference pose: the layer contributes <c>pose − reference(clip @ t0)</c> per bone instead of
    /// deltas around identity/zero (Mecanim additive-reference semantics).
    /// </summary>
    public AnimationMixerPlayable AddLayer(
        AvatarMask? mask,
        AnimatorLayerBlendingMode mode,
        float weight,
        AnimationClip? additiveReferenceClip)
    {
        EnsureGraph();
        var mixer = new AnimationMixerPlayable();
        mixer.Bind(_binding!);
        float[]? boneWeights = mask?.BuildBoneWeights(_binding!);
        _layerMixer!.AddLayer(mixer, weight, mode, boneWeights);
        if (mode == AnimatorLayerBlendingMode.Additive && additiveReferenceClip != null)
        {
            _layerMixer.SetAdditiveReferencePose(
                _layerMixer.InputCount - 1,
                AnimationLayerMixerPlayable.BuildAdditiveReferencePose(additiveReferenceClip, _binding!));
        }
        return mixer;
    }

    /// <summary>
    /// Rebuilds the skeleton binding and graph while preserving the first stable destination
    /// reference pose for every existing bone path. Call after restructuring the hierarchy; newly
    /// introduced paths use their current local TRS, while existing paths never re-capture a live
    /// animated pose.
    /// </summary>
    public void RebuildGraph()
    {
        _graph?.Destroy();
        UnsubscribeAssetPublication();

        SkeletonBinding binding = SkeletonBinding.Build(SkeletonRoot ?? Transform);
        if (_referenceBinding != null)
            PreserveReferencePose(_referenceBinding, binding);
        _referenceBinding = binding;

        _graph = new PlayableGraph(new GameClock());
        _binding = binding;
        PrepareRootMotionBuffers(binding.BoneCount);
        if (_travelPinAccumulators.Length != binding.BoneCount)
        {
            _travelPinAccumulators = new TravelPinAccumulator[binding.BoneCount];
            _travelPinBoneIndices = new int[binding.BoneCount];
        }
        else
        {
            Array.Clear(_travelPinAccumulators, 0, _travelPinAccumulators.Length);
        }
        _output = new AnimationPlayableOutput { Binding = _binding };
        _graph.AddOutput(_output);

        // Base layer (Override, full body) holds the code-driven / FSM clip states.
        _layer = new AnimationMixerPlayable();
        _layer.Bind(_binding);

        // Graph root is the layer mixer; the base layer is its first input.
        _layerMixer = new AnimationLayerMixerPlayable();
        _layerMixer.Bind(_binding);
        _layerMixer.AddLayer(_layer, weight: 1f, AnimatorLayerBlendingMode.Override, boneWeights: null);

        _graph.Root = _layerMixer;
        _output.Source = _layerMixer;
        _states.Clear();
        _weightlessClones.Clear();
        _transitionStates.Clear();
        _mixerBindings?.Clear();
        _current = null;
        CurrentClip = null;
        IsPlaying = false;
        _runtime = null;
        _layerRuntimes = null;
        ResetRootBindSnapshot();
        SubscribeAssetPublication();
    }

    /// <summary>
    /// Cold path that intentionally replaces the retained destination reference pose with the
    /// hierarchy's current local TRS, then rebuilds the graph. Before calling, place every bone in
    /// the authored reference pose. Normal enable/disable and graph rebuilds must use
    /// <see cref="RebuildGraph"/> so an evaluated animation frame cannot become the new reference.
    /// </summary>
    public void RebuildGraphAndRecaptureReferencePose()
    {
        _referenceBinding = null;
        RebuildGraph();
    }

    private static void PreserveReferencePose(SkeletonBinding source, SkeletonBinding destination)
    {
        string[] paths = destination.Paths;
        BonePose[] referencePose = destination.ReferencePose;
        for (int i = 0; i < paths.Length; i++)
        {
            int sourceIndex = source.IndexOfBonePath(paths[i]);
            if ((uint)sourceIndex < (uint)source.ReferencePose.Length)
                referencePose[i] = source.ReferencePose[sourceIndex];
        }
    }

    private void ResetRootBindSnapshot()
    {
        _rootBindCaptured = false;
        _pinToBindPose = false;
        _rootBindPos = default;
        Array.Clear(_travelPinAccumulators, 0, _travelPinAccumulators.Length);
    }

    private void BuildFromCompiled(AnimatorControllerConstant constant)
    {
        EnsureGraph();

        LayerDef[] layers = constant.Layers;
        int n = layers.Length > 0 ? layers.Length : 1;

        _layerMixer = new AnimationLayerMixerPlayable();
        _layerMixer.Bind(_binding!);

        ParameterDef[] parms = constant.Parameters;
        var shared = new float[parms.Length];
        for (int i = 0; i < parms.Length; i++)
        {
            shared[i] = parms[i].Type switch
            {
                AnimatorParameterType.Float => parms[i].DefaultFloat,
                AnimatorParameterType.Int => parms[i].DefaultInt,
                AnimatorParameterType.Bool => parms[i].DefaultBool ? 1f : 0f,
                _ => 0f,
            };
        }
        _sharedParameters = shared;

        _layerRuntimes = new AnimatorControllerRuntime[n];
        AnimationMixerPlayable? baseMixer = null;
        for (int i = 0; i < n; i++)
        {
            var mixer = new AnimationMixerPlayable();
            mixer.Bind(_binding!);
            if (i == 0)
                baseMixer = mixer;

            float weight = 1f;
            AnimatorLayerBlendingMode mode = AnimatorLayerBlendingMode.Override;
            AvatarMask? mask = null;
            if (layers.Length > 0)
            {
                weight = layers[i].Weight;
                mode = layers[i].Blending;
                mask = layers[i].Mask;
            }

            float[]? boneWeights = mask?.BuildBoneWeights(_binding!);
            _layerMixer.AddLayer(mixer, weight, mode, boneWeights);
            _layerRuntimes[i] = new AnimatorControllerRuntime(constant, this, i, shared, mixer);
        }

        _layer = baseMixer!;
        _runtime = _layerRuntimes[0];
        _graph.Root = _layerMixer;
        _output.Source = _layerMixer;
        _states.Clear();
        _current = null;
    }

    private void EvaluateRuntimes(float dt)
    {
        AnimatorControllerRuntime[]? runtimes = _layerRuntimes;
        if (runtimes != null)
        {
            AnimatorControllerConstant constant = runtimes[0].Constant;
            LayerDef[] layers = constant.Layers;
            for (int i = 0; i < runtimes.Length; i++)
            {
                int synced = (uint)i < (uint)layers.Length ? layers[i].SyncedLayerIndex : -1;
                if (synced >= 0) continue;
                runtimes[i].Evaluate(dt);
                // M51 — a layer bound to a parameter takes its mixer weight from that parameter.
                int weightParam = (uint)i < (uint)layers.Length ? layers[i].WeightParamIndex : -1;
                if (weightParam >= 0 && _sharedParameters != null && (uint)weightParam < (uint)_sharedParameters.Length)
                    SetLayerWeight(i, _sharedParameters[weightParam]);
            }
            for (int i = 0; i < runtimes.Length; i++)
            {
                if ((uint)i >= (uint)layers.Length) continue;
                int synced = layers[i].SyncedLayerIndex;
                if (synced < 0 || (uint)synced >= (uint)runtimes.Length) continue;
                runtimes[i].SyncFrom(runtimes[synced], layers[i].SyncedLayerAffectsTiming);
            }
            return;
        }

        _runtime?.Evaluate(dt);
    }

    /// <summary>
    /// M55 — constructs the right playable for a clip: humanoid (muscle) clips on an avatar-bound
    /// Animator become <see cref="HumanoidClipPlayable"/> cross-rig states; everything else stays a
    /// plain <see cref="AnimationClipPlayable"/>. Callers still Bind and parent the result.
    /// </summary>
    internal AnimationClipPlayable CreateClipState(AnimationClip clip)
    {
        XEngine.Animation.Humanoid.Avatar? avatar = Avatar.Res;
        PinGraphAsset(clip.AssetID);
        PinGraphAsset(avatar?.AssetID ?? Guid.Empty);
        if (clip.IsHumanoid && avatar != null && avatar.IsHumanoid && _binding != null)
        {
            var humanoidState = new HumanoidClipPlayable(clip, avatar);
            humanoidState.Bind(_binding);
            return humanoidState;
        }
        var state = new AnimationClipPlayable(clip);
        state.ConfigureHumanoidSampler(avatar);
        return state;
    }

    private AnimationClipPlayable GetOrCreateStateOnMixer(AnimationClip clip, AnimationMixerPlayable mixer)
    {
        if (mixer == _layer)
        {
            if (_states.TryGetValue(clip, out AnimationClipPlayable? state))
                return state;

            state = CreateClipState(clip);
            state.IsCodeClipState = true;
            state.Bind(_binding!);
            mixer.AddInput(state);
            _states[clip] = state;
            return state;
        }

        int n = mixer.InputCount;
        for (int i = 0; i < n; i++)
        {
            if (mixer.GetInput(i) is AnimationClipPlayable existing && existing.Clip == clip)
                return existing;
        }

        var created = CreateClipState(clip);
        created.Bind(_binding!);
        mixer.AddInput(created);
        return created;
    }

    /// <summary>
    /// FadeMode.FromStart path: when <paramref name="clip"/>'s main state is still meaningfully
    /// audible (weight ≥ <see cref="WeightlessThreshold"/>), restart via a weightless clone instead of
    /// snapping the live state's time back to zero. Returns false when the plain play path applies
    /// (never played, or already faded out — Play resets the clock anyway).
    /// </summary>
    private bool TryPlayFromStart(
        AnimationClip clip,
        float fadeDuration,
        FadeMode mode,
        FadeEasing easing,
        out AnimationClipPlayable result)
    {
        result = null!;
        if (!_states.TryGetValue(clip, out AnimationClipPlayable? existing))
            return false;
        if (existing.Weight < WeightlessThreshold)
            return false;

        AnimationClipPlayable clone = AcquireWeightlessClone(clip);
        clone.Speed = Speed;
        clone.Wrap = Wrap;
        clone.Play();
        clone.ResetPlaybackClock();
        clone.HoldEntryFrame = clip.Duration == 0d;

        FadeMode timing = FadeModes.TimingOf(mode);
        if (fadeDuration > 0f)
        {
            clone.StartFade(1f, fadeDuration, timing, easing);
            FadeOutOthers(_layer!, clone, fadeDuration, timing, easing);
        }
        else
        {
            clone.Weight = 1f;
            ZeroOutOthers(_layer!, clone);
        }

        _current = clone;
        CurrentClip = clip;
        IsPlaying = true;
        // Same root-path refresh the plain play path performs, so travel pinning follows the clip.
        if (_rootBindCaptured && _binding != null)
            TrySelectRootBindReference(_binding);
        result = clone;
        return true;
    }

    /// <summary>
    /// Returns a weightless clone state for <paramref name="clip"/>: a settled (zero-weight, not
    /// fading) clone when one exists, a new one while under <see cref="MaxCloneCount"/>, and the
    /// lightest existing clone beyond that (interrupting its fade rather than growing unbounded).
    /// </summary>
    private AnimationClipPlayable AcquireWeightlessClone(AnimationClip clip)
    {
        if (!_weightlessClones.TryGetValue(clip, out List<AnimationClipPlayable>? clones))
        {
            clones = new List<AnimationClipPlayable>(MaxCloneCount);
            _weightlessClones[clip] = clones;
        }

        AnimationClipPlayable? lightest = null;
        for (int i = 0; i < clones.Count; i++)
        {
            AnimationClipPlayable c = clones[i];
            if (!c.IsFading && c.Weight <= 1e-5f)
                return c;
            if (lightest == null || c.Weight < lightest.Weight)
                lightest = c;
        }

        if (clones.Count < MaxCloneCount)
        {
            var clone = CreateClipState(clip);
            clone.IsWeightlessClone = true;
            clone.Bind(_binding!);
            _layer!.AddInput(clone);
            clones.Add(clone);
            return clone;
        }

        return lightest!;
    }

    /// <summary>Stops one clip state (main or FromStart clone): fades out, pauses, stays registered.</summary>
    private static void StopState(AnimationClipPlayable state, float fadeDuration)
    {
        if (fadeDuration > 0f && state.Weight > 0f)
            state.StartFade(0f, fadeDuration);
        else
            state.Weight = 0f;
        state.Pause();
    }

    private static void FadeOutOthers(
        AnimationMixerPlayable mixer,
        Playable keep,
        float duration,
        FadeMode mode = FadeMode.FixedDuration,
        FadeEasing easing = FadeEasing.Linear,
        bool skipFirstFadeUpdate = false)
    {
        int n = mixer.InputCount;
        for (int i = 0; i < n; i++)
        {
            Playable p = mixer.GetInput(i);
            if (p != keep && p.Weight > 0f)
                p.StartFade(0f, duration, mode, easing, skipFirstFadeUpdate);
        }
    }

    private static void ZeroOutOthers(AnimationMixerPlayable mixer, Playable keep)
    {
        int n = mixer.InputCount;
        for (int i = 0; i < n; i++)
        {
            Playable p = mixer.GetInput(i);
            if (p != keep)
                p.Weight = 0f;
        }
    }

    private void EnsureGraph()
    {
        if (_binding == null)
            RebuildGraph();
    }
}
