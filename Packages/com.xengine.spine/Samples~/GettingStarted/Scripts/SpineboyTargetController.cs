using System;

using Spine;

using XEngine.Runtime;
using XEngine.Spine;
using XEngine.Vector;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 4: aim bone follows the mouse in skeleton space.</summary>
public class SpineboyTargetController : MonoBehaviour
{
    public SkeletonAnimation? SkeletonAnimation;
    public string BoneName = "crosshair";
    public Camera? Camera;

    private Bone? _bone;

    public override void Start()
    {
        SkeletonAnimation ??= GetComponent<SkeletonAnimation>();
        _bone = SkeletonAnimation?.Skeleton?.FindBone(BoneName);
    }

    public override void Update()
    {
        if (_bone == null || SkeletonAnimation == null)
            return;

        Float3 world = MouseWorld();
        Float3 local = SkeletonAnimation.Transform.InverseTransformPoint(world);
        if (SkeletonAnimation.Skeleton != null)
        {
            local.X *= SkeletonAnimation.Skeleton.ScaleX;
            local.Y *= SkeletonAnimation.Skeleton.ScaleY;
        }

        _bone.Pose.X = local.X;
        _bone.Pose.Y = local.Y;
    }

    private Float3 MouseWorld()
    {
        Int2 mouse = Input.MousePosition;
        if (Camera != null)
        {
            Float2 size = FramebufferSize();
            Ray ray = Camera.ScreenPointToRay(new Float2(mouse.X, mouse.Y), size);
            float t = Math.Abs(ray.Direction.Z) > 0.0001f
                ? (SkeletonAnimation!.Transform.Position.Z - ray.Origin.Z) / ray.Direction.Z
                : 0f;
            return ray.Origin + ray.Direction * t;
        }

        return new Float3(mouse.X * 0.01f, mouse.Y * 0.01f, 0f);
    }

    private static Float2 FramebufferSize()
    {
        try
        {
            if (Window.InternalWindow != null)
            {
                var fb = Window.InternalWindow.FramebufferSize;
                return new Float2(Math.Max(1, fb.X), Math.Max(1, fb.Y));
            }
        }
        catch (Exception)
        {
        }

        return new Float2(1280f, 720f);
    }
}
