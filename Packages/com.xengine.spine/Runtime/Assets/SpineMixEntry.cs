using System;

namespace XEngine.Spine;

/// <summary>One custom crossfade duration between two animations (<see cref="SkeletonDataAsset.CustomMixes"/>).</summary>
[Serializable]
public sealed class SpineMixEntry
{
    public string From = "";
    public string To = "";
    public float Duration;
}
