// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;
using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M17b: Inspector animation preview — previewable types, skeleton fallback, empty-state copy.
/// </summary>
public class AnimationPreviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDoesNotPublishEditsWhenRestoringDirtyState(bool dirty)
    {
        var session = new AnimationPreviewSession();
        using var go = new GameObject("PreviewDirtyState");
        go.AddComponent<Animator>().PlayAutomatically = false;
        var clip = new AnimationClip { Name = "preview", Duration = 1f };
        bool previousDirty = EditorSceneManager.IsDirty;
        try
        {
            EditorSceneManager.IsDirty = dirty;
            long version = EditorSceneManager.ChangeVersion;
            for (int i = 0; i < 20; i++) session.Sync(clip, i / 20f, go);
            Assert.Equal(dirty, EditorSceneManager.IsDirty);
            Assert.Equal(version, EditorSceneManager.ChangeVersion);
            session.Stop();
            Assert.Equal(dirty, EditorSceneManager.IsDirty);
            Assert.Equal(version, EditorSceneManager.ChangeVersion);
            // Real edits must still invalidate a scene that is already dirty.
            EditorSceneManager.MarkDirty();
            Assert.Equal(version + 1, EditorSceneManager.ChangeVersion);
        }
        finally
        {
            session.Stop();
            EditorSceneManager.IsDirty = previousDirty;
        }
    }

    [Fact]
    public void IsPreviewable_IncludesClipsControllersAndSubAssetTypes()
    {
        Assert.True(AnimationPreviewAvatar.IsPreviewable(new AnimationClip()));
        Assert.True(AnimationPreviewAvatar.IsPreviewable(new AnimatorController()));
        Assert.True(AnimationPreviewAvatar.IsPreviewable(new BlendTree()));
        Assert.True(AnimationPreviewAvatar.IsPreviewable(new AvatarMask()));
        Assert.True(AnimationPreviewAvatar.IsAnimationPreview(new AnimationClip()));
        Assert.False(AnimationPreviewAvatar.IsAnimationPreview(new Mesh()));
    }

    [Fact]
    public void EmptyPreviewCopy_MatchesUnityDragHint()
    {
        Assert.Contains("No model is available for preview", AnimationEditorChrome.EmptyPreview);
        Assert.Contains("drag a model", AnimationEditorChrome.EmptyPreview);
    }

    [Fact]
    public void BuildSkeletonSubject_CreatesBoneHierarchy()
    {
        var clip = new AnimationClip { Name = "Walk", Duration = 1f };
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "Hips" });
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "Hips/Spine" });
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "Hips/Spine/Head" });

        GameObject root = AnimationPreviewAvatar.BuildSkeletonSubject(clip);
        Assert.Equal("PreviewSkeleton", root.Name);
        Assert.Single(root.Children);
        GameObject hips = root.Children[0];
        Assert.Equal("Hips", hips.Name);
        Assert.Equal("Spine", hips.Children[0].Name);
        Assert.Equal("Head", hips.Children[0].Children[0].Name);
    }

    [Fact]
    public void Animator_EvaluateGraph_DoesNotThrowWithoutRootMotion()
    {
        var go = new GameObject("Preview");
        var hips = new GameObject("Hips");
        hips.SetParent(go);
        var anim = go.AddComponent<Animator>();
        anim.PlayAutomatically = false;
        anim.ApplyRootMotion = false;
        anim.EvaluateGraph(0.016f);
        Assert.False(anim.ApplyRootMotion);
    }

    [Fact]
    public void FindPreviewTarget_ReturnsSelectedAnimatorObject()
    {
        var go = new GameObject("Char");
        go.AddComponent<Animator>();
        Selection.Select(go);

        Assert.Same(go, AnimationPreviewSession.FindPreviewTarget());
        Selection.Clear();
    }

    [Fact]
    public void Sync_SamplesClipOntoBonesAndRestoresBindPose()
    {
        var go = new GameObject("Char");
        go.AddComponent<Animator>().PlayAutomatically = false;
        var hips = new GameObject("Hips");
        hips.SetParent(go);
        hips.Transform.LocalPosition = new Float3(1f, 2f, 3f);
        var spine = new GameObject("Spine");
        spine.SetParent(hips);
        spine.Transform.LocalPosition = new Float3(0.5f, 0f, 0f);

        // The root bone (Hips) carries the bind pin — its translation curve is deliberately
        // overridden in-place (root-travel clips otherwise displace the skeleton and shred the
        // skinned mesh). Non-root bones under the 0.5 m travel threshold sample freely, so the
        // clip drives Spine by a small (limb-scale) offset.
        var clip = new AnimationClip { Name = "walk", Duration = 1f };
        var bone = new AnimationClip.AnimBone { BoneName = "Hips/Spine" };
        bone.PosX = new AnimationCurve(new[] { new KeyFrame(0f, 0f), new KeyFrame(1f, 0.3f) });
        bone.PosX.SmoothTangents(CurveTangent.Linear);
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        clip.AddBone(bone);

        Selection.Select(go);
        EditorSceneManager.IsDirty = false;
        var session = AnimationPreviewSession.Instance;
        try
        {
            session.Sync(clip, 1f);
            Assert.True(session.IsActive);
            Assert.Equal(0.3f, spine.Transform.LocalPosition.X, 2);
            Assert.Equal(1f, hips.Transform.LocalPosition.X, 2);
            Assert.False(EditorSceneManager.IsDirty);

            session.Stop();
            Assert.False(session.IsActive);
            Assert.Equal(1f, hips.Transform.LocalPosition.X, 2);
            Assert.Equal(2f, hips.Transform.LocalPosition.Y, 2);
            Assert.Equal(3f, hips.Transform.LocalPosition.Z, 2);
            Assert.Equal(0.5f, spine.Transform.LocalPosition.X, 2);
            Assert.False(EditorSceneManager.IsDirty);
        }
        finally
        {
            session.Stop();
            Selection.Clear();
        }
    }

    [Fact]
    public void Sync_WithoutTarget_DoesNotStart()
    {
        Selection.Clear();
        var clip = new AnimationClip { Name = "walk", Duration = 1f };
        AnimationPreviewSession.Instance.Sync(clip, 0f);
        Assert.False(AnimationPreviewSession.Instance.IsActive);
    }

    [Fact]
    public void Sync_UsesExplicitRootWhenSelectionEmpty()
    {
        Selection.Clear();
        var go = new GameObject("Char");
        go.AddComponent<Animator>().PlayAutomatically = false;
        var hips = new GameObject("Hips");
        hips.SetParent(go);
        hips.Transform.LocalPosition = new Float3(1f, 2f, 3f);
        var spine = new GameObject("Spine");
        spine.SetParent(hips);
        spine.Transform.LocalPosition = new Float3(0.5f, 0f, 0f);

        var clip = new AnimationClip { Name = "walk", Duration = 1f };
        var bone = new AnimationClip.AnimBone { BoneName = "Hips/Spine" };
        bone.PosX = new AnimationCurve(new[] { new KeyFrame(0f, 0f), new KeyFrame(1f, 0.3f) });
        bone.PosX.SmoothTangents(CurveTangent.Linear);
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, 0f) });
        clip.AddBone(bone);

        var session = AnimationPreviewSession.Instance;
        try
        {
            session.Sync(clip, 1f, go);
            Assert.True(session.IsActive);
            Assert.Same(go, session.Target);
            Assert.Equal(0.3f, spine.Transform.LocalPosition.X, 2);
            Assert.Equal(1f, hips.Transform.LocalPosition.X, 2);
        }
        finally
        {
            session.Stop();
        }
    }
}
