using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 5: map platformer state names onto SkeletonAnimation clips.</summary>
public class SkeletonAnimationHandleExample : MonoBehaviour
{
    public SkeletonAnimation? SkeletonAnimation;
    public string LastStateName { get; private set; } = "";

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
    }

    public void PlayAnimationForState(string? stateName, int trackIndex)
    {
        if (SkeletonAnimation == null || string.IsNullOrEmpty(stateName))
            return;

        string clip = stateName switch
        {
            "rise" => GettingStartedAnims.Has(SkeletonAnimation, "rise") ? "rise" : "jump",
            _ => stateName
        };
        if (!GettingStartedAnims.Has(SkeletonAnimation, clip))
            return;

        LastStateName = stateName;
        SkeletonAnimation.SetAnimation(trackIndex, clip, loop: clip is not "attack" and not "jump");
    }

    public void SetFlip(float horizontal)
    {
        if (SkeletonAnimation?.Skeleton == null || horizontal == 0f)
            return;
        SkeletonAnimation.Skeleton.ScaleX = horizontal < 0f ? -1f : 1f;
    }
}
