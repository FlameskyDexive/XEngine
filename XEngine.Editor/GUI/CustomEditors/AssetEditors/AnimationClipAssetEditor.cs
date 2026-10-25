// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

[CustomAssetEditor(typeof(AnimationClip))]
public class AnimationClipAssetEditor : AssetImporterEditor
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
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Animation Clip").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var clip = asset as AnimationClip;
        if (clip == null)
        {
            Origami.Label(paper, $"{id}_none", "AnimationClip failed to load.").Show();
            return;
        }

        EditorGUI.Row(paper, $"{id}_len", "Length", () =>
            Origami.NumericField(paper, $"{id}_len_v", clip.Duration, v =>
            {
                clip.Duration = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_fps", "FPS", () =>
            Origami.NumericField(paper, $"{id}_fps_v", clip.TicksPerSecond, v =>
            {
                clip.TicksPerSecond = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_wrap", "Wrap Mode", () =>
            Origami.EnumDropdown(paper, $"{id}_wrap_v", clip.Wrap, v =>
            {
                clip.Wrap = v;
                _dirty = true;
            }).Show());

        EditorGUI.Row(paper, $"{id}_loop", "Loop", () =>
            Origami.Toggle(paper, $"{id}_loop_v", clip.Wrap == AnimationWrapMode.Loop, v =>
            {
                clip.Wrap = v ? AnimationWrapMode.Loop : AnimationWrapMode.Once;
                _dirty = true;
            }).Show());

        string rootPolicy = clip.RootMotionPolicy switch
        {
            ClipRootMotionPolicy.LegacyAutomatic => "Legacy Automatic (path, then automatic fallback)",
            ClipRootMotionPolicy.ExplicitBone when clip.RootBonePath == "."
                => "Explicit Model Root (movement and rotation; no fallback)",
            ClipRootMotionPolicy.ExplicitBone => "Explicit Bone (exact path; no fallback)",
            ClipRootMotionPolicy.Disabled => "Disabled (path curves: no extraction or in-place root pin)",
            _ => $"Unknown ({(int)clip.RootMotionPolicy}); extraction and root pin disabled",
        };
        if (clip.IsHumanoid)
            rootPolicy += ". Humanoid muscle playback uses Avatar/TDoF root handling.";
        paper.Box($"{id}_rootPolicy").Height(UnitValue.Auto).Margin(8, 8, 2, 6)
            .Text("Root Motion: " + rootPolicy, EditorTheme.DefaultFont!).FontSize(EditorTheme.FontSizeSmall)
            .TextColor(EditorTheme.Ink400).Wrap(XEngine.Scribe.TextWrapMode.Wrap).IsNotInteractable();

        EditorGUI.Row(paper, $"{id}_root", "Root Bone Path", () =>
            Origami.TextField(paper, $"{id}_root_v", clip.RootBonePath ?? "", v =>
            {
                clip.RootBonePath = string.IsNullOrEmpty(v) ? null : v;
                _dirty = true;
            }).Show());

        Origami.Label(paper, $"{id}_ev", "Events: not serialized (v1.2)").Show();

        Origami.Button(paper, $"{id}_open", "Open Animation Window",
            () => AnimationAssetEditorUtil.OpenAnimationWindow(clip)).Show();

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(clip, entry))
                    _dirty = false;
            }).Show();
    }
}
