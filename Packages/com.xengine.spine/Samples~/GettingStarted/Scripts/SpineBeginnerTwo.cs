using System;

using Spine;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 2: SetAnimation / AddAnimation walk → run → idle, then flip.</summary>
public class SpineBeginnerTwo : MonoBehaviour
{
    public string WalkAnimationName = "walk";
    public string RunAnimationName = "run";
    public string IdleAnimationName = "idle";
    public string ShootAnimationName = "shoot";
    public string IdleTurnAnimationName = "idle";
    public string RunToIdleAnimationName = "idle";
    public float RunWalkDuration = 1.5f;
    public bool AutoPlay = true;

    public SkeletonAnimation? SkeletonAnimation;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        if (AutoPlay)
            RunDemo().Forget();
    }

    public TrackEntry? PlayWalk()
        => SetNamed(WalkAnimationName, loop: true);

    public TrackEntry? PlayRun()
        => SetNamed(RunAnimationName, loop: true);

    public TrackEntry? PlayRunToIdle()
    {
        SetNamed(Resolve(RunToIdleAnimationName, IdleAnimationName), loop: false);
        return AddNamed(IdleAnimationName, loop: true, delay: 0f);
    }

    public void FlipX(bool facingLeft)
    {
        if (SkeletonAnimation?.Skeleton != null)
            SkeletonAnimation.Skeleton.ScaleX = facingLeft ? -1f : 1f;
    }

    private async XTaskVoid RunDemo()
    {
        while (this.IsValid())
        {
            PlayWalk();
            await XTask.Delay(TimeSpan.FromSeconds(RunWalkDuration));
            if (!this.IsValid()) return;

            PlayRun();
            await XTask.Delay(TimeSpan.FromSeconds(RunWalkDuration));
            if (!this.IsValid()) return;

            PlayRunToIdle();
            await XTask.Delay(TimeSpan.FromSeconds(1f));
            if (!this.IsValid()) return;

            FlipX(true);
            SetNamed(Resolve(IdleTurnAnimationName, IdleAnimationName), loop: false);
            AddNamed(IdleAnimationName, loop: true, delay: 0f);
            await XTask.Delay(TimeSpan.FromSeconds(0.5f));
            if (!this.IsValid()) return;

            FlipX(false);
            SetNamed(Resolve(IdleTurnAnimationName, IdleAnimationName), loop: false);
            AddNamed(IdleAnimationName, loop: true, delay: 0f);
            await XTask.Delay(TimeSpan.FromSeconds(0.5f));
        }
    }

    private TrackEntry? SetNamed(string name, bool loop)
        => GettingStartedAnims.Has(SkeletonAnimation, name)
            ? SkeletonAnimation!.SetAnimation(0, name, loop)
            : null;

    private TrackEntry? AddNamed(string name, bool loop, float delay)
        => GettingStartedAnims.Has(SkeletonAnimation, name)
            ? SkeletonAnimation!.AddAnimation(0, name, loop, delay)
            : null;

    private string Resolve(string preferred, string fallback)
        => GettingStartedAnims.Has(SkeletonAnimation, preferred) ? preferred : fallback;
}
