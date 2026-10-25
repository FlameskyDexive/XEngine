using System;

namespace XEngine.Spine38;

/// <summary>One custom crossfade duration between two animations.</summary>
[Serializable]
public sealed class SpineMixEntry
{
    public string From = "";
    public string To = "";
    public float Duration;
}
