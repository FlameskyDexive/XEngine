using System;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>WASD + gravity jump. Spec from Voxel Play TPS jump, rewritten.</summary>
public sealed class VoxelCharacterMotor
{
    public float MoveSpeed = 6f;
    public float JumpSpeed = 7.5f;
    public float Gravity = 22f;

    public float VerticalSpeed { get; private set; }
    public bool Grounded { get; private set; }

    public void ResetVerticalSpeed(float speed = 0f) => VerticalSpeed = speed;

    public Float3 Step(Float2 wasd, bool jump, bool grounded, Float3 forward, Float3 right, float dt)
    {
        dt = Math.Max(dt, 1e-4f);
        Grounded = grounded;
        if (Grounded && VerticalSpeed < 0f)
            VerticalSpeed = 0f;
        if (jump && Grounded)
        {
            VerticalSpeed = JumpSpeed;
            Grounded = false;
        }
        else if (!Grounded)
            VerticalSpeed -= Gravity * dt;

        Float3 flatForward = forward;
        flatForward.Y = 0f;
        float fLen = Float3.Length(flatForward);
        if (fLen > 1e-4f)
            flatForward /= fLen;
        Float3 flatRight = right;
        flatRight.Y = 0f;
        float rLen = Float3.Length(flatRight);
        if (rLen > 1e-4f)
            flatRight /= rLen;

        Float3 motion = ((flatForward * wasd.Y) + (flatRight * wasd.X)) * MoveSpeed * dt;
        motion.Y = VerticalSpeed * dt;
        return motion;
    }
}
