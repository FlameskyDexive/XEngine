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

[CustomAssetEditor(typeof(AvatarMask))]
public class AvatarMaskAssetEditor : AssetImporterEditor
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
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Avatar Mask").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var mask = asset as AvatarMask;
        if (mask == null)
        {
            Origami.Label(paper, $"{id}_none", "AvatarMask failed to load.").Show();
            return;
        }

        Origami.Header(paper, $"{id}_hum", "Humanoid").Show();
        for (int p = 0; p < AvatarMask.HumanoidPartCount; p++)
        {
            int part = p;
            string name = AvatarMaskEditorModel.HumanoidPartNames[p];
            bool on = mask.GetHumanoidPart((AvatarMaskBodyPart)p);
            EditorGUI.Row(paper, $"{id}_hp_{p}", name, () =>
                Origami.Checkbox(paper, $"{id}_hp_{p}_v", on, v =>
                {
                    mask.SetHumanoidPart((AvatarMaskBodyPart)part, v);
                    _dirty = true;
                }).Show());
        }

        Origami.Header(paper, $"{id}_eh", $"Bones ({mask.Entries.Count})").Show();
        var entries = mask.Entries;
        var rows = AvatarMaskEditorModel.BuildBoneTree(entries);
        for (int i = 0; i < rows.Count; i++)
        {
            AvatarMaskEditorModel.BoneRow row = rows[i];
            AvatarMask.Entry e = entries[row.EntryIndex];
            int idx = row.EntryIndex;
            string rid = $"{id}_b_{idx}";
            string label = new string(' ', row.Depth * 2) + row.Label;
            bool included = e.Weight > 0.5f;
            EditorGUI.Row(paper, $"{rid}_row", label, () =>
            {
                Origami.Checkbox(paper, $"{rid}_on", included, v =>
                {
                    e.Weight = v ? 1f : 0f;
                    _dirty = true;
                }).Show();
            });
            Origami.Button(paper, $"{rid}_del", "Remove", () =>
            {
                entries.RemoveAt(idx);
                _dirty = true;
            }).Show();
        }

        Origami.Button(paper, $"{id}_add", "Add Bone", () =>
        {
            entries.Add(new AvatarMask.Entry());
            _dirty = true;
        }).Show();

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(mask, entry))
                    _dirty = false;
            }).Show();
    }
}
