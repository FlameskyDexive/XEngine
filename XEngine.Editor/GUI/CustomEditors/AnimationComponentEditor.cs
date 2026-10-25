// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

[CustomEditor(typeof(AnimationComponent))]
public class AnimationComponentEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var anim = (AnimationComponent)target;
        if (EditorTheme.DefaultFont == null) return;

        Undo.SnapshotIfInteracting(anim, paper);
        DrawDefaultInspector(paper, id, anim);

        Origami.Separator(paper, $"{id}_mig_sep").Show();
        Origami.Label(paper, $"{id}_mig_note",
            "Legacy Animation component. Convert to Animator to use Mecanim controllers.").Show();
        Origami.Button(paper, $"{id}_convert", "Convert to Animator", () =>
        {
            Undo.Snapshot(anim.GameObject);
            AnimatorComponentSetup.ConvertLegacy(anim.GameObject);
        }).Show();
    }
}
