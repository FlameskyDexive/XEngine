using System;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 6: play blink on track 1 at random intervals.</summary>
public class SpineBlinkPlayer : MonoBehaviour
{
    public const int BlinkTrack = 1;

    public SkeletonAnimation? SkeletonAnimation;
    public SkeletonGraphic? SkeletonGraphic;
    public AnimationReferenceAsset? BlinkAnimation;
    public string BlinkName = "blink";
    public float MinimumDelay = 0.15f;
    public float MaximumDelay = 3f;
    public bool AutoPlay = true;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        SkeletonGraphic ??= GetComponent<SkeletonGraphic>();
        if (AutoPlay)
            BlinkRoutine().Forget();
    }

    public void PlayBlink()
    {
        if (SkeletonAnimation != null)
            GettingStartedAnims.Set(SkeletonAnimation, BlinkTrack, BlinkAnimation, BlinkName, loop: false);
        else if (SkeletonGraphic != null && !string.IsNullOrEmpty(BlinkName))
            SkeletonGraphic.SetAnimation(BlinkTrack, BlinkName, loop: false);
    }

    private async XTaskVoid BlinkRoutine()
    {
        var random = new Random();
        while (this.IsValid())
        {
            PlayBlink();
            float span = MinimumDelay + (float)random.NextDouble() * Math.Max(0f, MaximumDelay - MinimumDelay);
            await XTask.Delay(TimeSpan.FromSeconds(span));
        }
    }
}
