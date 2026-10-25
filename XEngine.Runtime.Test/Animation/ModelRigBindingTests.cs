// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;

using Xunit;

namespace XEngine.Runtime.Test.Animation;

public class ModelRigBindingTests : RuntimeTestBase
{
    private static AnimationClip MakeClip(string name)
        => new() { Name = name, Duration = 1f, TicksPerSecond = 30f };

    [Fact]
    public void Generic_AttachesAnimatorWithFirstClip()
    {
        var go = CreateGameObject("generic");
        var clip = MakeClip("Walk");
        ModelRigBinding.Attach(go, [clip], ModelRigBinding.Generic);

        var animator = go.GetComponent<Animator>();
        Assert.NotNull(animator);
        Assert.Same(clip, animator.Clip.Res);
        Assert.Null(animator.Controller.Res);
        Assert.Null(go.GetComponent<AnimationComponent>());
    }

    [Fact]
    public void Humanoid_AttachesAnimatorLikeGeneric()
    {
        var go = CreateGameObject("humanoid");
        var clip = MakeClip("Idle");
        ModelRigBinding.Attach(go, [clip], ModelRigBinding.Humanoid);

        Assert.NotNull(go.GetComponent<Animator>());
        Assert.Null(go.GetComponent<AnimationComponent>());
    }

    [Fact]
    public void Legacy_AttachesAnimationComponentWithClipList()
    {
        var go = CreateGameObject("legacy");
        var walk = MakeClip("Walk");
        var run = MakeClip("Run");
        ModelRigBinding.Attach(go, [walk, run], ModelRigBinding.Legacy);

        var anim = go.GetComponent<AnimationComponent>();
        Assert.NotNull(anim);
        Assert.Same(walk, anim.DefaultClip.Res);
        Assert.Equal(2, anim.Clips.Count);
        Assert.Same(run, anim.Clips[1].Res);
        Assert.Null(go.GetComponent<Animator>());
    }

    [Fact]
    public void NoClips_AttachesNothing()
    {
        var go = CreateGameObject("empty");
        ModelRigBinding.Attach(go, [], ModelRigBinding.Generic);
        Assert.Null(go.GetComponent<Animator>());
        Assert.Null(go.GetComponent<AnimationComponent>());
    }

    [Fact]
    public void RoundTrip_LegacyComponent_SurvivesDeserialize()
    {
        var go = CreateGameObject("old");
        var clip = MakeClip("LegacyWalk");
        ModelRigBinding.Attach(go, [clip], ModelRigBinding.Legacy);

        EchoObject echo = Serializer.Serialize(typeof(object), go);
        var restored = Serializer.Deserialize<GameObject>(echo);
        Assert.NotNull(restored);
        Assert.NotNull(restored.GetComponent<AnimationComponent>());
        Assert.Null(restored.GetComponent<Animator>());
    }
}
