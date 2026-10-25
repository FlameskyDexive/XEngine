// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// ★ M38 — Generic float bindings serialize beside Bones, sample through a pre-resolved slot table,
/// and stay allocation-free on the clip playable hot path (typed Light.Intensity setter).
/// </summary>
public class AnimationClipGenericCurveTests : RuntimeTestBase
{
    static AnimationClip MakeIntensityClip(float start, float end, float duration = 1f)
    {
        var clip = new AnimationClip { Name = "Lit", Duration = duration, Wrap = AnimationWrapMode.ClampForever };
        clip.GenericCurves.Add(new AnimationClip.GenericCurve
        {
            Path = "",
            TypeName = nameof(DirectionalLight),
            Property = "Intensity",
            Curve = AnimationTestHelpers.LinearCurve(start, end, duration),
        });
        return clip;
    }

    [Fact]
    public void Deserialize_OldClipWithoutGenericCurves_LeavesListEmpty()
    {
        var clip = new AnimationClip { Name = "Walk", Duration = 1f };
        clip.AddBone(new AnimationClip.AnimBone
        {
            BoneName = "Hips",
            PosX = AnimationTestHelpers.LinearCurve(0f, 10f, 1f),
            PosY = AnimationTestHelpers.ConstantCurve(0f),
            PosZ = AnimationTestHelpers.ConstantCurve(0f),
        });
        EchoObject echo = Serializer.Serialize(clip, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.DoesNotContain("GenericCurves", text);

        var loaded = Serializer.Deserialize<AnimationClip>(EchoObject.ReadFromString(text));
        Assert.NotNull(loaded);
        Assert.Empty(loaded!.GenericCurves);
        Assert.NotNull(loaded.GetBone("Hips"));
    }

    [Fact]
    public void SerializeRoundTrip_PreservesGenericCurve()
    {
        var clip = MakeIntensityClip(1f, 5f);
        EchoObject echo = Serializer.Serialize(clip, TypeMode.Aggressive);
        string text = echo.WriteToString();
        Assert.Contains("GenericCurves", text);
        Assert.Contains("Intensity", text);

        var loaded = Serializer.Deserialize<AnimationClip>(EchoObject.ReadFromString(text));
        Assert.NotNull(loaded);
        AnimationClip.GenericCurve g = Assert.Single(loaded!.GenericCurves);
        Assert.Equal("", g.Path);
        Assert.Equal(nameof(DirectionalLight), g.TypeName);
        Assert.Equal("Intensity", g.Property);
        Assert.Equal(5f, g.Curve.Evaluate(1f));
    }

    [Fact]
    public void ProcessFrame_WritesDirectionalLightIntensity()
    {
        var root = CreateGameObject("Lamp");
        var light = root.AddComponent<DirectionalLight>();
        light.Intensity = 1f;
        var binding = SkeletonBinding.Build(root.Transform);

        var playable = new AnimationClipPlayable(MakeIntensityClip(1f, 5f));
        playable.Bind(binding);
        playable.Play();
        playable.Weight = 1f;
        playable.SetTime(1.0);
        playable.ProcessFrame(new PlayableContext(0f, 0d, false));

        Assert.Equal(5f, light.Intensity);
    }

    [Fact]
    public void ProcessFrame_GenericIntensity_IsSteadyStateZeroAllocation()
    {
        var root = CreateGameObject("Lamp");
        var light = root.AddComponent<DirectionalLight>();
        light.Intensity = 1f;
        var binding = SkeletonBinding.Build(root.Transform);

        var playable = new AnimationClipPlayable(MakeIntensityClip(1f, 5f));
        playable.Bind(binding);
        playable.Play();
        playable.Weight = 1f;

        var graph = new PlayableGraph { Root = playable };
        var output = new AnimationPlayableOutput { Source = playable, Binding = binding };
        graph.AddOutput(output);

        graph.Evaluate(0.016f);

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            graph.Evaluate(0.016f);
        long after = System.GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.True(light.Intensity > 1f);
    }
}
