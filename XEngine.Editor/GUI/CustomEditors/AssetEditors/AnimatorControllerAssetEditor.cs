// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

[CustomAssetEditor(typeof(AnimatorController))]
public class AnimatorControllerAssetEditor : AssetImporterEditor
{
    private Guid _currentGuid;
    private bool _dirty;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (_currentGuid != entry.Guid)
        {
            _currentGuid = entry.Guid;
            _dirty = false;
        }

        id = $"{id}_{entry.Guid:N}";
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Animator Controller").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var controller = asset as AnimatorController;
        if (controller == null)
        {
            Origami.Label(paper, $"{id}_none", "AnimatorController failed to load.").Show();
            return;
        }

        EditorGUI.Row(paper, $"{id}_def", "Default State", () =>
            Origami.TextField(paper, $"{id}_def_v", controller.DefaultStateName, v =>
            {
                controller.DefaultStateName = v ?? "";
                _dirty = true;
            }).Show());

        Origami.Label(paper, $"{id}_sum",
            $"States: {controller.States.Count}   Parameters: {controller.Parameters.Count}   Transitions: {controller.Transitions.Count}").Show();

        var parameters = controller.Parameters;
        int n = parameters.Count;
        if (n > 0)
        {
            Origami.Header(paper, $"{id}_ph", "Parameters").Show();
            for (int i = 0; i < n; i++)
            {
                AnimatorController.Parameter p = parameters[i];
                Origami.Label(paper, $"{id}_p_{i}", $"{p.Type}  {p.Name}").Show();
            }
        }

        Origami.Button(paper, $"{id}_open", "Open Animator Window",
            () => AnimationAssetEditorUtil.OpenAnimatorWindow(controller)).Show();

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(controller, entry))
                    _dirty = false;
            }).Show();
    }
}
