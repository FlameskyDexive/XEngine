// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Inspector;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M17: Animator / animation asset CustomEditors, CreateAssetMenu, and Undo.Snapshot on controllers.
/// </summary>
public class AnimationCustomEditorTests : EditorTestHarness
{
    public AnimationCustomEditorTests()
    {
        EditorRegistries.Initialize();
        Undo.Clear();
    }

    [Fact]
    public void Animator_HasCustomEditor()
    {
        CustomEditor? editor = EditorRegistries.GetCustomEditor(typeof(Animator));
        Assert.NotNull(editor);
        Assert.IsType<AnimatorEditor>(editor);
    }

    [Fact]
    public void AnimationAssets_HaveCustomAssetEditors()
    {
        Assert.IsType<AnimatorControllerAssetEditor>(EditorRegistries.GetAssetEditor(typeof(AnimatorController)));
        Assert.IsType<AnimationClipAssetEditor>(EditorRegistries.GetAssetEditor(typeof(AnimationClip)));
        Assert.IsType<BlendTreeAssetEditor>(EditorRegistries.GetAssetEditor(typeof(BlendTree)));
        Assert.IsType<AvatarMaskAssetEditor>(EditorRegistries.GetAssetEditor(typeof(AvatarMask)));
        Assert.IsType<AnimatorOverrideControllerAssetEditor>(
            EditorRegistries.GetAssetEditor(typeof(AnimatorOverrideController)));
    }

    [Fact]
    public void CreateAssetMenu_BlendTreeAndAvatarMask()
    {
        var blend = typeof(BlendTree).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(blend);
        Assert.Equal(".motion", blend!.Extension);
        Assert.Equal("Animation/Blend Tree", blend.Name);

        var mask = typeof(AvatarMask).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(mask);
        Assert.Equal(".mask", mask!.Extension);
        Assert.Equal("Animation/Avatar Mask", mask.Name);

        var controller = typeof(AnimatorController).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(controller);
        Assert.Equal(".controller", controller!.Extension);

        var ov = typeof(AnimatorOverrideController).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(ov);
        Assert.Equal(".overridecontroller", ov!.Extension);
    }

    [Fact]
    public void UndoSnapshot_RestoresAnimatorController()
    {
        var controller = new AnimatorController { DefaultStateName = "Idle" };
        controller.States.Add(new AnimatorController.State { Name = "Idle" });

        Undo.Snapshot(controller);
        controller.DefaultStateName = "Walk";
        controller.States.Add(new AnimatorController.State { Name = "Walk" });
        Undo.IncrementGroup();

        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal("Idle", controller.DefaultStateName);
        Assert.Single(controller.States);
        Assert.Equal("Idle", controller.States[0].Name);
    }

    [Fact]
    public void BlendTreeAndAvatarMask_EchoRoundTrip()
    {
        var tree = new BlendTree { ParameterName = "Speed", Type = BlendType.OneD };
        tree.Children.Add(new BlendTree.Child { Threshold = 0.5f });
        EchoObject treeEcho = Serializer.Serialize(typeof(object), tree);
        var tree2 = Assert.IsType<BlendTree>(Serializer.Deserialize(treeEcho, typeof(object)));
        Assert.Equal("Speed", tree2.ParameterName);
        Assert.Equal(BlendType.OneD, tree2.Type);
        Assert.Single(tree2.Children);
        Assert.Equal(0.5f, tree2.Children[0].Threshold);

        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips", Weight = 1f });
        EchoObject maskEcho = Serializer.Serialize(typeof(object), mask);
        var mask2 = Assert.IsType<AvatarMask>(Serializer.Deserialize(maskEcho, typeof(object)));
        Assert.Single(mask2.Entries);
        Assert.Equal("Hips", mask2.Entries[0].Path);
        Assert.Equal(1f, mask2.Entries[0].Weight);
    }
}
