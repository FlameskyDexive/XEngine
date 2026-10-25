using Spine;

using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

internal static class GettingStartedAnims
{
    public static TrackEntry? Set(SkeletonAnimation? animation, int track, AnimationReferenceAsset? reference, string fallback, bool loop)
    {
        if (animation == null)
            return null;
        if (reference != null && !string.IsNullOrEmpty(reference.AnimationName))
            return animation.SetAnimation(track, reference, loop);
        return string.IsNullOrEmpty(fallback) ? null : animation.SetAnimation(track, fallback, loop);
    }

    public static TrackEntry? Add(SkeletonAnimation? animation, int track, AnimationReferenceAsset? reference, string fallback, bool loop, float delay)
    {
        if (animation == null)
            return null;
        if (reference != null && !string.IsNullOrEmpty(reference.AnimationName))
            return animation.AddAnimation(track, reference, loop, delay);
        return string.IsNullOrEmpty(fallback) ? null : animation.AddAnimation(track, fallback, loop, delay);
    }

    public static bool Has(SkeletonAnimation? animation, string name)
        => animation?.Skeleton?.Data.FindAnimation(name) != null;
}
