// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

using XEngine.Animation;
using XEngine.Editor.Core;
using XEngine.Runtime;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// ★ M42 — Reverse of <see cref="AnimationPreviewSession"/>: snapshot bound properties on Rec
/// enter, then write keys at the playhead when Scene edits diverge (Unity
/// <c>AnimationRecording.SaveModifiedProperties</c>).
/// </summary>
public sealed class AnimationRecording
{
    public static AnimationRecording Instance { get; } = new();

    const float Epsilon = AnimationClipEditorModel.KeyTimeEpsilon;

    struct BoneSnap
    {
        public string Path;
        public Transform Transform;
        public Float3 Position;
        public Quaternion Rotation;
        public Float3 Scale;
    }

    struct GenericSnap
    {
        public string Path;
        public string TypeName;
        public string Property;
        public float Value;
    }

    struct BlendShapeSnap
    {
        public string Path;
        public SkinnedMeshRenderer? Renderer;
        public int ShapeIndex;
        public string ShapeName;
        public float Value;
    }

    readonly List<BoneSnap> _bones = new();
    readonly List<GenericSnap> _generic = new();
    readonly List<BlendShapeSnap> _blendShapes = new();
    GameObject? _root;
    AnimationClip? _clip;
    bool _recording;

    public bool IsRecording => _recording;
    public int KeysWritten { get; private set; }
    public GameObject? Root => _root;
    public AnimationClip? Clip => _clip;

    public bool Begin(GameObject root, AnimationClip clip)
    {
        End();
        if (root == null || clip == null)
            return false;

        _root = root;
        _clip = clip;
        _recording = true;
        KeysWritten = 0;
        CaptureBones(root, string.Empty);
        CaptureGeneric(root, clip);
        CaptureBlendShapes(root, string.Empty);
        return true;
    }

    public void End()
    {
        _recording = false;
        _root = null;
        _clip = null;
        _bones.Clear();
        _generic.Clear();
        _blendShapes.Clear();
    }

    /// <summary>Compare the live pose to the snapshot and write keys at <paramref name="time"/>.</summary>
    public void Tick(float time)
    {
        if (!_recording || _clip == null || _root == null)
            return;
        if (time < 0f) time = 0f;

        bool any = false;
        for (int i = 0; i < _bones.Count; i++)
        {
            BoneSnap snap = _bones[i];
            Transform t = snap.Transform;
            if (t == null) continue;

            Float3 pos = t.LocalPosition;
            Quaternion rot = t.LocalRotation;
            Float3 scl = t.LocalScale;

            if (Differs(pos, snap.Position))
            {
                WriteBoneChannel(_clip, snap.Path, AnimationClipEditorModel.Channel.Position, time, pos.X, pos.Y, pos.Z, 0f);
                snap.Position = pos;
                any = true;
            }
            if (Differs(rot, snap.Rotation))
            {
                WriteBoneChannel(_clip, snap.Path, AnimationClipEditorModel.Channel.Rotation, time, rot.X, rot.Y, rot.Z, rot.W);
                snap.Rotation = rot;
                any = true;
            }
            if (Differs(scl, snap.Scale))
            {
                WriteBoneChannel(_clip, snap.Path, AnimationClipEditorModel.Channel.Scale, time, scl.X, scl.Y, scl.Z, 0f);
                snap.Scale = scl;
                any = true;
            }
            _bones[i] = snap;
        }

        for (int i = 0; i < _generic.Count; i++)
        {
            GenericSnap g = _generic[i];
            if (!TryReadFloat(_root, g.Path, g.TypeName, g.Property, out float live))
                continue;
            if (AnimationClipEditorModel.Near(live, g.Value, Epsilon))
                continue;
            WriteGeneric(_clip, g.Path, g.TypeName, g.Property, time, live);
            g.Value = live;
            _generic[i] = g;
            any = true;
        }

        for (int i = 0; i < _blendShapes.Count; i++)
        {
            BlendShapeSnap bs = _blendShapes[i];
            if (bs.Renderer == null) continue;
            float live = bs.Renderer.GetBlendShapeWeight(bs.ShapeIndex);
            if (AnimationClipEditorModel.Near(live, bs.Value, Epsilon))
                continue;
            WriteBlendShape(_clip, bs.Path, bs.ShapeName, time, live);
            bs.Value = live;
            _blendShapes[i] = bs;
            any = true;
        }

        if (any)
            Undo.Snapshot(_clip);
    }

    /// <summary>Explicit key write for AI / tests. Works with or without an active Rec session.</summary>
    public static bool WriteKey(
        AnimationClip clip,
        GameObject? root,
        string path,
        string channel,
        float time,
        float value,
        string? typeName = null)
    {
        if (clip == null || string.IsNullOrEmpty(channel))
            return false;
        if (time < 0f) time = 0f;

        if (TryParseBoneChannel(channel, out AnimationClipEditorModel.Channel ch, out int component))
        {
            AnimationClip.AnimBone bone = GetOrAddBone(clip, path ?? "");
            AnimationClipEditorModel.EnsureCurves(bone, ch);
            var tmp = new AnimationCurve?[4];
            int n = AnimationClipEditorModel.FillCurves(bone, ch, tmp);
            if (component < 0 || component >= n || tmp[component] == null)
                return false;
            WriteCurveKey(tmp[component]!, time, value);
            ExpandDuration(clip, time);
            Undo.Snapshot(clip);
            Instance.RefreshBoneSnap(path ?? "", root);
            Instance.KeysWritten++;
            return true;
        }

        // "blendShape.<ShapeName>" — SkinnedMeshRenderer morph-weight key (N5).
        if (channel.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
        {
            string shapeName = channel.Substring("blendShape.".Length);
            if (string.IsNullOrEmpty(shapeName))
                return false;
            WriteBlendShape(clip, path ?? "", shapeName, time, value);
            Undo.Snapshot(clip);
            Instance.RefreshBlendShapeSnap(path ?? "", shapeName, root);
            Instance.KeysWritten++;
            return true;
        }

        string tn = typeName ?? "";
        if (string.IsNullOrEmpty(tn))
            return false;
        WriteGeneric(clip, path ?? "", tn, channel, time, value);
        Undo.Snapshot(clip);
        return true;
    }

    void RefreshBoneSnap(string path, GameObject? root)
    {
        if (!_recording) return;
        GameObject? r = root ?? _root;
        if (r == null) return;
        for (int i = 0; i < _bones.Count; i++)
        {
            if (_bones[i].Path != path) continue;
            Transform t = _bones[i].Transform;
            if (t == null) return;
            BoneSnap s = _bones[i];
            s.Position = t.LocalPosition;
            s.Rotation = t.LocalRotation;
            s.Scale = t.LocalScale;
            _bones[i] = s;
            return;
        }
    }

    void CaptureBones(GameObject go, string parentPath)
    {
        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
        {
            GameObject child = children[i];
            string path = string.IsNullOrEmpty(parentPath) ? child.Name : parentPath + "/" + child.Name;
            Transform t = child.Transform;
            _bones.Add(new BoneSnap
            {
                Path = path,
                Transform = t,
                Position = t.LocalPosition,
                Rotation = t.LocalRotation,
                Scale = t.LocalScale,
            });
            CaptureBones(child, path);
        }
    }

    void CaptureGeneric(GameObject root, AnimationClip clip)
    {
        var list = clip.GenericCurves;
        for (int i = 0; i < list.Count; i++)
        {
            AnimationClip.GenericCurve g = list[i];
            if (g == null) continue;
            float v = 0f;
            TryReadFloat(root, g.Path, g.TypeName, g.Property, out v);
            _generic.Add(new GenericSnap
            {
                Path = g.Path ?? "",
                TypeName = g.TypeName ?? "",
                Property = g.Property ?? "",
                Value = v,
            });
        }
    }

    void CaptureBlendShapes(GameObject go, string path)
    {
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null)
        {
            var mesh = smr.SharedMesh.Res;
            int count = mesh?.BlendShapeCount ?? 0;
            for (int s = 0; s < count; s++)
            {
                _blendShapes.Add(new BlendShapeSnap
                {
                    Path = path,
                    Renderer = smr,
                    ShapeIndex = s,
                    ShapeName = mesh!.GetBlendShapeName(s),
                    Value = smr.GetBlendShapeWeight(s),
                });
            }
        }

        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
        {
            GameObject child = children[i];
            string childPath = string.IsNullOrEmpty(path) ? child.Name : path + "/" + child.Name;
            CaptureBlendShapes(child, childPath);
        }
    }

    void RefreshBlendShapeSnap(string path, string shapeName, GameObject? root)
    {
        if (!_recording) return;
        GameObject? r = root ?? _root;
        if (r == null) return;
        for (int i = 0; i < _blendShapes.Count; i++)
        {
            if (_blendShapes[i].Path != path || _blendShapes[i].ShapeName != shapeName) continue;
            SkinnedMeshRenderer? smr = _blendShapes[i].Renderer;
            if (smr == null) return;
            BlendShapeSnap s = _blendShapes[i];
            s.Value = smr.GetBlendShapeWeight(s.ShapeIndex);
            _blendShapes[i] = s;
            return;
        }
    }

    static void WriteBlendShape(AnimationClip clip, string path, string shapeName, float time, float value)
    {
        var track = clip.FindBlendShapeTrack(path, shapeName);
        if (track == null)
        {
            track = new AnimationClip.BlendShapeAnim
            {
                Path = path,
                ShapeName = shapeName,
                Weight = new AnimationCurve(),
            };
            clip.AddBlendShape(track);
            // new AnimationCurve() seeds default keys (0→0, 1→1); authored tracks start empty.
            track.Weight!.Keys.Clear();
        }
        track.Weight ??= new AnimationCurve();
        WriteCurveKey(track.Weight, time, value);
        ExpandDuration(clip, time);
        Instance.KeysWritten++;
    }

    static void WriteBoneChannel(
        AnimationClip clip, string path, AnimationClipEditorModel.Channel channel, float time,
        float c0, float c1, float c2, float c3)
    {
        AnimationClip.AnimBone bone = GetOrAddBone(clip, path);
        AnimationClipEditorModel.EnsureCurves(bone, channel);
        var tmp = new AnimationCurve?[4];
        int n = AnimationClipEditorModel.FillCurves(bone, channel, tmp);
        float[] vals = { c0, c1, c2, c3 };
        for (int i = 0; i < n; i++)
        {
            if (tmp[i] == null) continue;
            WriteCurveKey(tmp[i]!, time, vals[i]);
            Instance.KeysWritten++;
        }
        ExpandDuration(clip, time);
    }

    static void WriteGeneric(AnimationClip clip, string path, string typeName, string property, float time, float value)
    {
        AnimationClip.GenericCurve? g = clip.FindGenericCurve(path, typeName, property);
        if (g == null)
        {
            g = new AnimationClip.GenericCurve
            {
                Path = path,
                TypeName = typeName,
                Property = property,
                Curve = new AnimationCurve(),
            };
            g.Curve.Keys.Clear();
            clip.GenericCurves.Add(g);
        }
        g.Curve ??= new AnimationCurve();
        WriteCurveKey(g.Curve, time, value);
        ExpandDuration(clip, time);
        Instance.KeysWritten++;
    }

    static void WriteCurveKey(AnimationCurve curve, float time, float value)
    {
        int i = AnimationClipEditorModel.IndexOfKeyNear(curve, time);
        if (i >= 0)
        {
            curve.Keys[i].Value = value;
            curve.RecalculateNonFreeTangents();
            return;
        }
        curve.Keys.Add(new KeyFrame(time, value));
        curve.SmoothTangents(CurveTangent.Smooth);
    }

    static AnimationClip.AnimBone GetOrAddBone(AnimationClip clip, string path)
    {
        AnimationClip.AnimBone? bone = clip.GetBone(path);
        if (bone != null) return bone;
        bone = new AnimationClip.AnimBone { BoneName = path };
        clip.AddBone(bone);
        return bone;
    }

    static void ExpandDuration(AnimationClip clip, float time)
    {
        if (time > clip.Duration)
            clip.Duration = time;
    }

    static bool TryParseBoneChannel(string channel, out AnimationClipEditorModel.Channel ch, out int component)
    {
        ch = AnimationClipEditorModel.Channel.Position;
        component = 0;
        switch (channel)
        {
            case "PosX": ch = AnimationClipEditorModel.Channel.Position; component = 0; return true;
            case "PosY": ch = AnimationClipEditorModel.Channel.Position; component = 1; return true;
            case "PosZ": ch = AnimationClipEditorModel.Channel.Position; component = 2; return true;
            case "RotX": ch = AnimationClipEditorModel.Channel.Rotation; component = 0; return true;
            case "RotY": ch = AnimationClipEditorModel.Channel.Rotation; component = 1; return true;
            case "RotZ": ch = AnimationClipEditorModel.Channel.Rotation; component = 2; return true;
            case "RotW": ch = AnimationClipEditorModel.Channel.Rotation; component = 3; return true;
            case "ScaleX": ch = AnimationClipEditorModel.Channel.Scale; component = 0; return true;
            case "ScaleY": ch = AnimationClipEditorModel.Channel.Scale; component = 1; return true;
            case "ScaleZ": ch = AnimationClipEditorModel.Channel.Scale; component = 2; return true;
            default: return false;
        }
    }

    static bool TryReadFloat(GameObject root, string path, string typeName, string property, out float value)
    {
        value = 0f;
        Transform? node = string.IsNullOrEmpty(path) ? root.Transform : root.Transform.Find(path);
        if (node == null) return false;

        foreach (MonoBehaviour comp in node.GameObject.GetComponents())
        {
            Type t = comp.GetType();
            if (t.Name != typeName && t.FullName != typeName)
                continue;
            if (comp is Light light && property == "Intensity")
            {
                value = light.Intensity;
                return true;
            }
            if (comp is Light light2 && property == "ShadowStrength")
            {
                value = light2.ShadowStrength;
                return true;
            }

            PropertyInfo? p = t.GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
            if (p != null && p.PropertyType == typeof(float) && p.GetMethod != null)
            {
                value = (float)p.GetValue(comp)!;
                return true;
            }
            FieldInfo? f = t.GetField(property, BindingFlags.Instance | BindingFlags.Public);
            if (f != null && f.FieldType == typeof(float))
            {
                value = (float)f.GetValue(comp)!;
                return true;
            }
        }
        return false;
    }

    static bool Differs(Float3 a, Float3 b)
        => !AnimationClipEditorModel.Near(a.X, b.X, Epsilon)
           || !AnimationClipEditorModel.Near(a.Y, b.Y, Epsilon)
           || !AnimationClipEditorModel.Near(a.Z, b.Z, Epsilon);

    static bool Differs(Quaternion a, Quaternion b)
        => !AnimationClipEditorModel.Near(a.X, b.X, Epsilon)
           || !AnimationClipEditorModel.Near(a.Y, b.Y, Epsilon)
           || !AnimationClipEditorModel.Near(a.Z, b.Z, Epsilon)
           || !AnimationClipEditorModel.Near(a.W, b.W, Epsilon);
}
