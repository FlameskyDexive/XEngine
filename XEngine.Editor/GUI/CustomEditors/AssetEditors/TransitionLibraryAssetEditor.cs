// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using global::XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

/// <summary>
/// M50 — inspector for <see cref="TransitionLibraryAsset"/> (<c>.transitionlibrary</c>): the
/// (from → to) modifier table with fade/start/speed/order columns, wildcard hints, and pipeline save.
/// </summary>
[CustomAssetEditor(typeof(TransitionLibraryAsset))]
public class TransitionLibraryAssetEditor : AssetImporterEditor
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
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Film}  Transition Library").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();
        Origami.Label(paper, $"{id}_hint",
            "Empty From/To = wildcard. Specificity: both keys > one key > wildcard; Order breaks ties.").Show();

        var library = asset as TransitionLibraryAsset;
        if (library == null)
        {
            Origami.Label(paper, $"{id}_none", "TransitionLibraryAsset failed to load.").Show();
            return;
        }

        Origami.Header(paper, $"{id}_rows_h", $"Modifiers ({library.Modifiers.Count})").Show();
        var modifiers = library.Modifiers;
        for (int i = 0; i < modifiers.Count; i++)
        {
            TransitionLibraryAsset.Modifier m = modifiers[i];
            string row = $"{id}_m_{i}";
            EditorGUI.Row(paper, $"{row}_from", "From", () =>
                Origami.TextField(paper, $"{row}_from_v", m.FromKey, v =>
                {
                    m.FromKey = v ?? "";
                    _dirty = true;
                }).Show());
            EditorGUI.Row(paper, $"{row}_to", "To", () =>
                Origami.TextField(paper, $"{row}_to_v", m.ToKey, v =>
                {
                    m.ToKey = v ?? "";
                    _dirty = true;
                }).Show());
            EditorGUI.Row(paper, $"{row}_fade", "Fade (s)", () =>
                Origami.NumericField(paper, $"{row}_fade_v", m.FadeDuration, v =>
                {
                    m.FadeDuration = v;
                    _dirty = true;
                }).Show());
            EditorGUI.Row(paper, $"{row}_start", "Start", () =>
                Origami.NumericField(paper, $"{row}_start_v",
                    float.IsNaN(m.NormalizedStartTime) ? -1f : m.NormalizedStartTime, v =>
                    {
                        m.NormalizedStartTime = v < 0f ? float.NaN : v;
                        _dirty = true;
                    }).Show());
            EditorGUI.Row(paper, $"{row}_speed", "Speed", () =>
                Origami.NumericField(paper, $"{row}_speed_v", m.Speed, v =>
                {
                    m.Speed = v;
                    _dirty = true;
                }).Show());
            EditorGUI.Row(paper, $"{row}_order", "Order", () =>
                Origami.NumericField(paper, $"{row}_order_v", m.Order, v =>
                {
                    m.Order = (int)v;
                    _dirty = true;
                }).Show());
            Origami.Button(paper, $"{row}_del", "Remove", () =>
            {
                modifiers.RemoveAt(i);
                _dirty = true;
            }).Show();
        }

        Origami.Button(paper, $"{id}_add", "Add Modifier", () =>
        {
            modifiers.Add(new TransitionLibraryAsset.Modifier());
            _dirty = true;
        }).Show();

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(library, entry))
                    _dirty = false;
            }).Show();
    }
}
