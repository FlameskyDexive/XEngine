// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Presets;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Rosetta;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

/// <summary>
/// Inspector for <see cref="Preset"/> assets: shows the captured target type, lets the user apply
/// the preset to the current selection, toggles the per-type default (Preset Manager), and edits
/// the excluded-property list.
/// </summary>
[CustomAssetEditor(typeof(Preset))]
public class PresetAssetEditor : AssetImporterEditor
{
    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        id = $"{id}_{entry.Guid:N}";
        var preset = asset as Preset;
        if (preset == null)
        {
            Origami.Label(paper, $"{id}_none", "Preset failed to load.").Show();
            return;
        }

        var targetType = preset.ResolveTargetType();
        string targetLabel = targetType?.Name ?? $"{preset.TargetType} (missing)";

        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Sliders}  {Loc.Get("presets.preset")} — {targetLabel}").Show();
        Origami.Label(paper, $"{id}_path", $"{Loc.Get("inspector.path")}: {entry.Path}").Show();
        Origami.Label(paper, $"{id}_target", $"{Loc.Get("inspector.type")}: {targetLabel}").Show();

        // ── Apply to selection ──
        Origami.Separator(paper, $"{id}_sep_apply").Show();
        bool applied = false;

        if (targetType != null && typeof(MonoBehaviour).IsAssignableFrom(targetType))
        {
            foreach (var go in Selection.GetSelected<GameObject>())
            {
                var comp = go.GetComponent(targetType);
                if (comp == null) continue;
                var captured = comp;
                Origami.Button(paper, $"{id}_apply", $"{Loc.Get("presets.apply_to", new { name = go.Name })}",
                    () => { PresetUtility.ApplyToComponent(preset, captured); EditorSceneManager.MarkDirty(); }).Show();
                applied = true;
                break;
            }
        }
        else if (targetType != null && typeof(EngineObject).IsAssignableFrom(targetType))
        {
            var selectedAsset = Selection.ActiveObject as EngineObject;
            if (selectedAsset != null && selectedAsset.GetType() == targetType)
            {
                var captured = selectedAsset;
                Origami.Button(paper, $"{id}_apply", Loc.Get("presets.apply_to_selection"),
                    () => PresetUtility.ApplyToAsset(preset, captured)).Show();
                applied = true;
            }
        }
        if (!applied)
            Origami.Label(paper, $"{id}_apply_hint",
                Loc.Get("presets.apply_hint", new { type = targetLabel })).Show();

        // ── Preset Manager: default for new components ──
        Origami.Separator(paper, $"{id}_sep_default").Show();
        bool isDefault = targetType != null
            && PresetUtility.GetDefaultPreset(targetType) is { } def
            && ((EngineObject)def).AssetID == entry.Guid;
        Origami.Button(paper, $"{id}_default",
            isDefault ? Loc.Get("presets.clear_default") : Loc.Get("presets.set_default"),
            () => PresetUtility.SetDefaultPreset(targetType!, isDefault ? null : preset)).Show();

        // ── Excluded properties ──
        Origami.Separator(paper, $"{id}_sep_excl").Show();
        Origami.Header(paper, $"{id}_excl_h",
            $"{Loc.Get("presets.excluded")} ({preset.Excluded.Count})").Show();
        for (int i = 0; i < preset.Excluded.Count; i++)
        {
            int idx = i;
            EditorGUI.Row(paper, $"{id}_excl_{i}", PropertyGridUtils.NicifyName(preset.Excluded[i]), () =>
                Origami.Button(paper, $"{id}_excl_rm_{i}", Loc.Get("common.remove"),
                    () => preset.Excluded.RemoveAt(idx)).Show());
        }
    }
}
