// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

public class AnimationWindowBindingTests
{
    [Fact]
    public void FindAnimationRoot_WalksToAnimatorParent()
    {
        var root = new GameObject("Rig");
        root.AddComponent<Animator>();
        var bone = new GameObject("Hips");
        bone.SetParent(root);

        Assert.Same(root, AnimationWindowBinding.FindAnimationRoot(bone));
        Assert.Same(root, AnimationWindowBinding.FindPreviewTarget(bone));
    }

    [Fact]
    public void FindAnimationRoot_CubeIsCreatePromptTarget_ButNotPreviewable()
    {
        var cube = new GameObject("Cube");
        Assert.Same(cube, AnimationWindowBinding.FindAnimationRoot(cube));
        Assert.Null(AnimationWindowBinding.FindPreviewTarget(cube));
        Assert.False(AnimationWindowBinding.HasAnimationComponent(cube));
    }

    [Fact]
    public void CollectClips_FromAnimatorClipAndController()
    {
        var walk = new AnimationClip { Name = "Walk", Duration = 1f };
        var idle = new AnimationClip { Name = "Idle", Duration = 1f };
        var ctrl = AnimatorComponentSetup.CreateControllerForClip(idle, "Hero");
        var go = new GameObject("Hero");
        var animator = go.AddComponent<Animator>();
        animator.Clip = new AssetRef<AnimationClip>(walk);
        animator.Controller = new AssetRef<AnimatorController>(ctrl);

        var clips = new List<AnimationClip>();
        AnimationWindowBinding.CollectClips(go, clips);
        Assert.Equal(2, clips.Count);
        Assert.Contains(walk, clips);
        Assert.Contains(idle, clips);
    }

    [Fact]
    public void CollectClips_FromAnimationComponent()
    {
        var a = new AnimationClip { Name = "A", Duration = 1f };
        var b = new AnimationClip { Name = "B", Duration = 1f };
        var go = new GameObject("Legacy");
        var legacy = go.AddComponent<AnimationComponent>();
        legacy.DefaultClip = new AssetRef<AnimationClip>(a);
        legacy.Clips.Add(new AssetRef<AnimationClip>(b));

        var clips = new List<AnimationClip>();
        AnimationWindowBinding.CollectClips(go, clips);
        Assert.Equal(2, clips.Count);
        Assert.Contains(a, clips);
        Assert.Contains(b, clips);
    }

    [Fact]
    public void IsReadOnlyClip_ModelSubAssetVsStandaloneAnim()
    {
        Assert.False(AnimationWindowBinding.IsReadOnlyClip(new AnimationClip { Name = "mem" }));
        Assert.False(AnimationWindowBinding.IsReadOnlyClip(new AnimationClip { Name = "walk", AssetPath = "Walk.anim" }));
        Assert.True(AnimationWindowBinding.IsReadOnlyClip(new AnimationClip
        {
            Name = "Take",
            AssetPath = "Hero.glb",
            AssetID = Guid.NewGuid(),
        }));
    }

    [Fact]
    public void AssignClipToRoot_AddsAnimatorAndController()
    {
        var go = new GameObject("Cube");
        var clip = AnimationWindowBinding.CreateEmptyClip("New Clip");

        AnimationWindowBinding.AssignClipToRoot(go, clip);

        var animator = go.GetComponent<Animator>();
        Assert.NotNull(animator);
        Assert.Same(clip, animator!.Clip.Res);
        Assert.NotNull(animator.Controller.Res);
        Assert.Same(clip, animator.Controller.Res!.States[0].Motion.Res);
    }

    [Fact]
    public void AssignClipToRoot_AppendsToLegacyClips()
    {
        var go = new GameObject("Legacy");
        var existing = new AnimationClip { Name = "Old", Duration = 1f };
        var legacy = go.AddComponent<AnimationComponent>();
        legacy.DefaultClip = new AssetRef<AnimationClip>(existing);
        var neu = AnimationWindowBinding.CreateEmptyClip("Extra");

        AnimationWindowBinding.AssignClipToRoot(go, neu);

        Assert.Same(existing, legacy.DefaultClip.Res);
        Assert.Contains(legacy.Clips, r => ReferenceEquals(r.Res, neu));
    }

    [Fact]
    public void Window_Bind_ExposesRootAndClip()
    {
        var go = new GameObject("Rig");
        go.AddComponent<Animator>();
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        var w = new AnimationClipWindow();
        w.Bind(go, clip);

        Assert.Same(go, w.CurrentRoot);
        Assert.Same(clip, w.CurrentClip);
        Assert.False(w.Model.NeedsCreatePrompt);
        w.AddPropertyPopupOpen = true;
        Assert.True(w.AddPropertyPopupOpen);
    }

    [Fact]
    public void Window_Toolbar_PreviewRecordFrameAndSampleRate()
    {
        var go = new GameObject("Rig");
        go.AddComponent<Animator>();
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        var w = new AnimationClipWindow();
        try
        {
            w.Bind(go, clip);

            w.PlaybackFrame = 15;
            Assert.Equal(15, w.PlaybackFrame);
            Assert.Equal(0.5f, w.PlaybackTime, 3);

            w.SampleRate = 60f;
            Assert.Equal(60f, clip.TicksPerSecond);
            Assert.Equal(60f, w.SampleRate);

            w.IsRecording = true;
            Assert.True(w.IsRecording);
            Assert.False(w.IsPreviewPlaying);
            w.IsPreviewPlaying = true;
            Assert.True(w.IsPreviewPlaying);
            Assert.False(w.IsRecording);

            Assert.True(w.RequestAddEvent());
            Assert.Equal(w.PlaybackTime, w.LastAddEventTime);
            Assert.Equal(1, w.ClipEventCount);
            Assert.Equal(0, w.SelectedEventIndex);
            Assert.True(w.SetSelectedEventFunction("Footstep"));
            Assert.Equal("Footstep", w.CurrentClip!.Events[0].FunctionName);
            Assert.True(w.RemoveSelectedEvent());
            Assert.Equal(0, w.ClipEventCount);

            w.ShowTimeInFrames = false;
            Assert.False(w.ShowTimeInFrames);
        }
        finally
        {
            w.OnClosed();
        }
    }

    [Fact]
    public void DuplicateClip_ClearsAssetIdentity()
    {
        var src = new AnimationClip { Name = "Take", Duration = 1.5f, AssetPath = "Hero.glb" };
        src.AssetID = Guid.NewGuid();
        src.AddBone(new AnimationClip.AnimBone { BoneName = "Hips" });

        var copy = AnimationWindowBinding.DuplicateClip(src);
        Assert.Equal(Guid.Empty, copy.AssetID);
        Assert.True(string.IsNullOrEmpty(copy.AssetPath));
        Assert.Equal("Take (edit)", copy.Name);
        Assert.False(AnimationWindowBinding.IsReadOnlyClip(copy));
        Assert.NotNull(copy.GetBone("Hips"));
    }

    [Fact]
    public void Window_CurveTangents_ShowCurvesAndPreset()
    {
        var go = new GameObject("Rig");
        go.AddComponent<Animator>();
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        var bone = new AnimationClip.AnimBone { BoneName = "Hips" };
        bone.PosX = new AnimationCurve(new[] { new KeyFrame(0f, 0f), new KeyFrame(1f, 10f) });
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        clip.AddBone(bone);
        var w = new AnimationClipWindow();
        w.Bind(go, clip);
        w.ShowCurves = true;
        Assert.True(w.ShowCurves);
        w.Model.SelectKeyframe("Hips", AnimationClipEditorModel.Channel.Position, 0, 0f, 0);
        Assert.True(w.ApplyTangentPreset("FreeSmooth"));
        Assert.True(w.Model.TryGetSelectedCurve(out var curve, out _));
        Assert.Equal(AnimationTangentMode.Free, curve.Keys[0].OutTangentMode);
        Assert.True(w.SetSelectedSideTangentMode(false, AnimationTangentMode.Linear));
        Assert.Equal(10f, curve.Keys[0].TangentOut);
    }
}
