using System;

using Spine;

using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 4 view: model state → SkeletonAnimation tracks.</summary>
public class SpineboyBeginnerView : MonoBehaviour
{
    public SpineboyBeginnerModel? Model;
    public SkeletonAnimation? SkeletonAnimation;
    public AnimationReferenceAsset? Run;
    public AnimationReferenceAsset? Idle;
    public AnimationReferenceAsset? Aim;
    public AnimationReferenceAsset? Shoot;
    public AnimationReferenceAsset? Jump;
    public string RunName = "run";
    public string IdleName = "idle";
    public string AimName = "aim";
    public string ShootName = "shoot";
    public string JumpName = "jump";
    public string FootstepEventName = "footstep";

    public string LastFootstep { get; private set; } = "";
    public SpineBeginnerBodyState PreviousViewState { get; private set; }

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        if (Model != null)
        {
            Model.ShootEvent += PlayShoot;
            Model.StartAimEvent += StartPlayingAim;
            Model.StopAimEvent += StopPlayingAim;
        }

        if (SkeletonAnimation != null)
            SkeletonAnimation.AnimationEvent += HandleEvent;
    }

    public override void OnDispose()
    {
        if (Model != null)
        {
            Model.ShootEvent -= PlayShoot;
            Model.StartAimEvent -= StartPlayingAim;
            Model.StopAimEvent -= StopPlayingAim;
        }

        if (SkeletonAnimation != null)
            SkeletonAnimation.AnimationEvent -= HandleEvent;
        base.OnDispose();
    }

    public override void Update()
    {
        if (SkeletonAnimation?.Skeleton == null || Model == null)
            return;

        if ((SkeletonAnimation.Skeleton.ScaleX < 0f) != Model.FacingLeft)
            Turn(Model.FacingLeft);

        if (PreviousViewState != Model.State)
            PlayNewStableAnimation();
        PreviousViewState = Model.State;
    }

    public void PlayShoot()
    {
        TrackEntry? shoot = GettingStartedAnims.Set(SkeletonAnimation, 1, Shoot, ShootName, loop: false);
        if (shoot != null)
        {
            shoot.MixAttachmentThreshold = 1f;
            shoot.SetMixDuration(0f, 0f);
            SkeletonAnimation!.AnimationState.AddEmptyAnimation(1, 0.5f, 0.1f);
        }

        TrackEntry? aim = GettingStartedAnims.Set(SkeletonAnimation, 2, Aim, AimName, loop: false);
        if (aim != null)
        {
            aim.MixAttachmentThreshold = 1f;
            aim.SetMixDuration(0f, 0f);
            SkeletonAnimation!.AnimationState.AddEmptyAnimation(2, 0.5f, 0.1f);
        }
    }

    public void StartPlayingAim()
    {
        TrackEntry? aim = GettingStartedAnims.Set(SkeletonAnimation, 2, Aim, AimName, loop: true);
        if (aim == null)
            return;
        aim.MixAttachmentThreshold = 1f;
        aim.SetMixDuration(0f, 0f);
    }

    public void StopPlayingAim()
        => SkeletonAnimation?.AnimationState.AddEmptyAnimation(2, 0.5f, 0.1f);

    public void Turn(bool facingLeft)
    {
        if (SkeletonAnimation?.Skeleton != null)
            SkeletonAnimation.Skeleton.ScaleX = facingLeft ? -1f : 1f;
    }

    private void PlayNewStableAnimation()
    {
        string fallback = Model!.State switch
        {
            SpineBeginnerBodyState.Jumping => JumpName,
            SpineBeginnerBodyState.Running => RunName,
            _ => IdleName
        };
        AnimationReferenceAsset? reference = Model.State switch
        {
            SpineBeginnerBodyState.Jumping => Jump,
            SpineBeginnerBodyState.Running => Run,
            _ => Idle
        };
        GettingStartedAnims.Set(SkeletonAnimation, 0, reference, fallback, loop: true);
    }

    private void HandleEvent(TrackEntry trackEntry, Event e)
    {
        if (string.Equals(e.Data.Name, FootstepEventName, StringComparison.Ordinal))
            LastFootstep = e.Data.Name;
    }
}
