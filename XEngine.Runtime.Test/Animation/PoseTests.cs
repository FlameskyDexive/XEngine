// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the <see cref="Pose"/> blend math in isolation (no Transforms, no clips): Clear resets
/// channels, Accumulate weighted-sums only driven channels, NormalizeRotations yields unit quats,
/// and unanimated bones are left untouched so the output layer can skip them.
/// </summary>
public class PoseTests
{
    private static Pose SingleBonePose(float posX, PoseChannel channels)
    {
        var p = new Pose(boneCount: 1, blendShapeCount: 0);
        p.Bones[0] = new BonePose
        {
            LocalPosition = new Float3(posX, 0, 0),
            LocalRotation = Quaternion.Identity,
            LocalScale = new Float3(1, 1, 1),
            Channels = channels,
        };
        return p;
    }

    [Fact]
    public void Clear_ResetsAllChannelsToNone()
    {
        var p = SingleBonePose(5f, PoseChannel.All);

        p.Clear();

        Assert.Equal(PoseChannel.None, p.Bones[0].Channels);
    }

    [Fact]
    public void Accumulate_SingleFullWeight_PreservesSource()
    {
        var src = SingleBonePose(7f, PoseChannel.Position);
        var dst = new Pose(1, 0);
        dst.Clear();

        dst.Accumulate(src, 1f);
        dst.NormalizeRotations();

        Assert.Equal(7f, dst.Bones[0].LocalPosition.X);
        Assert.Equal(PoseChannel.Position, dst.Bones[0].Channels);
    }

    [Fact]
    public void Accumulate_TwoEqualWeights_AveragesValues()
    {
        var a = SingleBonePose(0f, PoseChannel.Position);
        var b = SingleBonePose(10f, PoseChannel.Position);
        var dst = new Pose(1, 0);
        dst.Clear();

        dst.Accumulate(a, 0.5f);
        dst.Accumulate(b, 0.5f);
        dst.NormalizeRotations();

        Assert.Equal(5f, dst.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void Accumulate_OnlyDrivenChannels_Touched()
    {
        // Source drives only Position. Destination's rotation/scale must be left alone.
        var src = new Pose(1, 0);
        src.Bones[0] = new BonePose
        {
            LocalPosition = new Float3(3f, 0, 0),
            LocalRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.4f),
            LocalScale = new Float3(9f, 9f, 9f),
            Channels = PoseChannel.Position,
        };

        var dst = new Pose(1, 0);
        dst.Clear();
        // Pre-set a known rotation/scale that should survive (position-channel-only source).
        dst.Bones[0].LocalRotation = Quaternion.Identity;
        dst.Bones[0].LocalScale = new Float3(2, 2, 2);

        dst.Accumulate(src, 1f);

        Assert.Equal(3f, dst.Bones[0].LocalPosition.X);
        Assert.Equal(2f, dst.Bones[0].LocalScale.X);        // untouched
        Assert.Equal(1f, dst.Bones[0].LocalRotation.W);     // identity survived (untouched)
        Assert.Equal(PoseChannel.Position, dst.Bones[0].Channels);
    }

    [Fact]
    public void Accumulate_BlendShapesWeighted()
    {
        var a = new Pose(0, 1) { BlendShapeWeights = [2f] };
        a.BlendShapeWeights[0] = 2f;
        var b = new Pose(0, 1);
        b.BlendShapeWeights[0] = 10f;

        var dst = new Pose(0, 1);
        dst.Clear();
        dst.Accumulate(a, 0.5f);
        dst.Accumulate(b, 0.5f);

        Assert.Equal(6f, dst.BlendShapeWeights[0]); // 0.5*2 + 0.5*10
    }

    [Fact]
    public void NormalizeRotations_ProducesUnitQuaternions()
    {
        var src = new Pose(1, 0);
        src.Bones[0] = new BonePose
        {
            LocalRotation = new Quaternion(1f, 2f, 3f, 4f), // deliberately non-unit
            Channels = PoseChannel.Rotation,
        };

        var dst = new Pose(1, 0);
        dst.Clear();
        dst.Accumulate(src, 1f);
        dst.NormalizeRotations();

        var r = dst.Bones[0].LocalRotation;
        float len = Maths.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W);
        Assert.Equal(1f, len, precision: 5);
        // Direction preserved (scaled to unit): original (1,2,3,4) normalizes to (1,2,3,4)/sqrt(30).
        Assert.Equal(1f / Maths.Sqrt(30f), r.X, precision: 5);
    }

    [Fact]
    public void Accumulate_UnanimatedBone_LeavesChannelsNone()
    {
        // A child that drives nothing (Channels None) must not flip the destination bone's channels on.
        var empty = new Pose(1, 0);
        empty.Bones[0] = new BonePose { Channels = PoseChannel.None };

        var dst = new Pose(1, 0);
        dst.Clear();

        dst.Accumulate(empty, 1f);

        Assert.Equal(PoseChannel.None, dst.Bones[0].Channels);
    }
}
