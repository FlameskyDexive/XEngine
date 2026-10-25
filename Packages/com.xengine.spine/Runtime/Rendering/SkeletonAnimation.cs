using System;

using Spine;

using XEngine.Runtime;

namespace XEngine.Spine;

/// <summary>
/// Poses a <see cref="SkeletonRenderer"/> from <see cref="AnimationState"/> each LateUpdate
/// (SetAnimation / AddAnimation, multi-track, events).
/// </summary>
[AddComponentMenu("Spine/Skeleton Animation")]
public class SkeletonAnimation : SkeletonRenderer
{
    public string AnimationName = "";
    public bool Loop;
    public float TimeScale = 1f;
    public bool UnscaledTime;
    public string InitialSkinName = "";

    [NonSerialized] private AnimationState? _state;
    [NonSerialized] private bool _initialized;
    [NonSerialized] private string _appliedAnimationName = "";

    public AnimationState AnimationState
    {
        get
        {
            Initialize(overwrite: false);
            return _state!;
        }
    }

    public event AnimationState.TrackEntryEventDelegate? AnimationEvent;
    public event AnimationState.TrackEntryDelegate? AnimationComplete;

    /// <summary>After <see cref="AnimationState.Apply"/>, before world transform. Adjust local bones here.</summary>
    public event Action<SkeletonAnimation>? UpdateLocal;

    public void Initialize(bool overwrite)
    {
        if (_initialized && !overwrite && _state != null)
            return;

        DetachState();
        EnsureSkeleton();
        if (_skeleton == null)
        {
            _initialized = false;
            return;
        }

        if (!string.IsNullOrEmpty(InitialSkinName))
            _skeleton.SetSkin(InitialSkinName);

        SkeletonDataAsset? asset = SkeletonDataAsset.ResWeak
            ?? AssetDatabase.Get(SkeletonDataAsset.AssetID) as SkeletonDataAsset;
        AnimationStateData stateData = asset != null
            ? asset.GetAnimationStateData()
            : new AnimationStateData(_skeleton.Data);
        _state = new AnimationState(stateData);
        _state.Event += OnSpineEvent;
        _state.Complete += OnSpineComplete;

        if (!string.IsNullOrEmpty(AnimationName))
        {
            global::Spine.Animation? animation = _skeleton.Data.FindAnimation(AnimationName);
            if (animation != null)
                _state.SetAnimation(0, animation, Loop);
            _appliedAnimationName = AnimationName;
        }

        _initialized = true;
    }

    public TrackEntry? SetAnimation(int trackIndex, string animationName, bool loop)
    {
        Initialize(overwrite: false);
        if (_state == null || _skeleton == null || string.IsNullOrEmpty(animationName))
            return null;

        global::Spine.Animation? animation = _skeleton.Data.FindAnimation(animationName);
        if (animation == null)
            return null;

        if (trackIndex == 0)
        {
            AnimationName = animationName;
            Loop = loop;
            _appliedAnimationName = animationName;
        }

        return _state.SetAnimation(trackIndex, animation, loop);
    }

    public TrackEntry? SetAnimation(int trackIndex, AnimationReferenceAsset? animation, bool loop)
    {
        if (animation == null)
            return null;
        global::Spine.Animation? resolved = animation.GetAnimation();
        if (resolved == null)
            return SetAnimation(trackIndex, animation.AnimationName, loop);

        Initialize(overwrite: false);
        if (_state == null)
            return null;

        if (trackIndex == 0)
        {
            AnimationName = resolved.Name;
            Loop = loop;
            _appliedAnimationName = resolved.Name;
        }

        return _state.SetAnimation(trackIndex, resolved, loop);
    }

    public TrackEntry? AddAnimation(int trackIndex, string animationName, bool loop, float delay)
    {
        Initialize(overwrite: false);
        if (_state == null || _skeleton == null || string.IsNullOrEmpty(animationName))
            return null;

        global::Spine.Animation? animation = _skeleton.Data.FindAnimation(animationName);
        return animation == null ? null : _state.AddAnimation(trackIndex, animation, loop, delay);
    }

    public TrackEntry? AddAnimation(int trackIndex, AnimationReferenceAsset? animation, bool loop, float delay)
    {
        if (animation == null)
            return null;
        global::Spine.Animation? resolved = animation.GetAnimation();
        if (resolved == null)
            return AddAnimation(trackIndex, animation.AnimationName, loop, delay);

        Initialize(overwrite: false);
        return _state?.AddAnimation(trackIndex, resolved, loop, delay);
    }

    public override void LateUpdate()
        => UpdateAnimation(UnscaledTime ? Time.UnscaledDeltaTime : Time.DeltaTime);

    /// <summary>Advance and apply animation. Tests and tooling call this with an explicit delta.</summary>
    public void UpdateAnimation(float deltaTime)
    {
        Initialize(overwrite: false);
        if (_state == null || _skeleton == null)
            return;

        SyncInspectorAnimation();
        float scaled = deltaTime * TimeScale;
        _state.Update(scaled);
        _state.Apply(_skeleton);
        _skeleton.Update(scaled);
        UpdateLocal?.Invoke(this);
        _skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
    }

    public override void InvalidateSkeleton()
    {
        DetachState();
        _initialized = false;
        _appliedAnimationName = "";
        base.InvalidateSkeleton();
    }

    public override void OnDispose()
    {
        DetachState();
        _initialized = false;
        base.OnDispose();
    }

    private void SyncInspectorAnimation()
    {
        if (_state == null || _skeleton == null)
            return;
        if (string.Equals(AnimationName, _appliedAnimationName, StringComparison.Ordinal))
            return;

        if (string.IsNullOrEmpty(AnimationName))
        {
            _state.ClearTrack(0);
            _appliedAnimationName = AnimationName;
            return;
        }

        global::Spine.Animation? animation = _skeleton.Data.FindAnimation(AnimationName);
        if (animation != null)
            _state.SetAnimation(0, animation, Loop);
        _appliedAnimationName = AnimationName;
    }

    private void OnSpineEvent(TrackEntry entry, Event e)
        => AnimationEvent?.Invoke(entry, e);

    private void OnSpineComplete(TrackEntry entry)
        => AnimationComplete?.Invoke(entry);

    private void DetachState()
    {
        if (_state == null)
            return;
        _state.Event -= OnSpineEvent;
        _state.Complete -= OnSpineComplete;
        _state = null;
    }
}
