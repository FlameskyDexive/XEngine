using XEngine.Runtime.UI;

namespace XEngine.Runtime.UI;

/// <summary>Package name for the existing backend-neutral rectangular mask implementation.</summary>
[AddComponentMenu("UI/Rect Mask 2D")]
public class RectMask2D : RectMask
{
    public override void OnDirtyFlagsChanged(UIDirtyFlags flags)
    {
        base.OnDirtyFlagsChanged(flags);
        if ((flags & (UIDirtyFlags.Hierarchy | UIDirtyFlags.Clip | UIDirtyFlags.Layout)) != 0)
            CanvasUpdateRegistry.RegisterClip(this);
    }

    public override void OnCanvasRebuildCompleted(UIDirtyFlags processedFlags)
    {
        base.OnCanvasRebuildCompleted(processedFlags);
        CanvasUpdateRegistry.Unregister(this, processedFlags | UIDirtyFlags.Clip);
    }
}
