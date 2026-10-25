using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Spine.Samples.GettingStarted;

/// <summary>Scene 5: keep the camera over the hero with optional X/Y clamps.</summary>
public class ConstrainedCamera : MonoBehaviour
{
    public Transform? Target;
    public Float2 Offset = new(0f, 1.5f);
    public bool ConstrainX;
    public bool ConstrainY;
    public Float2 Min = new(-20f, 0f);
    public Float2 Max = new(20f, 10f);

    public override void LateUpdate()
    {
        if (Target == null)
            return;

        Float3 p = Target.Position;
        float x = p.X + Offset.X;
        float y = p.Y + Offset.Y;
        if (ConstrainX)
            x = Math.Clamp(x, Min.X, Max.X);
        if (ConstrainY)
            y = Math.Clamp(y, Min.Y, Max.Y);
        Transform.Position = new Float3(x, y, Transform.Position.Z);
    }
}
