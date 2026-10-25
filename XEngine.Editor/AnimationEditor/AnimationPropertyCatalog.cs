// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// ★ M38 — Hierarchy + reflection catalog for the Animation window Add Property popup.
/// Transform TRS channels that are not yet on the clip are offered as built-in bindings;
/// other public float fields/properties become <see cref="AnimationClip.GenericCurve"/> tracks.
/// </summary>
public static class AnimationPropertyCatalog
{
    public struct Entry
    {
        public string Path;
        public string TypeName;
        public string Property;
        public string Label;
        public bool IsTransformChannel;
        public bool IsBlendShape;
        public AnimationClipEditorModel.Channel Channel;
    }

    public static void Collect(GameObject? root, AnimationClip? clip, string? search, List<Entry> dest)
    {
        dest.Clear();
        if (root == null) return;
        Walk(root, "", clip, search, dest);
    }

    static void Walk(GameObject go, string path, AnimationClip? clip, string? search, List<Entry> dest)
    {
        AnimationClip.AnimBone? bone = clip != null && !string.IsNullOrEmpty(path) ? clip.GetBone(path) : null;
        OfferTransform(path, "Position", AnimationClipEditorModel.Channel.Position, bone, search, dest);
        OfferTransform(path, "Rotation", AnimationClipEditorModel.Channel.Rotation, bone, search, dest);
        OfferTransform(path, "Scale", AnimationClipEditorModel.Channel.Scale, bone, search, dest);

        foreach (MonoBehaviour comp in go.GetComponents())
        {
            if (comp == null) continue;
            Type t = comp.GetType();
            string typeName = t.FullName ?? t.Name;

            // Blend shapes: every mesh shape not already tracked on the clip is offered as a
            // dedicated BlendShapeAnim binding (GenericCurve float reflection does not apply).
            if (comp is SkinnedMeshRenderer smr)
            {
                var shapeMesh = smr.SharedMesh.Res;
                int shapeCount = shapeMesh?.BlendShapeCount ?? 0;
                for (int s = 0; s < shapeCount; s++)
                {
                    string shapeName = shapeMesh!.GetBlendShapeName(s);
                    if (clip != null && clip.FindBlendShapeTrack(path, shapeName) != null) continue;
                    OfferBlendShape(path, shapeName, search, dest);
                }
                continue;
            }

            const BindingFlags fieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy;
            FieldInfo[] fields = t.GetFields(fieldFlags);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.FieldType != typeof(float)) continue;
                if (f.IsDefined(typeof(HideInInspectorAttribute), inherit: true)) continue;
                OfferGeneric(path, typeName, t.Name, f.Name, search, clip, dest);
            }
            PropertyInfo[] props = t.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            for (int i = 0; i < props.Length; i++)
            {
                PropertyInfo p = props[i];
                if (p.PropertyType != typeof(float) || p.GetIndexParameters().Length > 0) continue;
                if (p.SetMethod == null || !p.SetMethod.IsPublic) continue;
                if (p.IsDefined(typeof(HideInInspectorAttribute), inherit: true)) continue;
                OfferGeneric(path, typeName, t.Name, p.Name, search, clip, dest);
            }
        }

        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
        {
            GameObject child = children[i];
            string childPath = string.IsNullOrEmpty(path) ? child.Name : path + "/" + child.Name;
            Walk(child, childPath, clip, search, dest);
        }
    }

    static void OfferTransform(
        string path, string label, AnimationClipEditorModel.Channel channel,
        AnimationClip.AnimBone? bone, string? search, List<Entry> dest)
    {
        if (bone != null && AnimationClipEditorModel.HasChannelCurves(bone, channel))
            return;
        string loc = string.IsNullOrEmpty(path) ? "(Root)" : path;
        string text = loc + " : Transform." + label;
        if (!Matches(search, text)) return;
        dest.Add(new Entry
        {
            Path = path,
            TypeName = "Transform",
            Property = label,
            Label = text,
            IsTransformChannel = true,
            Channel = channel,
        });
    }

    static void OfferGeneric(
        string path, string typeName, string shortName, string property,
        string? search, AnimationClip? clip, List<Entry> dest)
    {
        if (clip != null && clip.FindGenericCurve(path, typeName, property) != null)
            return;
        string loc = string.IsNullOrEmpty(path) ? "(Root)" : path;
        string text = loc + " : " + shortName + "." + property;
        if (!Matches(search, text) && !Matches(search, property)) return;
        dest.Add(new Entry
        {
            Path = path,
            TypeName = typeName,
            Property = property,
            Label = text,
            IsTransformChannel = false,
            Channel = AnimationClipEditorModel.Channel.Generic,
        });
    }

    static void OfferBlendShape(string path, string shapeName, string? search, List<Entry> dest)
    {
        string loc = string.IsNullOrEmpty(path) ? "(Root)" : path;
        string text = loc + " : BlendShape." + shapeName;
        if (!Matches(search, text) && !Matches(search, shapeName)) return;
        dest.Add(new Entry
        {
            Path = path,
            TypeName = "SkinnedMeshRenderer",
            Property = shapeName,
            Label = text,
            IsTransformChannel = false,
            IsBlendShape = true,
            Channel = AnimationClipEditorModel.Channel.BlendShape,
        });
    }

    static bool Matches(string? search, string text)
    {
        if (string.IsNullOrEmpty(search)) return true;
        return text.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
