// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

public sealed class AnimationClipExtractionServiceTests : EditorTestHarness
{
    [Fact]
    public void TryExtract_WritesStandaloneClipWithFreshGuidAndSameCurves()
    {
        var source = new AnimationClip
        {
            Name = "Walk/Forward",
            Duration = 1.5f,
            TicksPerSecond = 30f,
            DurationInTicks = 45f,
            Wrap = AnimationWrapMode.Loop,
            RootBonePath = "Hips",
            AssetPath = "Models/Hero.fbx",
            AssetID = Guid.NewGuid(),
        };
        var bone = new AnimationClip.AnimBone { BoneName = "Hips" };
        bone.PosX = new AnimationCurve(new[]
        {
            new KeyFrame(0f, 0f),
            new KeyFrame(0.5f, 1.25f),
            new KeyFrame(1.5f, 4f),
        });
        bone.PosY = new AnimationCurve(new[] { new KeyFrame(0f, 2f) });
        bone.PosZ = new AnimationCurve(new[] { new KeyFrame(0f, -3f) });
        source.AddBone(bone);

        Guid sourceGuid = source.AssetID;
        bool ok = AnimationClipExtractionService.TryExtract(
            Assets,
            source,
            "Models/Hero.fbx",
            sourceGuid,
            AnimationClipExtractionService.DefaultOutputFolderName,
            out AnimationClipExtractionService.ExtractionResult? result,
            out string? error);

        Assert.True(ok, error);
        Assert.NotNull(result);
        Assert.NotEqual(sourceGuid, result!.ExtractedGuid);
        Assert.Equal("Models/Hero.fbx", result.SourcePath);
        Assert.Equal("Models/Animations/Walk_Forward.anim", result.ExtractedPath);
        Assert.Equal("Walk/Forward", result.ClipName);

        string absolute = AssetAbsolutePath(result.ExtractedPath);
        Assert.True(File.Exists(absolute));
        Assert.True(File.Exists(MetaFile.GetMetaPath(absolute)));

        var loaded = Assert.IsType<AnimationClip>(Assets.Get(result.ExtractedGuid));
        Assert.Equal(result.ExtractedGuid, loaded.AssetID);
        Assert.Equal(result.ExtractedPath, loaded.AssetPath);
        Assert.Equal(source.Name, loaded.Name);
        Assert.Equal(source.Duration, loaded.Duration);
        Assert.Equal(source.TicksPerSecond, loaded.TicksPerSecond);
        Assert.Equal(source.DurationInTicks, loaded.DurationInTicks);
        Assert.Equal(source.Wrap, loaded.Wrap);
        Assert.Equal(source.RootBonePath, loaded.RootBonePath);

        AnimationClip.AnimBone copiedBone = Assert.Single(loaded.Bones);
        Assert.Equal("Hips", copiedBone.BoneName);
        Assert.Equal(3, copiedBone.PosX.Keys.Count);
        Assert.Equal(1.25f, copiedBone.PosX.Keys[1].Value);
        Assert.Equal(4f, copiedBone.PosX.Keys[2].Value);
        Assert.Equal(2f, copiedBone.PosY.Keys[0].Value);
        Assert.Equal(-3f, copiedBone.PosZ.Keys[0].Value);

        AssetEntry? entry = Assets.GetEntry(result.ExtractedGuid);
        Assert.NotNull(entry);
        Assert.DoesNotContain(sourceGuid, entry!.Dependencies);
        Assert.DoesNotContain("Hero.fbx", loaded.AssetPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractControllerClips_SkipsAlreadyStandaloneReferences()
    {
        var clip = new AnimationClip { Name = "Idle", Duration = 1f };
        Assets.CreateAsset(clip, "Models/Animations/Idle.anim");

        var controller = new AnimatorController { Name = "Hero" };
        controller.States.Add(new AnimatorController.State
        {
            Name = "Idle",
            Motion = new AssetRef<AnimationClip>(clip),
        });

        var batch = AnimationClipExtractionService.ExtractControllerClips(Assets, controller);

        Assert.Empty(batch.Extracted);
        Assert.Empty(batch.Errors);
        Assert.Equal("extracted=0 errors=0", batch.ToString());
        Assert.Equal(clip.AssetID, Assets.PathToGuid("Models/Animations/Idle.anim"));
    }

    [Fact]
    public void RemapControllerClipReferences_ChangesOnlyMappedSlots()
    {
        Guid sourceGuid = Guid.NewGuid();
        Guid replacementGuid = Guid.NewGuid();
        Guid untouchedGuid = Guid.NewGuid();

        var controller = new AnimatorController { Name = "Hero" };
        var state = new AnimatorController.State
        {
            Name = "Walk",
            Motion = new AssetRef<AnimationClip>(sourceGuid),
        };
        state.Motions.Add(new AssetRef<AnimationClip>(untouchedGuid));
        controller.States.Add(state);

        var mapping = new AnimationClipExtractionService.ExtractionResult(
            sourceGuid,
            replacementGuid,
            "Models/Hero.fbx",
            "Models/Animations/Walk.anim",
            "Walk");

        int changed = AnimationClipExtractionService.RemapControllerClipReferences(
            controller, new[] { mapping });

        Assert.Equal(1, changed);
        Assert.Equal(replacementGuid, controller.States[0].Motion.AssetID);
        Assert.Equal(untouchedGuid, controller.States[0].Motions[0].AssetID);
    }
}
