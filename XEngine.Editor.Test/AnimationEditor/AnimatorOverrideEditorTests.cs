// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Animation;
using XEngine.Editor;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Importers;
using XEngine.Editor.Inspector;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>★ M44 — Override controller inspector + AvatarMask bone tree / humanoid model.</summary>
public class AnimatorOverrideEditorTests
{
    [Fact]
    public void OverrideController_HasCustomAssetEditorAndMenu()
    {
        EditorRegistries.Initialize();
        Assert.IsType<AnimatorOverrideControllerAssetEditor>(
            EditorRegistries.GetAssetEditor(typeof(AnimatorOverrideController)));
        Assert.IsType<ScriptableObjectImporter>(EditorRegistries.GetImporter(".overridecontroller"));

        var menu = typeof(AnimatorOverrideController).GetCustomAttribute<CreateAssetMenuAttribute>();
        Assert.NotNull(menu);
        Assert.Equal(".overridecontroller", menu!.Extension);
        Assert.Equal("Animation/Animator Override Controller", menu.Name);
    }

    [Fact]
    public void AvatarMask_BoneTree_IndentsByPath()
    {
        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine/Head", Weight = 0f });
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips", Weight = 1f });
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });

        var rows = AvatarMaskEditorModel.BuildBoneTree(mask.Entries);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Hips", rows[0].Label);
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal("Spine", rows[1].Label);
        Assert.Equal(1, rows[1].Depth);
        Assert.Equal("Head", rows[2].Label);
        Assert.Equal(2, rows[2].Depth);
        Assert.Equal(1, rows[0].EntryIndex);
    }
}
