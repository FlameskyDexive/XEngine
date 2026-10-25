// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

using XEngine.Runtime;
using XEngine.Vector;
using XEngine;
using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Animation;

/// <summary>
/// ★ M38 — Bind-time resolution of <see cref="AnimationClip.GenericCurve"/> tracks. String and
/// reflection work happens here (cold); <see cref="Apply"/> is an indexed loop over prebuilt slots.
/// </summary>
internal static class GenericCurveBinder
{
    // Reflection field writes accept object. Reuse these boxes for Boolean fields on AOT too;
    // no emitted IL, expression compilation or per-frame boxing is required.
    private static readonly object BoxedTrue = true;
    private static readonly object BoxedFalse = false;
    public struct Slot
    {
        public AnimationCurve Curve;
        public Action<float>? SetFloat;
        public Transform? Transform;
        public byte TransformChannel; // 0=none, 1-3 pos, 4-7 rot, 8-10 scale
    }

    public static Slot[] Bind(AnimationClip clip, Transform root)
    {
        var curves = clip.GenericCurves;
        int n = curves.Count;
        if (n == 0)
            return Array.Empty<Slot>();

        var slots = new Slot[n];
        for (int i = 0; i < n; i++)
        {
            AnimationClip.GenericCurve g = curves[i];
            slots[i].Curve = g.Curve;
            Transform? node = string.IsNullOrEmpty(g.Path) ? root : root.Find(g.Path);
            if (node == null)
                continue;

            if (IsTransformType(g.TypeName))
            {
                slots[i].Transform = node;
                slots[i].TransformChannel = TransformChannelId(g.Property);
                continue;
            }

            MonoBehaviour? mb = FindComponent(node.GameObject, g.TypeName);
            if (mb == null)
                continue;
            slots[i].SetFloat = BindFloatSetter(mb, g.Property);
        }
        return slots;
    }

    [HotPath]
    public static void Apply(Slot[] slots, float time)
    {
        int n = slots.Length;
        for (int i = 0; i < n; i++)
        {
            AnimationCurve curve = slots[i].Curve;
            if (curve == null)
                continue;
            float v = curve.Evaluate(time);
            Action<float>? set = slots[i].SetFloat;
            if (set != null)
            {
                set(v);
                continue;
            }
            Transform? t = slots[i].Transform;
            if (t == null)
                continue;
            ApplyTransform(t, slots[i].TransformChannel, v);
        }
    }

    static void ApplyTransform(Transform t, byte channel, float v)
    {
        switch (channel)
        {
            case 1:
            {
                Float3 p = t.LocalPosition;
                t.LocalPosition = new Float3(v, p.Y, p.Z);
                break;
            }
            case 2:
            {
                Float3 p = t.LocalPosition;
                t.LocalPosition = new Float3(p.X, v, p.Z);
                break;
            }
            case 3:
            {
                Float3 p = t.LocalPosition;
                t.LocalPosition = new Float3(p.X, p.Y, v);
                break;
            }
            case 4:
            {
                Quaternion q = t.LocalRotation;
                t.LocalRotation = new Quaternion(v, q.Y, q.Z, q.W);
                break;
            }
            case 5:
            {
                Quaternion q = t.LocalRotation;
                t.LocalRotation = new Quaternion(q.X, v, q.Z, q.W);
                break;
            }
            case 6:
            {
                Quaternion q = t.LocalRotation;
                t.LocalRotation = new Quaternion(q.X, q.Y, v, q.W);
                break;
            }
            case 7:
            {
                Quaternion q = t.LocalRotation;
                t.LocalRotation = new Quaternion(q.X, q.Y, q.Z, v);
                break;
            }
            case 8:
            {
                Float3 s = t.LocalScale;
                t.LocalScale = new Float3(v, s.Y, s.Z);
                break;
            }
            case 9:
            {
                Float3 s = t.LocalScale;
                t.LocalScale = new Float3(s.X, v, s.Z);
                break;
            }
            case 10:
            {
                Float3 s = t.LocalScale;
                t.LocalScale = new Float3(s.X, s.Y, v);
                break;
            }
        }
    }

    static bool IsTransformType(string typeName)
        => typeName == "Transform"
           || typeName.EndsWith(".Transform", StringComparison.Ordinal)
           || typeName == "XEngine.Runtime.Transform";

    static byte TransformChannelId(string property)
    {
        if (property == "localPosition.x" || property == "m_LocalPosition.x" || property == "PosX") return 1;
        if (property == "localPosition.y" || property == "m_LocalPosition.y" || property == "PosY") return 2;
        if (property == "localPosition.z" || property == "m_LocalPosition.z" || property == "PosZ") return 3;
        if (property == "localRotation.x" || property == "m_LocalRotation.x" || property == "RotX") return 4;
        if (property == "localRotation.y" || property == "m_LocalRotation.y" || property == "RotY") return 5;
        if (property == "localRotation.z" || property == "m_LocalRotation.z" || property == "RotZ") return 6;
        if (property == "localRotation.w" || property == "m_LocalRotation.w" || property == "RotW") return 7;
        if (property == "localScale.x" || property == "m_LocalScale.x" || property == "ScaleX") return 8;
        if (property == "localScale.y" || property == "m_LocalScale.y" || property == "ScaleY") return 9;
        if (property == "localScale.z" || property == "m_LocalScale.z" || property == "ScaleZ") return 10;
        return 0;
    }

    static MonoBehaviour? FindComponent(GameObject go, string typeName)
    {
        foreach (MonoBehaviour comp in go.GetComponents())
        {
            Type t = comp.GetType();
            if (t.Name == typeName || t.FullName == typeName)
                return comp;
        }
        return null;
    }

    static Action<float>? BindFloatSetter(MonoBehaviour target, string property)
    {
        if (target is Light light && property == "Intensity")
            return v => light.Intensity = v;
        if (target is Light light2 && property == "ShadowStrength")
            return v => light2.ShadowStrength = v;

        Type t = target.GetType();
        PropertyInfo? p = t.GetProperty(property, BindingFlags.Instance | BindingFlags.Public);
        if (p != null && p.PropertyType == typeof(float) && p.SetMethod != null)
            return (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), target, p.SetMethod);
        if (p != null && p.PropertyType == typeof(bool) && p.SetMethod != null)
        {
            var set = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), target, p.SetMethod);
            return v => set(MathF.Abs(v) > 0.001f);
        }

        FieldInfo? f = t.GetField(property, BindingFlags.Instance | BindingFlags.Public);
        if (f != null && f.FieldType == typeof(float))
        {
            object boxed = target;
            return v => f.SetValue(boxed, v);
        }
        if (f != null && f.FieldType == typeof(bool) && !f.IsInitOnly)
            return v => f.SetValue(target, MathF.Abs(v) > 0.001f ? BoxedTrue : BoxedFalse);
        return null;
    }
}
