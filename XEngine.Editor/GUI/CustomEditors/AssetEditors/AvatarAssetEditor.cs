// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

/// <summary>
/// M59 — inspector for <see cref="Avatar"/> (<c>.avatar</c>): the bone-mapping grid (one row per
/// humanoid role with its skeleton path), an Auto-Map button that re-runs the hierarchy-aware
/// resolver over a chosen skeleton-root object, the validation report, and limb-length measures.
/// </summary>
[CustomAssetEditor(typeof(Avatar))]
public class AvatarAssetEditor : AssetImporterEditor
{
    private Guid _currentGuid;
    private bool _dirty;
    private string? _autoMapRootName;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (_currentGuid != entry.Guid)
        {
            _currentGuid = entry.Guid;
            _dirty = false;
            _autoMapRootName = null;
        }

        id = $"{id}_{entry.Guid:N}";
        var avatar = asset as Avatar;
        Origami.Header(paper, $"{id}_h", $"{EditorIcons.User}  {avatar?.RigKind.ToString() ?? "Unknown"} Avatar").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        if (avatar == null)
        {
            Origami.Label(paper, $"{id}_none", "Avatar failed to load.").Show();
            return;
        }

        if (!avatar.IsHumanoid)
        {
            ReadOnlyNote(paper, $"{id}_kind", $"Rig Type: {avatar.RigKind}");
            ReadOnlyNote(paper, $"{id}_generic_root", "Root Path: " +
                (string.IsNullOrEmpty(avatar.RootMotionBonePath) ? "None" : avatar.RootMotionBonePath));
            ReadOnlyNote(paper, $"{id}_generic_count", $"Reference Transforms: {avatar.GenericSkeleton?.Count ?? 0}");
            string? validation = avatar.Validate();
            ReadOnlyNote(paper, $"{id}_generic_validation", validation == null ? "Validation: OK" : "Validation: " + validation);
            ReadOnlyNote(paper, $"{id}_generic_scope", "Read-only skeleton description. Model Create uses the stored Rig settings. Copy and Rig editing are unavailable here.");
            return;
        }

        // Auto-map: pick a scene object as the skeleton root and re-run the resolver.
        Origami.Header(paper, $"{id}_am_h", "Auto Map").Show();
        EditorGUI.Row(paper, $"{id}_root", "Skeleton Root", () =>
            Origami.TextField(paper, $"{id}_root_v", _autoMapRootName ?? "", v =>
            {
                _autoMapRootName = v;
            }).Show());
        Origami.Button(paper, $"{id}_am", "Auto Map From Root", () =>
        {
            if (!TryFindSceneRoot(_autoMapRootName, out XEngine.Vector.Transform? root) || root == null)
            {
                Runtime.Debug.LogWarning("[AvatarEditor] skeleton root object not found by name.");
                return;
            }
            Avatar rebuilt = Avatar.BuildAuto(root, avatar.Name);
            avatar.Mappings.Clear();
            avatar.Mappings.AddRange(rebuilt.Mappings);
            avatar.RootMotionBonePath = rebuilt.RootMotionBonePath;
            avatar.MeasureLimbs();
            _dirty = true;
        }).Show();

        // Validation report.
        string? problems = avatar.Validate();
        Origami.Label(paper, $"{id}_val", problems == null
            ? "Validation: OK"
            : $"Validation:\n{problems}").Show();
        Origami.Label(paper, $"{id}_limbs",
            $"Legs {avatar.LegLength:F2}m — Arms {avatar.ArmLength:F2}m").Show();

        // Mapping grid.
        Origami.Header(paper, $"{id}_map_h", $"Mappings ({avatar.Mappings.Count})").Show();
        var mappings = avatar.Mappings;
        for (int i = 0; i < mappings.Count; i++)
        {
            Avatar.BoneMapping m = mappings[i];
            EditorGUI.Row(paper, $"{id}_m{i}", m.Bone.ToString(), () =>
                Origami.TextField(paper, $"{id}_m{i}_v", m.Path, v =>
                {
                    m.Path = v ?? "";
                    _dirty = true;
                }).Show());
        }

        if (!_dirty)
            return;

        Origami.Separator(paper, $"{id}_sep").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
            () =>
            {
                if (AnimationAssetEditorUtil.Save(avatar, entry))
                    _dirty = false;
            }).Show();
    }

    private static void ReadOnlyNote(Paper paper, string id, string text)
        => paper.Box(id).Height(UnitValue.Auto).Margin(8, 8, 2, 6)
            .Text(text, EditorTheme.DefaultFont!).FontSize(EditorTheme.FontSizeSmall)
            .TextColor(EditorTheme.Ink400).Wrap(XEngine.Scribe.TextWrapMode.Wrap).IsNotInteractable();

    private static bool TryFindSceneRoot(string? name, out XEngine.Vector.Transform? root)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(name) || Runtime.Resources.Scene.Current == null)
            return false;
        foreach (GameObject go in Runtime.Resources.Scene.Current.AllObjects)
        {
            if (go.Name == name)
            {
                root = go.Transform;
                return true;
            }
        }
        return false;
    }
}
