// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using Xunit;

using XEngine.Animation;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M15 dynamic-loading dependency surface: <see cref="AnimatorController.GetClipReferences"/>
/// reports every clip a controller can reach (one per clip state, plus every BlendTree child), which is
/// what the asset-bundle packager and the pre-load/streaming path consume.
/// </summary>
public class AnimatorControllerDynamicLoadingTests
{
    private static AnimationClip Clip(string name) => new() { Name = name, Duration = 1f };

    [Fact]
    public void GetClipReferences_CollectsOnePerClipState()
    {
        var idle = Clip("idle");
        var attack = Clip("attack");
        var c = new AnimatorController { Name = "c" };
        c.States.Add(new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(idle) });
        c.States.Add(new AnimatorController.State { Name = "Attack", Motion = new AssetRef<AnimationClip>(attack) });

        var refs = c.GetClipReferences();

        Assert.Equal(2, refs.Count);
        Assert.Same(idle, refs[0].Res);
        Assert.Same(attack, refs[1].Res);
    }

    [Fact]
    public void GetClipReferences_IncludesBlendTreeChildren()
    {
        var bt = new BlendTree { Name = "loco", ParameterName = "Speed" };
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(Clip("idle")) });
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(Clip("walk")) });
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(Clip("run")) });

        var c = new AnimatorController { Name = "c" };
        c.States.Add(new AnimatorController.State { Name = "Loco", BlendTree = new AssetRef<BlendTree>(bt) });

        var refs = c.GetClipReferences();

        Assert.Equal(3, refs.Count);
        Assert.Equal("idle", refs[0].Res!.Name);
        Assert.Equal("walk", refs[1].Res!.Name);
        Assert.Equal("run", refs[2].Res!.Name);
    }

    [Fact]
    public void GetClipReferences_MixesClipAndBlendTreeStates()
    {
        var bt = new BlendTree { Name = "loco" };
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(Clip("idle")) });
        bt.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(Clip("run")) });

        var c = new AnimatorController { Name = "c" };
        c.States.Add(new AnimatorController.State { Name = "Loco", BlendTree = new AssetRef<BlendTree>(bt) });
        c.States.Add(new AnimatorController.State { Name = "Attack", Motion = new AssetRef<AnimationClip>(Clip("attack")) });

        var refs = c.GetClipReferences();

        // 2 BlendTree children + 1 clip state.
        Assert.Equal(3, refs.Count);
    }

    [Fact]
    public void GetClipReferences_EmptyController_ReturnsEmpty()
    {
        var refs = new AnimatorController().GetClipReferences();
        Assert.Empty(refs);
    }

    [Fact]
    public void GetClipReferences_ScansAllLayers_IgnoresRootStates()
    {
        var rootOnly = Clip("root-ignored");
        var baseClip = Clip("base");
        var overlayClip = Clip("overlay");

        var c = new AnimatorController { Name = "c" };
        c.States.Add(new AnimatorController.State { Name = "RootIdle", Motion = new AssetRef<AnimationClip>(rootOnly) });
        c.Layers.Add(new AnimatorController.Layer
        {
            Name = "Base Layer",
            DefaultStateName = "Idle",
            States = { new AnimatorController.State { Name = "Idle", Motion = new AssetRef<AnimationClip>(baseClip) } },
        });
        c.Layers.Add(new AnimatorController.Layer
        {
            Name = "UpperBody",
            DefaultStateName = "Wave",
            States = { new AnimatorController.State { Name = "Wave", Motion = new AssetRef<AnimationClip>(overlayClip) } },
        });

        var refs = c.GetClipReferences();

        Assert.Equal(2, refs.Count);
        Assert.Same(baseClip, refs[0].Res);
        Assert.Same(overlayClip, refs[1].Res);
    }
}
