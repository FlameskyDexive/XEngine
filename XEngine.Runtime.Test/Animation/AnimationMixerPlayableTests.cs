// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Playables;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="AnimationMixerPlayable"/>: normalized weighted blending of N inputs,
/// crossfade behaviour, zero-weight skipping, and the steady-state zero-allocation contract for a
/// full graph (mixer + clip playables + output writing to Transforms).
/// </summary>
public class AnimationMixerPlayableTests : RuntimeTestBase
{
    private static readonly PlayableContext Ctx = new(0f, 0d, false);

    private (SkeletonBinding binding, AnimationClipPlayable a, AnimationClipPlayable b, AnimationMixerPlayable mixer)
        BuildTwoClipMixer()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        // Clip A moves Hips.PosX to 0; Clip B moves it to 10. Same bone, different targets.
        var clipA = AnimationTestHelpers.MakeSingleBonePosXClip("a", "Hips", endX: 0f);
        var clipB = AnimationTestHelpers.MakeSingleBonePosXClip("b", "Hips", endX: 10f);

        var a = new AnimationClipPlayable(clipA);
        var b = new AnimationClipPlayable(clipB);
        a.Bind(binding);
        b.Bind(binding);
        a.Play();
        b.Play();
        // Hold at the end so the sampled value is the clip's terminal position regardless of dt.
        a.SetTime(1.0);
        b.SetTime(1.0);

        var mixer = new AnimationMixerPlayable();
        mixer.Bind(binding);
        mixer.AddInput(a);
        mixer.AddInput(b);
        return (binding, a, b, mixer);
    }

    private static void ProcessAll(AnimationMixerPlayable mixer)
    {
        // Post-order: children must be sampled before the mixer blends.
        for (int i = 0; i < mixer.InputCount; i++)
            mixer.GetInput(i).ProcessFrame(Ctx);
        mixer.ProcessFrame(Ctx);
    }

    [Fact]
    public void FullWeightOnA_YieldsAPose()
    {
        var (_, a, b, mixer) = BuildTwoClipMixer();
        a.Weight = 1f;
        b.Weight = 0f;

        ProcessAll(mixer);

        Assert.Equal(0f, mixer.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void EqualWeights_Average()
    {
        var (_, a, b, mixer) = BuildTwoClipMixer();
        a.Weight = 1f;
        b.Weight = 1f;

        ProcessAll(mixer);

        // Normalized 0.5 / 0.5 → (0 + 10) * 0.5 = 5.
        Assert.Equal(5f, mixer.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void Crossfade_Midpoint_Averages()
    {
        var (_, a, b, mixer) = BuildTwoClipMixer();
        // Halfway through an A->B crossfade: A weight 0.5, B weight 0.5.
        a.Weight = 0.5f;
        b.Weight = 0.5f;

        ProcessAll(mixer);

        Assert.Equal(5f, mixer.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void Crossfade_Complete_LandsOnB()
    {
        var (_, a, b, mixer) = BuildTwoClipMixer();
        a.Weight = 0f;
        b.Weight = 1f;

        ProcessAll(mixer);

        Assert.Equal(10f, mixer.Output!.Bones[0].LocalPosition.X);
    }

    [Fact]
    public void ZeroWeights_ProduceNoDrivenChannels()
    {
        var (_, a, b, mixer) = BuildTwoClipMixer();
        a.Weight = 0f;
        b.Weight = 0f;

        ProcessAll(mixer);

        Assert.Equal(PoseChannel.None, mixer.Output!.Bones[0].Channels);
    }

    [Fact]
    public void FullGraphEvaluate_IsSteadyStateZeroAllocation()
    {
        // Hot-path contract: a realistic graph (mixer + 2 clip playables + output writing to real
        // Transforms) must not allocate on the managed heap during Evaluate. PR0007 enforces this
        // statically; this test verifies it dynamically over 1000 frames.
        var (binding, a, b, mixer) = BuildTwoClipMixer();
        a.Weight = 0.5f;
        b.Weight = 0.5f;

        var graph = new PlayableGraph { Root = mixer };
        var output = new AnimationPlayableOutput { Source = mixer, Binding = binding };
        graph.AddOutput(output);

        graph.Evaluate(0.016f); // warm up JIT / one-time work.

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            graph.Evaluate(0.016f);
        long after = System.GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
