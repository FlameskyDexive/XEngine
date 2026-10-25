// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Transitions;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M50 — transition library: (from → to) modifier resolution specificity (both keys > one > wildcard,
/// Order tie-break) and the PlayWithLibrary application of fade / start / speed overrides.
/// </summary>
public class TransitionLibraryTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildAnimator()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        return (scene, root, animator);
    }

    private static AnimationClip ClipNamed(string name, float posX = 5f)
        => AnimationTestHelpers.MakeConstantPosClip(name, "Hips", posX);

    [Fact]
    public void FindBest_PrefersBothKeys_ThenOne_ThenWildcard()
    {
        var lib = new TransitionLibraryAsset();
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { FromKey = "", ToKey = "", FadeDuration = 0.1f }); // wildcard
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { FromKey = "", ToKey = "Run", FadeDuration = 0.2f }); // to-only
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { FromKey = "Idle", ToKey = "Run", FadeDuration = 0.3f }); // exact

        Assert.Equal(0.3f, lib.FindBest("Idle", "Run")!.FadeDuration);
        Assert.Equal(0.2f, lib.FindBest("Walk", "Run")!.FadeDuration);
        Assert.Equal(0.1f, lib.FindBest("Walk", "Jump")!.FadeDuration);
        Assert.Null(new TransitionLibraryAsset().FindBest("A", "B"));
    }

    [Fact]
    public void FindBest_OrderBreaksTies()
    {
        var lib = new TransitionLibraryAsset();
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { ToKey = "Run", FadeDuration = 0.4f, Order = 2 });
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { ToKey = "Run", FadeDuration = 0.6f, Order = 1 });

        Assert.Equal(0.6f, lib.FindBest("Idle", "Run")!.FadeDuration);
    }

    [Fact]
    public void PlayWithLibrary_AppliesFadeStartSpeed()
    {
        var (scene, _, animator) = BuildAnimator();
        var lib = new TransitionLibraryAsset();
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier
        {
            FromKey = "Idle",
            ToKey = "Run",
            FadeDuration = 1.0f,
            NormalizedStartTime = 0.25f,
            Speed = 2f,
        });
        animator.TransitionLibrary = new AssetRef<TransitionLibraryAsset>(lib);

        animator.Play(ClipNamed("Idle"));
        var run = ClipNamed("Run");
        var state = animator.PlayWithLibrary(run);

        Assert.True(state.Weight < 1f, "library fade should be active");
        Assert.Equal(2f, state.Speed);
        Update(scene, frames: 1);
        Assert.True(state.NormalizedTime > 0.2f && state.NormalizedTime < 0.4f,
            $"start ~0.25 + one 2x frame, got {state.NormalizedTime}");
    }

    [Fact]
    public void PlayWithLibrary_NoMatch_SnapsLikePlainPlay()
    {
        var (scene, _, animator) = BuildAnimator();
        var lib = new TransitionLibraryAsset();
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { ToKey = "Run", FadeDuration = 1f });
        animator.TransitionLibrary = new AssetRef<TransitionLibraryAsset>(lib);

        var state = animator.PlayWithLibrary(ClipNamed("Jump"));

        Assert.Equal(1f, state.Weight);
        Assert.Equal(1f, state.Speed);
    }

    [Fact]
    public void PlayWithLibrary_UsesCurrentClipAsFromKey()
    {
        var (_, _, animator) = BuildAnimator();
        var lib = new TransitionLibraryAsset();
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier { FromKey = "Idle", ToKey = "Run", FadeDuration = 0.75f });
        animator.TransitionLibrary = new AssetRef<TransitionLibraryAsset>(lib);

        animator.Play(ClipNamed("Idle"));
        var state = animator.PlayWithLibrary(ClipNamed("Run")); // No explicit fromKey → current clip.

        Assert.True(state.Weight < 1f && state.IsFading);
    }

    [Fact]
    public void PlayWithLibrary_TransitionAsset_OverridesWrap()
    {
        var (_, _, animator) = BuildAnimator();
        var asset = new TransitionAsset { Name = "atk" };
        asset.Transition = new ClipTransition
        {
            Clip = new AssetRef<AnimationClip>(ClipNamed("Attack")),
            FadeDuration = 0f,
            Speed = 1f,
        };
        var lib = new TransitionLibraryAsset();
        lib.Modifiers.Add(new TransitionLibraryAsset.Modifier
        { FromKey = "Idle", ToKey = "Attack", FadeDuration = 1.0f, Speed = 2f });
        animator.TransitionLibrary = new AssetRef<TransitionLibraryAsset>(lib);

        animator.Play(ClipNamed("Idle"));
        animator.PlayWithLibrary(asset);

        Assert.Equal(1.0f, asset.Transition.FadeDuration);
        Assert.Equal(2f, asset.Transition.Speed);
    }
}
