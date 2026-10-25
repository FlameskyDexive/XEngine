// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Runtime.Test;

/// <summary>Tests for <see cref="AnimationCurve"/> evaluation, looping, and key management.</summary>
public class AnimationCurveTests
{
    [Fact]
    public void OrderedAndUnorderedInsertion_PreservesStableDuplicates()
    {
        var keys = new CurveKeyCollection();
        var first = new KeyFrame(1, 10);
        var second = new KeyFrame(1, 20);
        keys.Add(first);
        keys.Add(second);
        keys.Add(new KeyFrame(0, 0));
        keys.Add(new KeyFrame(2, 30));
        var third = new KeyFrame(1, 40);
        keys.Add(third);
        Assert.Equal(5, keys.Count);
        Assert.Same(first, keys[1]);
        Assert.Same(second, keys[2]);
        Assert.Same(third, keys[3]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(242)]
    [InlineData(10000)]
    public void BinaryRoundTrip_PreservesEveryKeyField(int count)
    {
        var curve = new AnimationCurve { PreLoop = CurveLoopType.Cycle, PostLoop = CurveLoopType.Linear };
        curve.Keys.Clear();
        for (int i = 0; i < count; i++)
            curve.Keys.Add(new KeyFrame(i, i * .25f, -2, 3, CurveContinuity.Step)
            {
                InWeight = .1f, OutWeight = .7f, InTangentMode = AnimationTangentMode.Broken,
                OutTangentMode = AnimationTangentMode.Linear, WeightedMode = AnimationWeightedMode.Both,
            });
        var loaded = Serializer.Deserialize<AnimationCurve>(Serializer.Serialize(curve))!;
        Assert.Equal(count, loaded.Keys.Count);
        Assert.Equal(curve.PreLoop, loaded.PreLoop);
        Assert.Equal(curve.PostLoop, loaded.PostLoop);
        for (int i = 0; i < count; i++)
        {
            KeyFrame a = curve.Keys[i], b = loaded.Keys[i];
            Assert.Equal(a.Position, b.Position);
            Assert.Equal(a.Value, b.Value);
            Assert.Equal(a.TangentIn, b.TangentIn);
            Assert.Equal(a.TangentOut, b.TangentOut);
            Assert.Equal(a.Continuity, b.Continuity);
            Assert.Equal(a.InWeight, b.InWeight);
            Assert.Equal(a.OutWeight, b.OutWeight);
            Assert.Equal(a.InTangentMode, b.InTangentMode);
            Assert.Equal(a.OutTangentMode, b.OutTangentMode);
            Assert.Equal(a.WeightedMode, b.WeightedMode);
        }
    }

    [Fact]
    public void BinaryDecode_RejectsTruncatedKey()
    {
        var echo = EchoObject.NewCompound();
        echo.Add("PreLoop", new EchoObject(0));
        echo.Add("PostLoop", new EchoObject(0));
        echo.Add("KeysBinary", new EchoObject(new byte[39]));
        Assert.Throws<System.IO.InvalidDataException>(() => new AnimationCurve().Deserialize(echo, new SerializationContext()));
    }

    // PostLoop Linear extrapolation must use the LAST key's tangent, not the first's.
    [Fact]
    public void PostLoopLinear_UsesLastKeyTangent()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(10, 5) });
        c.PostLoop = CurveLoopType.Linear;
        c.Keys[0].TangentOut = 100f; // first key - must NOT be used past the end
        c.Keys[1].TangentOut = 2f;   // last key - the correct slope

        Assert.Equal(7f, c.Evaluate(11f), 3); // last.Value(5) + last.TangentOut(2) * 1
    }

    // Step continuity holds the previous key's value across the whole segment, not until a magic 1.0.
    [Fact]
    public void StepContinuity_HoldsPreviousValueAcrossSegment()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(5, 1), new KeyFrame(10, 2) });
        c.Keys[1].Continuity = CurveContinuity.Step; // governs the [5,10] segment

        Assert.Equal(1f, c.Evaluate(7f), 3); // holds key@5's value until reaching key@10
    }

    // Assigning through the indexer to a new position must keep keys sorted (sorted insert, not append).
    [Fact]
    public void IndexerAssignment_KeepsKeysSorted()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(5, 5), new KeyFrame(10, 10) });
        c.Keys[0] = new KeyFrame(7, 7); // move first key to position 7

        for (int i = 1; i < c.Keys.Count; i++)
            Assert.True(c.Keys[i - 1].Position <= c.Keys[i].Position, "Keys must remain sorted by position.");
    }

    // Cycle looping must floor the cycle count (no double-floor at exact negative integer boundaries).
    [Fact]
    public void CyclePreLoop_AtExactBoundary_MapsToStart()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(1, 10) });
        c.PreLoop = CurveLoopType.Cycle;

        // One full period below the start maps back to the start value (0), not the end value (10).
        Assert.Equal(0f, c.Evaluate(-1.0f), 3);
    }

    [Fact]
    public void ConstantOutMode_HoldsPreviousValue()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(1, 10) });
        c.Keys[0].OutTangentMode = AnimationTangentMode.Constant;
        c.Keys[0].InTangentMode = AnimationTangentMode.Free;
        c.ApplyTangentModes(0);

        Assert.Equal(0f, c.Evaluate(0.5f), 3);
        Assert.Equal(10f, c.Evaluate(1f), 3);
    }

    [Fact]
    public void ClampedAuto_ZerosTangentAtLocalExtremum()
    {
        var c = new AnimationCurve(new[]
        {
            new KeyFrame(0, 0),
            new KeyFrame(0.5f, 5),
            new KeyFrame(1, 0),
        });
        c.Keys[1].InTangentMode = AnimationTangentMode.ClampedAuto;
        c.Keys[1].OutTangentMode = AnimationTangentMode.ClampedAuto;
        c.ApplyTangentModes(1);

        Assert.Equal(0f, c.Keys[1].TangentIn);
        Assert.Equal(0f, c.Keys[1].TangentOut);
    }

    [Fact]
    public void WeightedOut_PullsMidpointTowardOutgoingHandle()
    {
        var unweighted = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(1, 0) });
        unweighted.Keys[0].TangentOut = 6f;
        unweighted.Keys[1].TangentIn = 0f;
        float mid = unweighted.Evaluate(0.5f);

        var weighted = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(1, 0) });
        weighted.Keys[0].TangentOut = 6f;
        weighted.Keys[0].WeightedMode = AnimationWeightedMode.Out;
        weighted.Keys[0].OutWeight = 0.5f;
        weighted.Keys[1].TangentIn = 0f;
        float midW = weighted.Evaluate(0.5f);

        Assert.True(midW > mid, $"weighted {midW} should exceed unweighted {mid}");
    }

    [Fact]
    public void SerializeRoundTrip_PreservesTangentModeAndWeight()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 0), new KeyFrame(1, 2) });
        c.Keys[0].OutTangentMode = AnimationTangentMode.Linear;
        c.Keys[0].WeightedMode = AnimationWeightedMode.Out;
        c.Keys[0].OutWeight = 0.5f;
        c.ApplyTangentModes(0);

        EchoObject echo = Serializer.Serialize(c, TypeMode.Aggressive);
        var loaded = Serializer.Deserialize<AnimationCurve>(echo);
        Assert.NotNull(loaded);
        Assert.Equal(AnimationTangentMode.Linear, loaded!.Keys[0].OutTangentMode);
        Assert.Equal(AnimationWeightedMode.Out, loaded.Keys[0].WeightedMode);
        Assert.Equal(0.5f, loaded.Keys[0].OutWeight);
        Assert.Equal(AnimationTangentMode.Free, loaded.Keys[0].InTangentMode);
    }

    [Fact]
    public void Deserialize_OldKeyWithoutModes_DefaultsToFree()
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, 1), new KeyFrame(1, 3) });
        EchoObject echo = Serializer.Serialize(c, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.DoesNotContain("InTangentMode", text);

        var loaded = Serializer.Deserialize<AnimationCurve>(EchoObject.ReadFromString(text));
        Assert.NotNull(loaded);
        Assert.Equal(AnimationTangentMode.Free, loaded!.Keys[0].InTangentMode);
        Assert.Equal(AnimationTangentMode.Free, loaded.Keys[0].OutTangentMode);
        Assert.Equal(AnimationWeightedMode.None, loaded.Keys[0].WeightedMode);
    }
}
