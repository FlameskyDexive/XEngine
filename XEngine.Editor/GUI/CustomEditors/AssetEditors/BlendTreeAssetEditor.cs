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

[CustomAssetEditor(typeof(BlendTree))]
public class BlendTreeAssetEditor : AssetImporterEditor
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
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Blend Tree").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var tree = asset as BlendTree;
        if (tree == null)
        {
            Origami.Label(paper, $"{id}_none", "BlendTree failed to load.").Show();
            return;
        }

        EditorGUI.Row(paper, $"{id}_type", "Type", () =>
            Origami.EnumDropdown(paper, $"{id}_type_v", tree.Type, v =>
            {
                tree.Type = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_param", "Parameter", () =>
            Origami.TextField(paper, $"{id}_param_v", tree.ParameterName, v =>
            {
                tree.ParameterName = v ?? "";
                _dirty = true;
            }).Show());

        Origami.Header(paper, $"{id}_ch", $"Children ({tree.Children.Count})").Show();
        var children = tree.Children;
        for (int i = 0; i < children.Count; i++)
        {
            int idx = i;
            BlendTree.Child child = children[i];
            string row = $"{id}_c_{i}";
            PropertyGridUtils.DrawField(paper, $"{row}_clip", $"Clip {i}",
                typeof(AssetRef<AnimationClip>), child.Clip,
                v =>
                {
                    child.Clip = v is AssetRef<AnimationClip> r ? r : default;
                    _dirty = true;
                });
            EditorGUI.Row(paper, $"{row}_th", "Threshold", () =>
                Origami.NumericField(paper, $"{row}_th_v", child.Threshold, v =>
                {
                    child.Threshold = v;
                    _dirty = true;
                }).Show());
            // M60 — per-child playback speed + clock alignment.
            EditorGUI.Row(paper, $"{row}_sp", "Speed", () =>
                Origami.NumericField(paper, $"{row}_sp_v", child.Speed, v =>
                {
                    child.Speed = v;
                    _dirty = true;
                }).Show());
            EditorGUI.Row(paper, $"{row}_ts", "Time Sync", () =>
                Origami.EnumDropdown(paper, $"{row}_ts_v", child.TimeSync, v =>
                {
                    child.TimeSync = v;
                    _dirty = true;
                }).Show());
            if (child.TimeSync == BlendTimeSync.StartTime || child.TimeSync == BlendTimeSync.PhaseFoot)
            {
                EditorGUI.Row(paper, $"{row}_ph", "Phase", () =>
                    Origami.NumericField(paper, $"{row}_ph_v", child.Phase, v =>
                    {
                        child.Phase = v;
                        _dirty = true;
                    }).Show());
            }
            Origami.Button(paper, $"{row}_del", "Remove", () =>
            {
                children.RemoveAt(idx);
                _dirty = true;
            }).Show();
        }

        Origami.Button(paper, $"{id}_add", "Add Child", () =>
        {
            children.Add(new BlendTree.Child());
            _dirty = true;
        }).Show();

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(tree, entry))
                    _dirty = false;
            }).Show();
    }
}
