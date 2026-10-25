// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PostProcessing.Effects;

namespace XEngine.PostProcessing.Editor;

[CustomEditor(typeof(PostProcessProfile))]
public sealed class PostProcessProfileEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var profile = (PostProcessProfile)target;
        Undo.SnapshotIfInteracting(profile);

        Origami.Label(paper, $"{id}_add_lbl", "Add effect").Show();
        var types = EffectCatalog.AllSettingsTypes;
        for (int i = 0; i < types.Count; i++)
        {
            Type type = types[i];
            if (profile.HasSettings(type))
                continue;
            Type captured = type;
            Origami.Button(paper, $"{id}_add_{type.Name}", EffectCatalog.MenuPath(type), () =>
            {
                Undo.Snapshot(profile);
                profile.AddSettings(captured);
            }).Show();
        }

        for (int i = 0; i < profile.settings.Count; i++)
        {
            PostProcessEffectSettings? settings = profile.settings[i];
            if (settings == null)
                continue;
            int index = i;
            Origami.Separator(paper, $"{id}_sep_{i}").Show();
            Origami.Label(paper, $"{id}_name_{i}", EffectCatalog.MenuPath(settings.GetType())).Show();
            DrawDefaultInspector(paper, $"{id}_fx_{i}", settings);
            Origami.Button(paper, $"{id}_rm_{i}", "Remove", () =>
            {
                Undo.Snapshot(profile);
                profile.settings.RemoveAt(index);
                profile.isDirty = true;
            }).Show();
        }
    }
}
