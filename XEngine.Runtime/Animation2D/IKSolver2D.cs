// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>2D CCD and FABRIK solvers. Bones point along local +X; chain is root-to-tip.</summary>
public static class IKSolver2D
{
    public static void SolveCCD(Bone2D[] chain, Float2 target, int iterations = 8)
    {
        if (chain == null || chain.Length == 0)
            return;

        int last = chain.Length - 1;
        iterations = Math.Max(iterations, 1);
        for (int iter = 0; iter < iterations; iter++)
        {
            for (int i = last; i >= 0; i--)
            {
                Bone2D? bone = chain[i];
                Transform? transform = bone?.Transform;
                if (bone == null || transform == null)
                    continue;

                Float2 origin = new(transform.Position.X, transform.Position.Y);
                Float3 tip3 = WorldTip(chain[last]);
                Float2 toTip = new(tip3.X - origin.X, tip3.Y - origin.Y);
                Float2 toTarget = new(target.X - origin.X, target.Y - origin.Y);
                float delta = SignedAngle(toTip, toTarget);
                if (Maths.Abs(delta) < 1e-6f)
                    continue;

                Float3 euler = transform.LocalEulerAngles;
                euler.Z += delta * Maths.Rad2Deg;
                transform.LocalEulerAngles = euler;
            }
        }
    }

    public static void SolveFABRIK(Bone2D[] chain, Float2 target, int iterations = 8)
    {
        if (chain == null || chain.Length == 0)
            return;

        int bones = chain.Length;
        int joints = bones + 1;
        Span<Float2> pos = stackalloc Float2[joints];
        Transform? rootTransform = chain[0].Transform;
        if (rootTransform == null)
            return;

        pos[0] = new Float2(rootTransform.Position.X, rootTransform.Position.Y);
        for (int i = 0; i < bones; i++)
        {
            Float3 tip = WorldTip(chain[i]);
            pos[i + 1] = new Float2(tip.X, tip.Y);
        }

        Float2 root = pos[0];
        iterations = Math.Max(iterations, 1);
        for (int iter = 0; iter < iterations; iter++)
        {
            pos[bones] = target;
            for (int i = bones - 1; i >= 0; i--)
                pos[i] = Place(pos[i + 1], pos[i], chain[i].Length);

            pos[0] = root;
            for (int i = 0; i < bones; i++)
                pos[i + 1] = Place(pos[i], pos[i + 1], chain[i].Length);
        }

        for (int i = 0; i < bones; i++)
        {
            Transform? transform = chain[i].Transform;
            if (transform == null)
                continue;

            Float2 dir = pos[i + 1] - pos[i];
            float worldZ = MathF.Atan2(dir.Y, dir.X) * Maths.Rad2Deg;
            float parentZ = 0f;
            Transform? parent = transform.Parent;
            if (parent != null)
                parentZ = parent.EulerAngles.Z;
            transform.LocalEulerAngles = new Float3(0f, 0f, worldZ - parentZ);
        }
    }

    internal static Float3 WorldTip(Bone2D bone)
        => bone.Transform.TransformPoint(new Float3(bone.Length, 0f, 0f));

    private static Float2 Place(Float2 from, Float2 toward, float length)
    {
        Float2 delta = toward - from;
        float mag = Float2.Length(delta);
        if (mag < 1e-8f)
            return from + new Float2(length, 0f);
        return from + delta * (length / mag);
    }

    private static float SignedAngle(Float2 from, Float2 to)
    {
        float fromSq = from.X * from.X + from.Y * from.Y;
        float toSq = to.X * to.X + to.Y * to.Y;
        if (fromSq < 1e-12f || toSq < 1e-12f)
            return 0f;

        float cross = from.X * to.Y - from.Y * to.X;
        float dot = from.X * to.X + from.Y * to.Y;
        return MathF.Atan2(cross, dot);
    }
}
