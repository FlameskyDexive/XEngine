using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Ai.Protocol;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>★ M32 — Nested state-machine Echo ops (inspect tree + create/delete/move).</summary>
internal static partial class AnimationAssetOps
{
    const string NestedStateMachineType =
        "XEngine.Animation.AnimatorController+StateMachine, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string NestedLayerType =
        "XEngine.Animation.AnimatorController+Layer, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    static IReadOnlyList<AnimatorStateMachineInfo> ReadStateMachineTrees(EchoObject root)
    {
        var blend = new List<BlendTreeSummary>();
        var trees = new List<AnimatorStateMachineInfo>();
        IReadOnlyList<EchoObject> layerItems = Items(root, "Layers");
        if (layerItems.Count == 0)
        {
            trees.Add(ReadNestedMachine(root, "Base Layer", blend) with { Name = "Base Layer" });
            return trees;
        }
        foreach (EchoObject layer in layerItems)
        {
            EchoObject machine = NestedLayerMachine(layer);
            trees.Add(ReadNestedMachine(machine, Str(layer, "Name", "Base Layer"), blend));
        }
        return trees;
    }

    public static void CreateStateMachine(EchoObject root, string name, string? layerName, string? parentPath, float posX, float posY)
    {
        EchoObject parent = NestedEnsureRoot(root, layerName, parentPath);
        EchoObject list = EnsureList(parent, "StateMachines");
        if (FindNamed(list.List, name) != null) return;
        var sm = NewNestedMachine(name, posX, posY);
        list.ListAdd(sm);
    }

    public static void DeleteStateMachine(EchoObject root, string name, string? layerName, string? parentPath)
    {
        EchoObject parent = NestedResolve(root, layerName, parentPath);
        EchoObject list = EnsureList(parent, "StateMachines");
        int idx = IndexOfName(list.List, name);
        if (idx < 0) return;
        list.ListRemoveAt(idx);
        EchoObject outs = EnsureList(parent, "StateMachineTransitions");
        for (int i = outs.List.Count - 1; i >= 0; i--)
        {
            if (Str(outs.List[i], "SourceStateMachineName") == name
                || Str(outs.List[i], "DestinationStateName") == name)
                outs.ListRemoveAt(i);
        }
    }

    public static void MoveState(EchoObject root, string stateName, string targetStateMachine, string? layerName, string? sourcePath)
    {
        EchoObject src = NestedResolve(root, layerName, sourcePath);
        EchoObject dest = FindNamed(Items(src, "StateMachines"), targetStateMachine)
            ?? throw new KeyNotFoundException($"State machine '{targetStateMachine}' not found.");
        EchoObject states = EnsureList(src, "States");
        int idx = IndexOfName(states.List, stateName);
        if (idx < 0) throw new KeyNotFoundException($"State '{stateName}' not found.");
        EchoObject state = states.List[idx];
        states.ListRemoveAt(idx);
        EnsureList(dest, "States").ListAdd(state);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (EchoObject n in Items(state, "TransitionNames"))
            names.Add(Scalar(n));
        EchoObject srcTrans = EnsureList(src, "Transitions");
        EchoObject destTrans = EnsureList(dest, "Transitions");
        for (int i = srcTrans.List.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(Str(srcTrans.List[i], "Name"))) continue;
            destTrans.ListAdd(srcTrans.List[i]);
            srcTrans.ListRemoveAt(i);
        }

        if (Str(src, "DefaultStateName") == stateName)
            src["DefaultStateName"] = new EchoObject(states.List.Count > 0 ? Str(states.List[0], "Name") : "");
        if (string.IsNullOrEmpty(Str(dest, "DefaultStateName")))
            dest["DefaultStateName"] = new EchoObject(stateName);
    }

    static EchoObject NestedLayerMachine(EchoObject layerOrRoot)
    {
        if (layerOrRoot.TryGet("Root", out EchoObject? nested) && nested is not null
            && nested.TagType == EchoType.Compound)
            return nested;
        return layerOrRoot;
    }

    static EchoObject NestedResolve(EchoObject root, string? layerName, string? path)
    {
        EchoObject sm = NestedLayerMachine(ResolveMachine(root, layerName));
        if (string.IsNullOrEmpty(path))
            return sm;
        string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
        {
            EchoObject? child = FindNamed(Items(sm, "StateMachines"), segs[i]);
            sm = child ?? throw new KeyNotFoundException($"State machine '{segs[i]}' not found.");
        }
        return sm;
    }

    static EchoObject NestedEnsureRoot(EchoObject root, string? layerName, string? parentPath)
    {
        if (!string.IsNullOrEmpty(parentPath))
            return NestedResolve(root, layerName, parentPath);

        EchoObject host = ResolveMachine(root, layerName, createLayer: true);
        if (host.TryGet("Root", out EchoObject? existing) && existing is not null
            && existing.TagType == EchoType.Compound)
            return existing;
        if (Items(host, "StateMachines").Count > 0)
            return host;
        return NestedPromote(root, layerName);
    }

    static EchoObject NestedPromote(EchoObject root, string? layerName)
    {
        EchoObject layers = EnsureList(root, "Layers");
        if (layers.List.Count == 0)
        {
            var layer = EchoObject.NewCompound();
            layer["$type"] = new EchoObject(NestedLayerType);
            layer["Name"] = new EchoObject("Base Layer");
            layer["Weight"] = new EchoObject(1f);
            layer["Blending"] = new EchoObject(0L);
            layer["DefaultStateName"] = new EchoObject(Str(root, "DefaultStateName"));
            var sm = NewNestedMachine("Base Layer", 0f, 0f);
            NestedMoveList(root, sm, "States");
            NestedMoveList(root, sm, "Transitions");
            NestedMoveList(root, sm, "AnyStateTransitions");
            sm["DefaultStateName"] = new EchoObject(Str(root, "DefaultStateName"));
            layer["Root"] = sm;
            layer["States"] = EchoObject.NewList();
            layer["Transitions"] = EchoObject.NewList();
            layer["AnyStateTransitions"] = EchoObject.NewList();
            layers.ListAdd(layer);
            root["DefaultStateName"] = new EchoObject("");
            return sm;
        }

        EchoObject layerNode = FindNamed(layers.List, string.IsNullOrEmpty(layerName) ? "Base Layer" : layerName)
            ?? layers.List[0];
        if (layerNode.TryGet("Root", out EchoObject? r) && r is not null && r.TagType == EchoType.Compound)
            return r;
        var wrapped = NewNestedMachine(Str(layerNode, "Name", "Base Layer"), 0f, 0f);
        wrapped["DefaultStateName"] = new EchoObject(Str(layerNode, "DefaultStateName"));
        NestedMoveList(layerNode, wrapped, "States");
        NestedMoveList(layerNode, wrapped, "Transitions");
        NestedMoveList(layerNode, wrapped, "AnyStateTransitions");
        layerNode["Root"] = wrapped;
        layerNode["States"] = EchoObject.NewList();
        layerNode["Transitions"] = EchoObject.NewList();
        layerNode["AnyStateTransitions"] = EchoObject.NewList();
        return wrapped;
    }

    static EchoObject NewNestedMachine(string name, float posX, float posY)
    {
        var sm = EchoObject.NewCompound();
        sm["$type"] = new EchoObject(NestedStateMachineType);
        sm["Name"] = new EchoObject(name);
        sm["PosX"] = new EchoObject(posX);
        sm["PosY"] = new EchoObject(posY);
        sm["DefaultStateName"] = new EchoObject("");
        sm["States"] = EchoObject.NewList();
        sm["StateMachines"] = EchoObject.NewList();
        sm["Transitions"] = EchoObject.NewList();
        sm["AnyStateTransitions"] = EchoObject.NewList();
        sm["EntryTransitions"] = EchoObject.NewList();
        sm["StateMachineTransitions"] = EchoObject.NewList();
        return sm;
    }

    static void NestedMoveList(EchoObject from, EchoObject to, string field)
    {
        EchoObject src = EnsureList(from, field);
        EchoObject dst = EnsureList(to, field);
        while (src.List.Count > 0)
        {
            EchoObject item = src.List[0];
            src.ListRemoveAt(0);
            dst.ListAdd(item);
        }
    }

    static AnimatorStateMachineInfo ReadNestedMachine(EchoObject sm, string fallbackName, List<BlendTreeSummary> blendTrees)
    {
        var states = new List<AnimatorStateInfo>();
        foreach (EchoObject s in Items(sm, "States"))
        {
            states.Add(ReadState(s));
            BlendTreeSummary? bt = ReadEmbeddedBlendTree(s);
            if (bt != null) blendTrees.Add(bt);
        }
        var children = new List<AnimatorStateMachineInfo>();
        foreach (EchoObject child in Items(sm, "StateMachines"))
            children.Add(ReadNestedMachine(child, Str(child, "Name"), blendTrees));
        var trans = new List<AnimatorTransitionInfo>();
        foreach (EchoObject t in Items(sm, "Transitions"))
            trans.Add(ReadTransition(t));
        var any = new List<AnimatorTransitionInfo>();
        foreach (EchoObject t in Items(sm, "AnyStateTransitions"))
            any.Add(ReadTransition(t));
        var entry = new List<AnimatorTransitionInfo>();
        foreach (EchoObject t in Items(sm, "EntryTransitions"))
            entry.Add(ReadTransition(t));
        var smOut = new List<AnimatorTransitionInfo>();
        foreach (EchoObject t in Items(sm, "StateMachineTransitions"))
            smOut.Add(ReadTransition(t));
        return new AnimatorStateMachineInfo(
            Str(sm, "Name", fallbackName),
            Str(sm, "DefaultStateName"),
            Num(sm, "PosX"), Num(sm, "PosY"),
            states, children, trans, any, entry, smOut);
    }
}
