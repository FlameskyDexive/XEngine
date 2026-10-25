// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// ★ N5 — Blend-shape (morph target) tracks in the Animation window. Storage stays on
/// <see cref="AnimationClip.BlendShapes"/> (the runtime contract used by the importers,
/// <c>AnimationComponent</c> and the Playables stack); rows surface them as
/// <see cref="PropertyRowKind.BlendShape"/> with the shape name carried in
/// <see cref="AnimationClipEditorModel.PropertyRow.Property"/>.
/// </summary>
public partial class AnimationClipEditorModel
{
    public AnimationClip.BlendShapeAnim? FindBlendShape(string path, string shapeName)
        => Clip?.FindBlendShapeTrack(path, shapeName);

    public bool AddBlendShapeProperty(string path, string shapeName, float time)
    {
        if (Clip == null || string.IsNullOrEmpty(shapeName)) return false;
        var track = Clip.FindBlendShapeTrack(path, shapeName);
        if (track == null)
        {
            track = new AnimationClip.BlendShapeAnim
            {
                Path = path ?? "",
                ShapeName = shapeName,
                Weight = CreateEmptyCurve(),
            };
            Clip.AddBlendShape(track);
        }
        track.Weight ??= CreateEmptyCurve();
        if (IndexOfKeyNear(track.Weight, time) < 0)
        {
            float value = track.Weight.Keys.Count > 0 ? track.Weight.Evaluate(time) : 0f;
            track.Weight.Keys.Add(new KeyFrame(time, value));
            track.Weight.SmoothTangents(CurveTangent.Smooth);
        }
        ExpandDuration(time);
        BumpContent();
        SelectKeyframe(path, Channel.BlendShape, IndexOfKeyNear(track.Weight, time), time, -1, false, "SkinnedMeshRenderer", shapeName);
        return true;
    }

    public void AppendBlendShapeRows(List<PropertyRow> dest)
    {
        if (Clip == null) return;
        var tracks = Clip.BlendShapes;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            string loc = string.IsNullOrEmpty(t.Path) ? "(Root)" : t.Path;
            dest.Add(new PropertyRow
            {
                Kind = PropertyRowKind.BlendShape,
                BonePath = t.Path,
                Channel = Channel.BlendShape,
                Component = 0,
                Label = loc + " BlendShape." + t.ShapeName,
                Depth = 0,
                HasChildren = false,
                Expanded = true,
                TypeName = "SkinnedMeshRenderer",
                Property = t.ShapeName,
            });
        }
    }
}
