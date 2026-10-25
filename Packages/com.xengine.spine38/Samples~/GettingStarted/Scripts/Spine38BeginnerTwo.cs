using XEngine.Async;
using XEngine.Runtime;

namespace XEngine.Spine38.Samples.GettingStarted;

/// <summary>Getting Started scene 2: play walk, then queue jump, then return to walk.</summary>
public sealed class Spine38BeginnerTwo : MonoBehaviour
{
    public SkeletonAnimation? SkeletonAnimation;
    public string IdleAnimation = "idle";
    public string WalkAnimation = "walk";
    public string JumpAnimation = "jump";

    public override void Start()
    {
        RunDemo().Forget();
    }

    private async XTaskVoid RunDemo()
    {
        SkeletonAnimation? animation = SkeletonAnimation ?? GetComponent<SkeletonAnimation>();
        if (animation == null)
            return;

        animation.SetAnimation(0, WalkAnimation, loop: true);
        await XTask.Delay(System.TimeSpan.FromSeconds(1.2), DelayType.DeltaTime);
        if (animation.Skeleton?.Data.FindAnimation(JumpAnimation) != null)
        {
            animation.SetAnimation(0, JumpAnimation, loop: false);
            animation.AddAnimation(0, WalkAnimation, loop: true, delay: 0f);
        }
    }
}
