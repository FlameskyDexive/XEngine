using System;

using Spine;

using XEngine.Runtime;

namespace XEngine.Spine;

/// <summary>Named animation on a <see cref="SkeletonDataAsset"/> (spine-unity compatible sub-asset).</summary>
public sealed class AnimationReferenceAsset : ScriptableObject
{
    public AssetRef<SkeletonDataAsset> SkeletonDataAsset;
    public string AnimationName = "";

    [NonSerialized] private global::Spine.Animation? _animation;

    public global::Spine.Animation? Animation => GetAnimation();

    public global::Spine.Animation? GetAnimation()
    {
        if (_animation != null && string.Equals(_animation.Name, AnimationName, StringComparison.Ordinal))
            return _animation;

        SkeletonDataAsset? asset = SkeletonDataAsset.ResWeak
            ?? AssetDatabase.Get(SkeletonDataAsset.AssetID) as SkeletonDataAsset;
        if (asset == null || string.IsNullOrEmpty(AnimationName))
            return null;

        _animation = asset.GetSkeletonData().FindAnimation(AnimationName);
        return _animation;
    }

    public static implicit operator global::Spine.Animation?(AnimationReferenceAsset? asset)
        => asset?.GetAnimation();
}
