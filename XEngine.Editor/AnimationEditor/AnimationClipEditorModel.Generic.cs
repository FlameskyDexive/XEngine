// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

public partial class AnimationClipEditorModel
{
    public AnimationClip.GenericCurve? FindGeneric(string path, string typeName, string property)
        => Clip?.FindGenericCurve(path, typeName, property);

    public bool AddGenericProperty(string path, string typeName, string property, float time)
    {
        if (Clip == null || string.IsNullOrEmpty(property)) return false;
        var g = Clip.FindGenericCurve(path, typeName, property);
        if (g == null)
        {
            g = new AnimationClip.GenericCurve
            {
                Path = path ?? "",
                TypeName = typeName ?? "",
                Property = property,
                Curve = CreateEmptyCurve(),
            };
            Clip.GenericCurves.Add(g);
        }
        g.Curve ??= CreateEmptyCurve();
        if (IndexOfKeyNear(g.Curve, time) < 0)
        {
            float value = g.Curve.Keys.Count > 0 ? g.Curve.Evaluate(time) : 0f;
            g.Curve.Keys.Add(new KeyFrame(time, value));
            g.Curve.SmoothTangents(CurveTangent.Smooth);
        }
        ExpandDuration(time);
        BumpContent();
        SelectKeyframe(path, Channel.Generic, IndexOfKeyNear(g.Curve, time), time, -1, false, typeName, property);
        return true;
    }

    public void AppendGenericRows(List<PropertyRow> dest)
    {
        if (Clip == null) return;
        var gens = Clip.GenericCurves;
        for (int i = 0; i < gens.Count; i++)
        {
            var g = gens[i];
            string loc = string.IsNullOrEmpty(g.Path) ? "(Root)" : g.Path;
            dest.Add(new PropertyRow
            {
                Kind = PropertyRowKind.Generic,
                BonePath = g.Path,
                Channel = Channel.Generic,
                Component = 0,
                Label = loc + " " + ShortType(g.TypeName) + "." + g.Property,
                Depth = 0,
                HasChildren = false,
                Expanded = true,
                TypeName = g.TypeName,
                Property = g.Property,
            });
        }
    }

    static string ShortType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return "Generic";
        int dot = typeName.LastIndexOf('.');
        return dot >= 0 && dot < typeName.Length - 1 ? typeName.Substring(dot + 1) : typeName;
    }
}
