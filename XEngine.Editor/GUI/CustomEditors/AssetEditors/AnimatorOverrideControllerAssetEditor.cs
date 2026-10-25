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

[CustomAssetEditor(typeof(AnimatorOverrideController))]
public class AnimatorOverrideControllerAssetEditor : AssetImporterEditor
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
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Animator Override Controller").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var ov = asset as AnimatorOverrideController;
        if (ov == null)
        {
            Origami.Label(paper, $"{id}_none", "AnimatorOverrideController failed to load.").Show();
            return;
        }

        EditorGUI.Row(paper, $"{id}_base", "Base", () =>
            PropertyGridUtils.DrawField(paper, $"{id}_base_v", "",
                typeof(AssetRef<AnimatorController>), ov.Base,
                v =>
                {
                    ov.Base = v is AssetRef<AnimatorController> r ? r : default;
                    _dirty = true;
                }));

        AnimatorController? src = ov.Base.Res;
        Origami.Label(paper, $"{id}_src", src != null
            ? $"Base: {src.Name}  ({src.States.Count} states)"
            : "Assign a base AnimatorController.").Show();

        Origami.Header(paper, $"{id}_map", $"Clip Overrides ({ov.Overrides.Count})").Show();
        var list = ov.Overrides;
        for (int i = 0; i < list.Count; i++)
        {
            int idx = i;
            AnimatorOverrideController.ClipOverride row = list[i];
            string rid = $"{id}_r_{i}";
            EditorGUI.Row(paper, $"{rid}_from", "Original", () =>
                PropertyGridUtils.DrawField(paper, $"{rid}_from_v", "",
                    typeof(AssetRef<AnimationClip>), row.From,
                    v =>
                    {
                        row.From = v is AssetRef<AnimationClip> r ? r : default;
                        _dirty = true;
                    }));
            EditorGUI.Row(paper, $"{rid}_to", "Override", () =>
                PropertyGridUtils.DrawField(paper, $"{rid}_to_v", "",
                    typeof(AssetRef<AnimationClip>), row.To,
                    v =>
                    {
                        row.To = v is AssetRef<AnimationClip> r ? r : default;
                        _dirty = true;
                    }));
            Origami.Button(paper, $"{rid}_del", "Remove", () =>
            {
                list.RemoveAt(idx);
                _dirty = true;
            }).Show();
        }

        Origami.Button(paper, $"{id}_add", "Add Override", () =>
        {
            list.Add(new AnimatorOverrideController.ClipOverride());
            _dirty = true;
        }).Show();

        if (src != null)
        {
            Origami.Button(paper, $"{id}_open", "Open Base Animator Window",
                () => AnimationAssetEditorUtil.OpenAnimatorWindow(src)).Show();
        }

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(ov, entry))
                    _dirty = false;
            }).Show();
    }
}
