// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation;

/// <summary>
/// ★ M40 — Bind-time resolution of authored <see cref="AnimationClip.Events"/> to zero-arg
/// <see cref="Action"/> slots. Reflection and delegate allocation happen here (cold);
/// per-frame fire is an indexed invoke.
/// </summary>
internal static class AnimationEventBinder
{
    public static AnimationClipEvent[] Bind(AnimationClip clip, Transform root)
    {
        var authored = clip.Events;
        int n = authored.Count;
        if (n == 0)
            return Array.Empty<AnimationClipEvent>();

        float duration = clip.Duration > 0f ? clip.Duration : 1f;
        var slots = new AnimationClipEvent[n];
        GameObject go = root.GameObject;
        for (int i = 0; i < n; i++)
        {
            AnimationClipEvent e = authored[i];
            float t = e.Time;
            slots[i].Time = t;
            slots[i].NormalizedTime = t / duration;
            slots[i].FunctionName = e.FunctionName;
            slots[i].FloatParam = e.FloatParam;
            slots[i].IntParam = e.IntParam;
            slots[i].StringParam = e.StringParam;
            slots[i].ObjectParam = e.ObjectParam;
            slots[i].Callback = BindFunction(go, e);
        }
        return slots;
    }

    static Action? BindFunction(GameObject root, AnimationClipEvent e)
    {
        if (string.IsNullOrEmpty(e.FunctionName))
            return null;
        return Walk(root, e);
    }

    static Action? Walk(GameObject go, AnimationClipEvent e)
    {
        foreach (MonoBehaviour mb in go.GetComponents())
        {
            if (mb == null) continue;
            Action? a = BindOn(mb, e);
            if (a != null) return a;
        }
        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
        {
            Action? a = Walk(children[i], e);
            if (a != null) return a;
        }
        return null;
    }

    static Action? BindOn(MonoBehaviour mb, AnimationClipEvent e)
    {
        Type t = mb.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string name = e.FunctionName;

        MethodInfo? mf = t.GetMethod(name, flags, binder: null, types: new[] { typeof(float) }, modifiers: null);
        if (mf != null)
        {
            var d = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), mb, mf);
            float v = e.FloatParam;
            return () => d(v);
        }

        MethodInfo? mi = t.GetMethod(name, flags, binder: null, types: new[] { typeof(int) }, modifiers: null);
        if (mi != null)
        {
            var d = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), mb, mi);
            int v = e.IntParam;
            return () => d(v);
        }

        MethodInfo? ms = t.GetMethod(name, flags, binder: null, types: new[] { typeof(string) }, modifiers: null);
        if (ms != null)
        {
            var d = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), mb, ms);
            string v = e.StringParam ?? "";
            return () => d(v);
        }

        MethodInfo? mo = t.GetMethod(name, flags, binder: null, types: new[] { typeof(EngineObject) }, modifiers: null);
        if (mo != null)
        {
            var d = (Action<EngineObject>)Delegate.CreateDelegate(typeof(Action<EngineObject>), mb, mo);
            EngineObject? obj = e.ObjectParam.Res;
            return () => d(obj!);
        }

        MethodInfo? m0 = t.GetMethod(name, flags, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (m0 != null)
            return (Action)Delegate.CreateDelegate(typeof(Action), mb, m0);

        return null;
    }
}
