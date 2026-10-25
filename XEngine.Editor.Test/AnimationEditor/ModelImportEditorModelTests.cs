// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.AnimationEditor;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>
/// Unity Model Import Settings: Animation-tab clip preview rules and clip frame range.
/// </summary>
public class ModelImportEditorModelTests
{
    [Fact]
    public void ShouldPreviewClip_WithClipsOnAnyTab()
    {
        // Unity parity: selecting an animated FBX animates the preview regardless of the
        // active Model/Rig/Animation/Materials tab; only clip presence and the import
        // flag gate it.
        Assert.True(ModelImportEditorModel.ShouldPreviewClip(ModelImportTab.Animation, true, 3));
        Assert.True(ModelImportEditorModel.ShouldPreviewClip(ModelImportTab.Model, true, 3));
        Assert.True(ModelImportEditorModel.ShouldPreviewClip(ModelImportTab.Rig, true, 3));
        Assert.True(ModelImportEditorModel.ShouldPreviewClip(ModelImportTab.Materials, true, 3));
        Assert.False(ModelImportEditorModel.ShouldPreviewClip(ModelImportTab.Animation, false, 3));
        Assert.False(ModelImportEditorModel.ShouldPreviewClip(ModelImportTab.Animation, true, 0));
    }

    [Fact]
    public void ClampClipIndex_StaysInRange()
    {
        Assert.Equal(-1, ModelImportEditorModel.ClampClipIndex(0, 0));
        Assert.Equal(0, ModelImportEditorModel.ClampClipIndex(-4, 5));
        Assert.Equal(4, ModelImportEditorModel.ClampClipIndex(99, 5));
        Assert.Equal(2, ModelImportEditorModel.ClampClipIndex(2, 5));
    }

    [Fact]
    public void ClipEndFrame_PrefersDurationInTicks()
    {
        var clip = new AnimationClip { Name = "atk", Duration = 1f, TicksPerSecond = 30f, DurationInTicks = 32f };
        Assert.Equal(0f, ModelImportEditorModel.ClipStartFrame(clip));
        Assert.Equal(32f, ModelImportEditorModel.ClipEndFrame(clip));
    }

    [Fact]
    public void ClipEndFrame_FallsBackToDurationTimesFps()
    {
        var clip = new AnimationClip { Name = "walk", Duration = 2f, TicksPerSecond = 30f, DurationInTicks = 0f };
        Assert.Equal(60f, ModelImportEditorModel.ClipEndFrame(clip), 2);
    }
}
