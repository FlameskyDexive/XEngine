using System;

using Spine;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Spine38;

/// <summary>xgui <see cref="Graphic"/> that poses a Spine 3.8 skeleton into the Canvas mesh.</summary>
[AddComponentMenu("Spine 3.8/Skeleton Graphic (UI)")]
public class SkeletonGraphic : MaskableGraphic
{
    public AssetRef<SkeletonDataAsset> SkeletonDataAsset;
    public string AnimationName = "";
    public bool Loop;
    public float TimeScale = 1f;
    public bool UnscaledTime;
    public string InitialSkinName = "";
    public float MeshScale = 1f;

    [NonSerialized] private Skeleton? _skeleton;
    [NonSerialized] private AnimationState? _state;
    [NonSerialized] private readonly SpineMeshGenerator _generator = new();
    [NonSerialized] private bool _initialized;
    [NonSerialized] private Texture2D? _mainTex;
    [NonSerialized] private DateTime _lastEditTickUtc;

    public Skeleton? Skeleton
    {
        get
        {
            Initialize(overwrite: false);
            return _skeleton;
        }
    }

    public AnimationState AnimationState
    {
        get
        {
            Initialize(overwrite: false);
            return _state!;
        }
    }

    public event AnimationState.TrackEntryEventDelegate? AnimationEvent;

    public void Initialize(bool overwrite)
    {
        if (_initialized && !overwrite && _state != null)
            return;

        DetachState();
        SkeletonDataAsset? asset = SkeletonDataAsset.ResWeak
            ?? AssetDatabase.Get(SkeletonDataAsset.AssetID) as SkeletonDataAsset;
        if (asset == null)
        {
            _initialized = false;
            return;
        }

        _skeleton = new Skeleton(asset.GetSkeletonData());
        if (!string.IsNullOrEmpty(InitialSkinName))
            _skeleton.SetSkin(InitialSkinName);
        _skeleton.SetToSetupPose();
        _state = new AnimationState(asset.GetAnimationStateData());
        _state.Event += OnSpineEvent;
        if (!string.IsNullOrEmpty(AnimationName))
        {
            global::Spine.Animation? animation = _skeleton.Data.FindAnimation(AnimationName);
            if (animation != null)
                _state.SetAnimation(0, animation, Loop);
        }

        _initialized = true;
        SetVerticesDirty();
    }

    public void SetSkeleton(Skeleton skeleton)
    {
        DetachState();
        _skeleton = skeleton;
        _state = new AnimationState(new AnimationStateData(skeleton.Data));
        _state.Event += OnSpineEvent;
        _initialized = true;
        SetVerticesDirty();
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
        }

        TrackEntry entry = _state.SetAnimation(trackIndex, animation, loop);
        SetVerticesDirty();
        return entry;
    }

    public override void LateUpdate()
        => UpdateAnimation(UnscaledTime ? Time.UnscaledDeltaTime : Time.DeltaTime);

    public void UpdateAnimation(float deltaTime)
    {
        Initialize(overwrite: false);
        if (_state == null || _skeleton == null)
            return;

        float scaled = deltaTime * TimeScale;
        _state.Update(scaled);
        _state.Apply(_skeleton);
        _skeleton.Update(scaled);
        _skeleton.UpdateWorldTransform();
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        if (_skeleton == null)
            Initialize(overwrite: false);
        if (!Application.IsPlaying)
            UpdateAnimation(EditModeDeltaTime());
        if (_skeleton == null)
            return;

        _generator.Build(_skeleton, Color);
        Float2 offset = Float2.Zero;
        RectTransform? rt = GameObject.RectTransform;
        if (rt != null)
        {
            Float2 size = rt.ComputedRect.Size;
            offset = new Float2((0.5f - rt.Pivot.X) * size.X, (0.5f - rt.Pivot.Y) * size.Y);
        }

        SpineUiMeshWriter.Write(vh.Builder, _generator, MeshScale, offset);
        if (_generator.BatchCount > 0)
            _mainTex = _generator.GetBatch(0).Texture;
    }

    public override void PopulateProperties(PropertyState props, in UIContext context)
    {
        base.PopulateProperties(props, context);
        props.SetTexture("_MainTex", _mainTex ?? Texture2D.LoadDefault(DefaultTexture.White));
    }

    private float EditModeDeltaTime()
    {
        DateTime now = DateTime.UtcNow;
        float dt = _lastEditTickUtc == default
            ? 0f
            : (float)(now - _lastEditTickUtc).TotalSeconds;
        _lastEditTickUtc = now;
        if (dt <= 0f)
            return 0f;
        return dt > 0.05f ? 0.05f : dt;
    }

    public override void OnDispose()
    {
        DetachState();
        _skeleton = null;
        _initialized = false;
        base.OnDispose();
    }

    private void OnSpineEvent(TrackEntry entry, Event e)
        => AnimationEvent?.Invoke(entry, e);

    private void DetachState()
    {
        if (_state == null)
            return;
        _state.Event -= OnSpineEvent;
        _state = null;
    }
}
