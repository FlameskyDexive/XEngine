// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.AnimationEditor;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

public class AnimatorComponentSetupTests
{
    [Fact]
    public void CreateControllerForClip_UsesClipAsDefaultState()
    {
        var clip = new AnimationClip { Name = "Walk", Duration = 1f };
        var c = AnimatorComponentSetup.CreateControllerForClip(clip);

        Assert.Equal("Walk", c.DefaultStateName);
        Assert.Single(c.States);
        Assert.Equal("Walk", c.States[0].Name);
        Assert.Same(clip, c.States[0].Motion.Res);
    }

    [Fact]
    public void ConvertLegacy_ReplacesAnimationComponentWithAnimator()
    {
        var go = new GameObject("rig");
        var clip = new AnimationClip { Name = "Idle", Duration = 1f };
        var legacy = go.AddComponent<AnimationComponent>();
        legacy.DefaultClip = new AssetRef<AnimationClip>(clip);

        var animator = AnimatorComponentSetup.ConvertLegacy(go);

        Assert.Same(animator, go.GetComponent<Animator>());
        Assert.Same(clip, animator.Clip.Res);
        Assert.Null(go.GetComponent<AnimationComponent>());
    }

    [Fact]
    public void ConvertLegacy_OnAnimatorOnly_LeavesAnimator()
    {
        var go = new GameObject("already");
        var animator = go.AddComponent<Animator>();
        var again = AnimatorComponentSetup.ConvertLegacy(go);
        Assert.Same(animator, again);
        Assert.Null(go.GetComponent<AnimationComponent>());
    }
}
