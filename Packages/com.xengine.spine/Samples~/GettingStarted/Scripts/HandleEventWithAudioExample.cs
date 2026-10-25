using System;

using Spine;

using XEngine.Runtime;
using XEngine.Spine;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 2: hook AnimationState.Event. Audio is logged when no clip is wired.</summary>
public class HandleEventWithAudioExample : MonoBehaviour
{
    public SkeletonAnimation? SkeletonAnimation;
    public string EventName = "footstep";
    public bool LogDebugMessage = true;

    public string LastEventName { get; private set; } = "";
    public int EventCount { get; private set; }

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        if (SkeletonAnimation == null)
            return;
        SkeletonAnimation.Initialize(overwrite: false);
        SkeletonAnimation.AnimationEvent += HandleAnimationStateEvent;
    }

    public override void OnDispose()
    {
        if (SkeletonAnimation != null)
            SkeletonAnimation.AnimationEvent -= HandleAnimationStateEvent;
        base.OnDispose();
    }

    private void HandleAnimationStateEvent(TrackEntry trackEntry, Event e)
    {
        if (!string.Equals(e.Data.Name, EventName, StringComparison.Ordinal))
            return;

        LastEventName = e.Data.Name;
        EventCount++;
        if (LogDebugMessage)
            Debug.Log($"[Spine] Event fired: {e.Data.Name}");
    }
}
