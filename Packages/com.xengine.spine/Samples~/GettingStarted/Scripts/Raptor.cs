using System;

using Spine;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 3: Raptor walk on track 0, gun-grab / gun-holster on track 1.</summary>
public class Raptor : MonoBehaviour
{
    public AnimationReferenceAsset? Walk;
    public AnimationReferenceAsset? GunGrab;
    public AnimationReferenceAsset? GunKeep;
    public string WalkName = "walk";
    public string GunGrabName = "gun-grab";
    public string GunKeepName = "gun-holster";
    public bool AutoPlay = true;
    public float MinDelay = 0.5f;
    public float MaxDelay = 3f;

    public SkeletonAnimation? SkeletonAnimation;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        PlayWalk();
        if (AutoPlay)
            GunGrabRoutine().Forget();
    }

    public TrackEntry? PlayWalk()
        => GettingStartedAnims.Set(SkeletonAnimation, 0, Walk, WalkName, loop: true);

    public TrackEntry? PlayGunGrab()
        => GettingStartedAnims.Set(SkeletonAnimation, 1, GunGrab, GunGrabName, loop: false);

    public TrackEntry? PlayGunKeep()
        => GettingStartedAnims.Set(SkeletonAnimation, 1, GunKeep, GunKeepName, loop: false);

    private async XTaskVoid GunGrabRoutine()
    {
        var random = new Random();
        while (this.IsValid())
        {
            await XTask.Delay(TimeSpan.FromSeconds(NextDelay(random)));
            if (!this.IsValid()) return;
            PlayGunGrab();
            await XTask.Delay(TimeSpan.FromSeconds(NextDelay(random)));
            if (!this.IsValid()) return;
            PlayGunKeep();
        }
    }

    private float NextDelay(Random random)
        => MinDelay + (float)random.NextDouble() * Math.Max(0f, MaxDelay - MinDelay);
}
