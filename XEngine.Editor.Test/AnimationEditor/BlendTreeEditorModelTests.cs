// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// M20: in-window 1D BlendTree create/sort/weights, and the compiler still eats the edited tree.
/// </summary>
public class BlendTreeEditorModelTests
{
    [Fact]
    public void SortChildrenByThreshold_OrdersAscending()
    {
        var tree = BlendTreeEditorModel.CreateInline("loco", "Speed");
        tree.Children.Add(new BlendTree.Child { Threshold = 1f });
        tree.Children.Add(new BlendTree.Child { Threshold = 0f });
        tree.Children.Add(new BlendTree.Child { Threshold = 0.5f });

        BlendTreeEditorModel.SortChildrenByThreshold(tree);

        Assert.Equal(0f, tree.Children[0].Threshold);
        Assert.Equal(0.5f, tree.Children[1].Threshold);
        Assert.Equal(1f, tree.Children[2].Threshold);
    }

    [Fact]
    public void ApplyAutomaticThresholds_SpacesZeroToOne()
    {
        var tree = BlendTreeEditorModel.CreateInline("loco", "Speed");
        tree.UseAutomaticThresholds = true;
        tree.Children.Add(new BlendTree.Child());
        tree.Children.Add(new BlendTree.Child());
        tree.Children.Add(new BlendTree.Child());
        BlendTreeEditorModel.ApplyAutomaticThresholds(tree);
        Assert.Equal(0f, tree.Children[0].Threshold);
        Assert.Equal(0.5f, tree.Children[1].Threshold);
        Assert.Equal(1f, tree.Children[2].Threshold);
    }

    [Fact]
    public void ComputeWeights1D_MatchesPlayableBracket()
    {
        var tree = BlendTreeEditorModel.CreateInline("loco", "Speed");
        tree.Children.Add(new BlendTree.Child { Threshold = 0f });
        tree.Children.Add(new BlendTree.Child { Threshold = 0.5f });
        tree.Children.Add(new BlendTree.Child { Threshold = 1f });
        var w = new float[3];

        BlendTreeEditorModel.ComputeWeights1D(tree, 0.25f, w);
        Assert.Equal(0.5f, w[0], 3);
        Assert.Equal(0.5f, w[1], 3);
        Assert.Equal(0f, w[2], 3);

        BlendTreeEditorModel.ComputeWeights1D(tree, -1f, w);
        Assert.Equal(1f, w[0], 3);

        BlendTreeEditorModel.ComputeWeights1D(tree, 2f, w);
        Assert.Equal(1f, w[2], 3);
    }

    [Fact]
    public void CreateBlendTreeState_AttachesInlineTree()
    {
        var c = new AnimatorController { Name = "c" };
        c.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        var window = new AnimatorWindow();
        window.SetController(c);
        window.CreateBlendTreeState(new Float2(40, 40));

        Assert.Single(c.States);
        BlendTree? tree = BlendTreeEditorModel.GetTree(c.States[0]);
        Assert.NotNull(tree);
        Assert.Equal(BlendType.OneD, tree!.Type);
        Assert.Equal("Speed", tree.ParameterName);

        window.OpenBlendTreeEditor(c.States[0].Name);
        Assert.Equal(c.States[0].Name, window.EditingBlendTreeState);
        window.CloseBlendTreeEditor();
        Assert.Null(window.EditingBlendTreeState);
    }

    [Fact]
    public void Compiler_StillCompilesEditedTree()
    {
        var idle = new AnimationClip { Name = "idle", Duration = 1f };
        var walk = new AnimationClip { Name = "walk", Duration = 1f };
        var tree = BlendTreeEditorModel.CreateInline("loco", "Speed");
        tree.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(walk), Threshold = 0.5f });
        tree.Children.Add(new BlendTree.Child { Clip = new AssetRef<AnimationClip>(idle), Threshold = 0f });
        BlendTreeEditorModel.SortChildrenByThreshold(tree);

        var author = new AnimatorController { Name = "c", DefaultStateName = "Loco" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        var state = new AnimatorController.State { Name = "Loco" };
        BlendTreeEditorModel.AttachInline(state, tree);
        author.States.Add(state);

        var compiled = AnimatorControllerCompiler.Compile(author);
        Assert.Equal(1, compiled.MotionKind[0]);
        Assert.Same(tree, compiled.MotionBlendTrees[0]);
        Assert.Equal(0, compiled.MotionBlendTreeParamIndex[0]);
        Assert.Equal(0f, compiled.MotionBlendTrees[0]!.Children[0].Threshold);
        Assert.Equal(0.5f, compiled.MotionBlendTrees[0]!.Children[1].Threshold);
    }
}
