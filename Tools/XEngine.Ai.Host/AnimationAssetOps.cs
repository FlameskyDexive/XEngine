using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using XEngine.Ai.Protocol;
using XEngine.Animation;
using XEngine.Data.Provenance;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ M31 — Engine-agnostic Echo walkers for .controller / .anim / .motion. Inspect returns typed
/// DTOs + contentHash; mutators edit the tree in place for <see cref="SceneWriter.MutateAssetDataAsync"/>.
/// </summary>
internal static partial class AnimationAssetOps
{
    const string StateType =
        "XEngine.Animation.AnimatorController+State, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string TransitionType =
        "XEngine.Animation.AnimatorController+Transition, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string ParameterType =
        "XEngine.Animation.AnimatorController+Parameter, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string LayerType =
        "XEngine.Animation.AnimatorController+Layer, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string ConditionType =
        "XEngine.Animation.AnimatorController+Condition, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string CurveType =
        "XEngine.Runtime.AnimationCurve, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    static readonly string[] CurveChannels =
        { "PosX", "PosY", "PosZ", "RotX", "RotY", "RotZ", "RotW", "ScaleX", "ScaleY", "ScaleZ" };

    public static AnimatorInspectResult InspectController(string text, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        EchoObject machine = ResolveMachine(root, layerName: null);

        var states = new List<AnimatorStateInfo>();
        var blendTrees = new List<BlendTreeSummary>();
        foreach (EchoObject s in Items(machine, "States"))
        {
            states.Add(ReadState(s));
            BlendTreeSummary? bt = ReadEmbeddedBlendTree(s);
            if (bt != null) blendTrees.Add(bt);
        }

        var transitions = new List<AnimatorTransitionInfo>();
        foreach (EchoObject t in Items(machine, "Transitions"))
            transitions.Add(ReadTransition(t));

        var any = new List<AnimatorTransitionInfo>();
        foreach (EchoObject t in Items(machine, "AnyStateTransitions"))
            any.Add(ReadTransition(t));

        var parameters = new List<AnimatorParameterInfo>();
        foreach (EchoObject p in Items(root, "Parameters"))
            parameters.Add(ReadParameter(p));

        var layers = new List<AnimatorLayerInfo>();
        IReadOnlyList<EchoObject> layerItems = Items(root, "Layers");
        if (layerItems.Count == 0)
        {
            layers.Add(new AnimatorLayerInfo("Base Layer", 1f, "Override",
                Str(root, "DefaultStateName"), states.Count));
        }
        else
        {
            if (Items(root, "States").Count > 0)
            {
                layers.Add(new AnimatorLayerInfo("Base Layer", 1f, "Override",
                    Str(root, "DefaultStateName"), Items(root, "States").Count));
            }
            foreach (EchoObject layer in layerItems)
            {
                layers.Add(new AnimatorLayerInfo(
                    Str(layer, "Name", "Base Layer"),
                    Num(layer, "Weight", 1f),
                    BlendModeName(Num(layer, "Blending", 0f)),
                    Str(layer, "DefaultStateName"),
                    Items(layer, "States").Count,
                    Root: null,
                    SyncedLayerIndex: (int)Num(layer, "SyncedLayerIndex", -1f),
                    SyncedLayerAffectsTiming: Bool(layer, "SyncedLayerAffectsTiming"),
                    IKPass: Bool(layer, "IKPass")));
            }
        }

        return new AnimatorInspectResult(
            targetAsset,
            Str(root, "Name", Path.GetFileNameWithoutExtension(targetAsset)),
            Str(root, "DefaultStateName"),
            layers, states, transitions, any, parameters, blendTrees,
            ReadStateMachineTrees(root),
            ProvenancedDelta.ContentHash(root));
    }

    public static ClipInspectResult InspectClip(string text, string targetAsset, IReadOnlyList<string>? availableGuids = null)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        var bindings = new List<ClipBindingInfo>();
        foreach (EchoObject bone in Items(root, "Bones"))
        {
            string path = Str(bone, "BoneName");
            foreach (string ch in CurveChannels)
            {
                if (!bone.TryGet(ch, out EchoObject? curve) || curve is null) continue;
                ClipBindingInfo? info = ReadCurveBinding(path, ch, curve);
                if (info != null) bindings.Add(info);
            }
        }

        foreach (EchoObject shape in Items(root, "BlendShapes"))
        {
            string path = Str(shape, "Path") + "/" + Str(shape, "ShapeName");
            if (shape.TryGet("Weight", out EchoObject? w) && w is not null)
            {
                ClipBindingInfo? info = ReadCurveBinding(path, "Weight", w);
                if (info != null) bindings.Add(info);
            }
        }

        foreach (EchoObject g in Items(root, "GenericCurves"))
        {
            string path = Str(g, "Path");
            string typeName = Str(g, "TypeName");
            string property = Str(g, "Property");
            if (g.TryGet("Curve", out EchoObject? curve) && curve is not null)
            {
                ClipBindingInfo? info = ReadCurveBinding(path, property, curve);
                if (info != null)
                    bindings.Add(info with { TypeName = typeName, Property = property, Channel = property });
            }
        }

        return new ClipInspectResult(
            targetAsset,
            Str(root, "Name", Path.GetFileNameWithoutExtension(targetAsset)),
            Num(root, "Duration"),
            Num(root, "TicksPerSecond", 30f),
            WrapName(Num(root, "Wrap")),
            bindings,
            ReadClipEvents(root),
            availableGuids ?? Array.Empty<string>(),
            ProvenancedDelta.ContentHash(root));
    }

    /// <summary>★ Gen-P4.2 — Rename humanoid bone tracks to Unity canonical names, or to names
    /// taken from <paramref name="likeRoot"/> (another clip that already plays on the dest rig).</summary>
    public static ClipRetargetStats RetargetClip(EchoObject root, EchoObject? likeRoot)
    {
        Dictionary<HumanoidBone, string>? dest = null;
        if (likeRoot is not null)
        {
            dest = new Dictionary<HumanoidBone, string>();
            foreach (EchoObject bone in Items(likeRoot, "Bones"))
            {
                string name = Str(bone, "BoneName");
                if (HumanoidBoneMap.TryResolve(name, out HumanoidBone human) && !dest.ContainsKey(human))
                    dest[human] = name;
            }
        }

        int remapped = 0;
        var unmapped = new List<string>();
        foreach (EchoObject bone in Items(root, "Bones"))
        {
            string name = Str(bone, "BoneName");
            if (!HumanoidBoneMap.TryResolve(name, out HumanoidBone human))
            {
                unmapped.Add(name);
                continue;
            }

            string next = dest is not null && dest.TryGetValue(human, out string? destName)
                ? destName
                : HumanoidBoneMap.CanonicalName(human);
            if (string.Equals(next, name, StringComparison.Ordinal))
                continue;
            bone["BoneName"] = new EchoObject(next);
            remapped++;
        }

        return new ClipRetargetStats(remapped, unmapped);
    }

    public static BlendTreeInspectResult InspectBlendTree(string text, string targetAsset, string? stateName)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        EchoObject tree = root;
        string? owner = stateName;
        if (LooksLikeController(root))
        {
            EchoObject? state = FindNamed(Items(ResolveMachine(root, null), "States"), stateName
                ?? throw new ArgumentException("blendtree_inspect on a .controller requires stateName."));
            tree = EmbeddedTree(state)
                ?? throw new KeyNotFoundException($"State '{stateName}' has no embedded BlendTree.");
            owner = Str(state, "Name");
        }

        return new BlendTreeInspectResult(
            targetAsset, owner,
            BlendTypeName(Num(tree, "Type")),
            Str(tree, "ParameterName"),
            ReadChildren(tree),
            ProvenancedDelta.ContentHash(root),
            Str(tree, "ParameterNameY"),
            Bool(tree, "UseAutomaticThresholds"));
    }

    public static IReadOnlyList<string> CollectRootClipGuids(string sceneText, string objectIdentifier)
    {
        EchoObject scene = EchoObject.ReadFromString(sceneText);
        EchoObject? go = EchoFacade.FindGameObject(scene, objectIdentifier)
            ?? throw new KeyNotFoundException($"GameObject '{objectIdentifier}' not found.");
        var guids = new List<string>();
        foreach (EchoObject comp in EchoFacade.GetComponentsList(go).List)
        {
            CollectGuid(comp, "Clip", guids);
            CollectGuid(comp, "DefaultClip", guids);
            if (comp.TryGet("Clips", out EchoObject? clips))
            {
                foreach (EchoObject item in EnumerateList(clips!))
                    CollectGuidFromRef(item, guids);
            }
        }
        return guids;
    }

    public static void CreateState(EchoObject root, string name, string? layerName, string? clipGuid)
    {
        EchoObject machine = ResolveMachine(root, layerName, createLayer: true);
        EchoObject list = EnsureList(machine, "States");
        if (FindNamed(list.List, name) != null) return;
        var state = EchoObject.NewCompound();
        state["$type"] = new EchoObject(StateType);
        state["Name"] = new EchoObject(name);
        state["Tag"] = new EchoObject("");
        state["Speed"] = new EchoObject(1f);
        state["IsLooping"] = new EchoObject(true);
        state["PosX"] = new EchoObject(0f);
        state["PosY"] = new EchoObject(0f);
        var motion = EchoObject.NewCompound();
        motion["AssetID"] = new EchoObject(string.IsNullOrEmpty(clipGuid) ? Guid.Empty.ToString() : clipGuid);
        state["Motion"] = motion;
        state["TransitionNames"] = EchoObject.NewList();
        list.ListAdd(state);
        if (string.IsNullOrEmpty(Str(machine, "DefaultStateName")))
            machine["DefaultStateName"] = new EchoObject(name);
        if (string.IsNullOrEmpty(Str(root, "DefaultStateName")))
            root["DefaultStateName"] = new EchoObject(name);
    }

    public static void PatchState(EchoObject root, string name, IReadOnlyList<SceneFieldPatch> patches, string? layerName)
    {
        EchoObject state = RequireNamed(Items(ResolveMachine(root, layerName), "States"), name, "state");
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(state, p.Path, p.Value);
    }

    public static void DeleteState(EchoObject root, string name, string? layerName)
    {
        EchoObject machine = ResolveMachine(root, layerName);
        EchoObject list = EnsureList(machine, "States");
        int idx = IndexOfName(list.List, name);
        if (idx < 0) return;
        list.ListRemoveAt(idx);

        EchoObject trans = EnsureList(machine, "Transitions");
        for (int i = trans.List.Count - 1; i >= 0; i--)
        {
            if (Str(trans.List[i], "DestinationStateName") == name)
                trans.ListRemoveAt(i);
        }

        foreach (EchoObject s in list.List)
        {
            EchoObject names = EnsureList(s, "TransitionNames");
            for (int i = names.List.Count - 1; i >= 0; i--)
            {
                if (Scalar(names.List[i]) == name)
                    names.ListRemoveAt(i);
            }
        }

        if (Str(machine, "DefaultStateName") == name)
            machine["DefaultStateName"] = new EchoObject(list.List.Count > 0 ? Str(list.List[0], "Name") : "");
    }

    public static void CreateTransition(EchoObject root, string from, string to, string? name, string? layerName)
    {
        EchoObject machine = ResolveMachine(root, layerName);
        EchoObject? src = FindNamed(Items(machine, "States"), from)
            ?? throw new KeyNotFoundException($"Source state '{from}' not found.");
        if (FindNamed(Items(machine, "States"), to) is null)
            throw new KeyNotFoundException($"Destination state '{to}' not found.");

        string id = string.IsNullOrEmpty(name) ? $"{from}To{to}" : name!;
        EchoObject list = EnsureList(machine, "Transitions");
        if (FindNamed(list.List, id) != null) return;

        var t = EchoObject.NewCompound();
        t["$type"] = new EchoObject(TransitionType);
        t["Name"] = new EchoObject(id);
        t["DestinationStateName"] = new EchoObject(to);
        t["Duration"] = new EchoObject(0.1f);
        t["Offset"] = new EchoObject(0f);
        t["HasExitTime"] = new EchoObject(true);
        t["ExitTime"] = new EchoObject(0.9f);
        t["Solo"] = new EchoObject(false);
        t["Mute"] = new EchoObject(false);
        t["HasFixedDuration"] = new EchoObject(true);
        t["InterruptionSource"] = new EchoObject(0L);
        t["OrderedInterruption"] = new EchoObject(true);
        t["CanTransitionToSelf"] = new EchoObject(false);
        t["Conditions"] = EchoObject.NewList();
        list.ListAdd(t);

        EchoObject names = EnsureList(src, "TransitionNames");
        foreach (EchoObject n in names.List)
            if (Scalar(n) == id) return;
        names.ListAdd(new EchoObject(id));
    }

    public static void PatchTransition(EchoObject root, string name, IReadOnlyList<SceneFieldPatch> patches, string? layerName)
    {
        EchoObject t = RequireNamed(Items(ResolveMachine(root, layerName), "Transitions"), name, "transition");
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(t, p.Path, p.Value);
    }

    public static void ReorderStateTransitions(EchoObject root, string stateName, IReadOnlyList<string> orderedNames, string? layerName)
    {
        EchoObject state = RequireNamed(Items(ResolveMachine(root, layerName), "States"), stateName, "state");
        EchoObject names = EnsureList(state, "TransitionNames");
        while (names.List.Count > 0)
            names.ListRemoveAt(names.List.Count - 1);
        for (int i = 0; i < orderedNames.Count; i++)
            names.ListAdd(new EchoObject(orderedNames[i]));
    }

    public static void DeleteTransition(EchoObject root, string name, string? layerName)
    {
        EchoObject machine = ResolveMachine(root, layerName);
        EchoObject list = EnsureList(machine, "Transitions");
        int idx = IndexOfName(list.List, name);
        if (idx < 0) return;
        list.ListRemoveAt(idx);
        foreach (EchoObject s in Items(machine, "States"))
        {
            EchoObject names = EnsureList(s, "TransitionNames");
            for (int i = names.List.Count - 1; i >= 0; i--)
                if (Scalar(names.List[i]) == name)
                    names.ListRemoveAt(i);
        }
    }

    public static void AddParameter(EchoObject root, string name, string type)
    {
        EchoObject list = EnsureList(root, "Parameters");
        if (FindNamed(list.List, name) != null) return;
        var p = EchoObject.NewCompound();
        p["$type"] = new EchoObject(ParameterType);
        p["Name"] = new EchoObject(name);
        p["Type"] = new EchoObject((long)ParseParamType(type));
        p["DefaultFloat"] = new EchoObject(0f);
        p["DefaultInt"] = new EchoObject(0);
        p["DefaultBool"] = new EchoObject(false);
        list.ListAdd(p);
    }

    public static void RemoveParameter(EchoObject root, string name)
    {
        EchoObject list = EnsureList(root, "Parameters");
        int idx = IndexOfName(list.List, name);
        if (idx >= 0) list.ListRemoveAt(idx);
    }

    public static void AddLayer(EchoObject root, string name)
    {
        EchoObject list = EnsureList(root, "Layers");
        if (FindNamed(list.List, name) != null) return;
        var layer = EchoObject.NewCompound();
        layer["$type"] = new EchoObject(LayerType);
        layer["Name"] = new EchoObject(name);
        layer["Weight"] = new EchoObject(1f);
        layer["Blending"] = new EchoObject(0L);
        layer["DefaultStateName"] = new EchoObject("");
        layer["States"] = EchoObject.NewList();
        layer["Transitions"] = EchoObject.NewList();
        layer["AnyStateTransitions"] = EchoObject.NewList();
        list.ListAdd(layer);
    }

    public static void PatchLayer(EchoObject root, string name, IReadOnlyList<SceneFieldPatch> patches)
    {
        EchoObject layer = RequireNamed(Items(root, "Layers"), name, "layer");
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(layer, p.Path, p.Value);
    }

    public static void PatchClip(EchoObject root, IReadOnlyList<SceneFieldPatch> patches)
    {
        foreach (SceneFieldPatch p in patches)
        {
            string path = p.Path;
            if (string.Equals(path, "SampleRate", StringComparison.OrdinalIgnoreCase))
                path = "TicksPerSecond";
            if (!IsClipPatchField(path))
                throw new ArgumentException($"clip_patch cannot write '{p.Path}'. Use TicksPerSecond (or SampleRate), Duration, Wrap, Name, RootBonePath.");
            EchoFacade.SetPathValue(root, path, p.Value);
        }
    }

    public static void AddClipEvent(
        EchoObject root, float time, string functionName,
        float floatParam = 0f, int intParam = 0, string? stringParam = null, string? objectGuid = null)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("clip_event_add requires functionName.");
        var node = EchoObject.NewCompound();
        node["Time"] = new EchoObject(time);
        node["FunctionName"] = new EchoObject(functionName);
        node["FloatParam"] = new EchoObject(floatParam);
        node["IntParam"] = new EchoObject(intParam);
        node["StringParam"] = new EchoObject(stringParam ?? "");
        if (!string.IsNullOrEmpty(objectGuid))
            node["ObjectParam"] = new EchoObject(objectGuid);
        EnsureList(root, "Events").ListAdd(node);
    }

    public static void PatchClipEvent(EchoObject root, int index, IReadOnlyList<SceneFieldPatch> patches)
    {
        EchoObject node = RequireEvent(root, index);
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(node, p.Path, p.Value);
    }

    public static void RemoveClipEvent(EchoObject root, int index)
    {
        EchoObject list = EnsureList(root, "Events");
        if (index < 0 || index >= list.List.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Event index {index} is out of range.");
        list.ListRemoveAt(index);
    }

    static EchoObject RequireEvent(EchoObject root, int index)
    {
        IReadOnlyList<EchoObject> events = Items(root, "Events");
        if (index < 0 || index >= events.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Event index {index} is out of range.");
        return events[index];
    }

    static List<string> ReadClipEvents(EchoObject root)
    {
        var list = new List<string>();
        foreach (EchoObject e in Items(root, "Events"))
        {
            string fn = Str(e, "FunctionName");
            float t = Num(e, "Time");
            list.Add(fn + "@" + t.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return list;
    }

    static bool IsClipPatchField(string path)
        => string.Equals(path, "TicksPerSecond", StringComparison.OrdinalIgnoreCase)
           || string.Equals(path, "Duration", StringComparison.OrdinalIgnoreCase)
           || string.Equals(path, "Wrap", StringComparison.OrdinalIgnoreCase)
           || string.Equals(path, "Name", StringComparison.OrdinalIgnoreCase)
           || string.Equals(path, "RootBonePath", StringComparison.OrdinalIgnoreCase);

    public static void PatchCurve(
        EchoObject root, string bone, string channel, string op, float time, float? value, string? typeName = null)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            PatchGenericCurve(root, bone ?? "", typeName, channel, op, time, value);
            return;
        }

        EchoObject? boneNode = FindBone(root, bone);
        if (boneNode is null)
        {
            if (!string.Equals(op, "add", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(op, "set", StringComparison.OrdinalIgnoreCase))
                throw new KeyNotFoundException($"Bone '{bone}' not found.");
            boneNode = EchoObject.NewCompound();
            boneNode["BoneName"] = new EchoObject(bone);
            EnsureList(root, "Bones").ListAdd(boneNode);
        }

        if (!IsChannel(channel))
            throw new ArgumentException($"Unknown channel '{channel}'. Use PosX/Y/Z, RotX/Y/Z/W, ScaleX/Y/Z, or pass typeName for a generic float property.");

        MutateKeys(EnsureList(EnsureCurve(boneNode, channel), "Keys"), op, time, value);
    }

    static void PatchGenericCurve(
        EchoObject root, string path, string typeName, string property, string op, float time, float? value)
    {
        if (string.IsNullOrWhiteSpace(property))
            throw new ArgumentException("Generic clip_curve_patch requires a property name in channel.");
        EchoObject? node = FindGeneric(root, path, typeName, property);
        if (node is null)
        {
            if (!string.Equals(op, "add", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(op, "set", StringComparison.OrdinalIgnoreCase))
                throw new KeyNotFoundException($"Generic curve '{path}' {typeName}.{property} not found.");
            node = EchoObject.NewCompound();
            node["Path"] = new EchoObject(path ?? "");
            node["TypeName"] = new EchoObject(typeName);
            node["Property"] = new EchoObject(property);
            EnsureList(root, "GenericCurves").ListAdd(node);
        }

        MutateKeys(EnsureList(EnsureCurve(node, "Curve"), "Keys"), op, time, value);
    }

    static EchoObject? FindGeneric(EchoObject root, string path, string typeName, string property)
    {
        foreach (EchoObject g in Items(root, "GenericCurves"))
        {
            if (Str(g, "Path") == (path ?? "")
                && Str(g, "TypeName") == typeName
                && Str(g, "Property") == property)
                return g;
        }
        return null;
    }

    static void MutateKeys(EchoObject keys, string op, float time, float? value)
    {
        int hit = FindKey(keys, time);
        string kind = op.Trim().ToLowerInvariant();
        if (kind == "remove")
        {
            if (hit >= 0) keys.ListRemoveAt(hit);
            return;
        }

        if (kind != "add" && kind != "set")
            throw new ArgumentException("clip_curve_patch op must be add, set, or remove.");

        float v = value ?? 0f;
        if (hit >= 0)
        {
            keys.List[hit]["Value"] = new EchoObject(v);
            keys.List[hit]["Position"] = new EchoObject(time);
            return;
        }

        var key = EchoObject.NewCompound();
        key["Position"] = new EchoObject(time);
        key["Value"] = new EchoObject(v);
        key["TangentIn"] = new EchoObject(0f);
        key["TangentOut"] = new EchoObject(0f);
        key["Continuity"] = new EchoObject(0);
        int insert = 0;
        while (insert < keys.List.Count && Num(keys.List[insert], "Position") < time)
            insert++;
        keys.ListInsert(insert, key);
    }

    public static void PatchBlendTree(EchoObject root, string? stateName, IReadOnlyList<SceneFieldPatch> patches)
    {
        EchoObject tree = root;
        if (LooksLikeController(root))
        {
            EchoObject? state = FindNamed(Items(ResolveMachine(root, null), "States"), stateName
                ?? throw new ArgumentException("blendtree_patch on a .controller requires stateName."));
            tree = EmbeddedTree(state)
                ?? throw new KeyNotFoundException($"State '{stateName}' has no embedded BlendTree.");
        }
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(tree, p.Path, p.Value);
    }

    static EchoObject ResolveMachine(EchoObject root, string? layerName, bool createLayer = false)
    {
        IReadOnlyList<EchoObject> layers = Items(root, "Layers");
        if (string.IsNullOrEmpty(layerName) || layerName == "Base Layer")
        {
            // Legacy .controller files keep States on the root. Adding extra layers must not
            // steal those states — only fall through to Layers[0] when the root has none.
            if (layers.Count == 0 || Items(root, "States").Count > 0)
                return root;
            EchoObject? named = FindNamed(layers, "Base Layer");
            return named ?? layers[0];
        }

        EchoObject? found = FindNamed(layers, layerName);
        if (found != null) return found;
        if (!createLayer)
            throw new KeyNotFoundException($"Layer '{layerName}' not found.");
        AddLayer(root, layerName);
        return FindNamed(Items(root, "Layers"), layerName)!;
    }

    static AnimatorStateInfo ReadState(EchoObject s)
    {
        var names = new List<string>();
        foreach (EchoObject n in Items(s, "TransitionNames"))
            names.Add(Scalar(n));
        var behaviours = new List<string>();
        foreach (EchoObject n in Items(s, "Behaviours"))
            behaviours.Add(Scalar(n));
        string? motion = null;
        if (s.TryGet("Motion", out EchoObject? m) && m is not null)
            motion = GetAssetId(m);
        string? btParam = null;
        EchoObject? tree = EmbeddedTree(s);
        if (tree != null) btParam = Str(tree, "ParameterName");
        return new AnimatorStateInfo(
            Str(s, "Name"), Str(s, "Tag"), Num(s, "Speed", 1f), Bool(s, "IsLooping", true),
            motion, btParam, names, Num(s, "PosX"), Num(s, "PosY"),
            Bool(s, "WriteDefaults", true), Bool(s, "Mirror"), Num(s, "CycleOffset"),
            Bool(s, "IKOnFeet"),
            Str(s, "SpeedMultiplierParameter"), Str(s, "MirrorParameter"),
            Str(s, "CycleOffsetParameter"), Str(s, "TimeParameter"),
            behaviours);
    }

    static AnimatorTransitionInfo ReadTransition(EchoObject t)
    {
        var conds = new List<AnimatorConditionInfo>();
        foreach (EchoObject c in Items(t, "Conditions"))
        {
            conds.Add(new AnimatorConditionInfo(
                ConditionModeName(Num(c, "Mode")),
                Str(c, "ParamName"),
                Num(c, "Threshold")));
        }
        return new AnimatorTransitionInfo(
            Str(t, "Name"), Str(t, "DestinationStateName"),
            Num(t, "Duration", 0.1f), Num(t, "Offset"),
            Bool(t, "HasExitTime"), Num(t, "ExitTime", 0.9f), conds,
            Str(t, "SourceStateMachineName"),
            Bool(t, "Solo"), Bool(t, "Mute"),
            Bool(t, "HasFixedDuration", true),
            InterruptName(Num(t, "InterruptionSource")),
            Bool(t, "OrderedInterruption", true),
            Bool(t, "CanTransitionToSelf"));
    }

    static AnimatorParameterInfo ReadParameter(EchoObject p)
        => new(Str(p, "Name"), ParamTypeName(Num(p, "Type")), Num(p, "DefaultFloat"),
            (int)Num(p, "DefaultInt"), Bool(p, "DefaultBool"));

    static BlendTreeSummary? ReadEmbeddedBlendTree(EchoObject state)
    {
        EchoObject? tree = EmbeddedTree(state);
        if (tree is null) return null;
        return new BlendTreeSummary(Str(state, "Name"), BlendTypeName(Num(tree, "Type")),
            Str(tree, "ParameterName"), ReadChildren(tree));
    }

    static List<BlendTreeChildInfo> ReadChildren(EchoObject tree)
    {
        var children = new List<BlendTreeChildInfo>();
        foreach (EchoObject c in Items(tree, "Children"))
        {
            string? guid = null;
            string? treeGuid = null;
            if (c.TryGet("Clip", out EchoObject? clip) && clip is not null)
                guid = GetAssetId(clip);
            if (c.TryGet("Tree", out EchoObject? nested) && nested is not null)
                treeGuid = GetAssetId(nested);
            float x = 0, y = 0;
            if (c.TryGet("Position", out EchoObject? pos) && pos is not null)
            {
                x = Num(pos, "X");
                y = Num(pos, "Y");
            }
            children.Add(new BlendTreeChildInfo(guid, Num(c, "Threshold"), x, y, treeGuid, Str(c, "DirectParameterName")));
        }
        return children;
    }

    static ClipBindingInfo? ReadCurveBinding(string path, string channel, EchoObject curve)
    {
        IReadOnlyList<EchoObject> keys = Items(curve, "Keys");
        if (keys.Count == 0) return null;
        float min = float.MaxValue, max = float.MinValue;
        foreach (EchoObject k in keys)
        {
            float v = Num(k, "Value");
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return new ClipBindingInfo(path, channel, keys.Count, min, max);
    }

    static EchoObject? EmbeddedTree(EchoObject? state)
    {
        if (state is null) return null;
        if (!state.TryGet("BlendTree", out EchoObject? bt) || bt is null) return null;
        if (bt.TryGet("Instance", out EchoObject? inst) && inst is not null && inst.TagType == EchoType.Compound)
            return inst;
        if (bt.TryGet("Type", out _)) return bt;
        return null;
    }

    static EchoObject? FindBone(EchoObject root, string name)
    {
        foreach (EchoObject b in Items(root, "Bones"))
            if (Str(b, "BoneName") == name) return b;
        return null;
    }

    static EchoObject EnsureCurve(EchoObject bone, string channel)
    {
        if (bone.TryGet(channel, out EchoObject? existing) && existing is not null
            && existing.TagType == EchoType.Compound)
            return existing;
        var curve = EchoObject.NewCompound();
        curve["$type"] = new EchoObject(CurveType);
        curve["PreLoop"] = new EchoObject(0);
        curve["PostLoop"] = new EchoObject(0);
        curve["Keys"] = EchoObject.NewList();
        bone[channel] = curve;
        return curve;
    }

    static int FindKey(EchoObject keys, float time)
    {
        for (int i = 0; i < keys.List.Count; i++)
            if (Math.Abs(Num(keys.List[i], "Position") - time) < 1e-4f)
                return i;
        return -1;
    }

    static bool IsChannel(string ch)
    {
        foreach (string c in CurveChannels)
            if (string.Equals(c, ch, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static IReadOnlyList<EchoObject> Items(EchoObject parent, string field)
    {
        if (!parent.TryGet(field, out EchoObject? node) || node is null)
            return Array.Empty<EchoObject>();
        return EnumerateList(node);
    }

    static List<EchoObject> EnumerateList(EchoObject node)
    {
        if (node.TagType == EchoType.List) return node.List;
        if (node.TryGet("$values", out EchoObject? values) && values!.TagType == EchoType.List)
            return values.List;
        return new List<EchoObject>();
    }

    static EchoObject EnsureList(EchoObject parent, string field)
    {
        if (parent.TryGet(field, out EchoObject? node) && node is not null)
        {
            if (node.TagType == EchoType.List) return node;
            if (node.TryGet("$values", out EchoObject? values) && values!.TagType == EchoType.List)
                return values;
            var made = EchoObject.NewList();
            node["$values"] = made;
            return made;
        }
        var list = EchoObject.NewList();
        parent[field] = list;
        return list;
    }

    static EchoObject? FindNamed(IReadOnlyList<EchoObject> items, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        for (int i = 0; i < items.Count; i++)
            if (Str(items[i], "Name") == name) return items[i];
        return null;
    }

    static EchoObject RequireNamed(IReadOnlyList<EchoObject> items, string name, string kind)
        => FindNamed(items, name) ?? throw new KeyNotFoundException($"{kind} '{name}' not found.");

    static int IndexOfName(IReadOnlyList<EchoObject> items, string name)
    {
        for (int i = 0; i < items.Count; i++)
            if (Str(items[i], "Name") == name) return i;
        return -1;
    }

    static bool LooksLikeController(EchoObject root)
    {
        string t = Str(root, "$type");
        return t.Contains("AnimatorController", StringComparison.Ordinal);
    }

    static void CollectGuid(EchoObject node, string field, List<string> dst)
    {
        if (!node.TryGet(field, out EchoObject? r) || r is null) return;
        CollectGuidFromRef(r, dst);
    }

    static void CollectGuidFromRef(EchoObject r, List<string> dst)
    {
        string? id = GetAssetId(r);
        if (string.IsNullOrEmpty(id) || id == Guid.Empty.ToString()) return;
        for (int i = 0; i < dst.Count; i++)
            if (dst[i] == id) return;
        dst.Add(id);
    }

    static string? GetAssetId(EchoObject node)
    {
        if (node.TagType == EchoType.String) return node.StringValue;
        if (node.TryGet("AssetID", out EchoObject? id) && id is not null)
        {
            if (id.TagType == EchoType.String) return id.StringValue;
            if (id.TryGet("$v", out EchoObject? v) && v is not null)
                return v.TagType == EchoType.String ? v.StringValue : Scalar(v);
        }
        if (node.TryGet("$v", out EchoObject? raw) && raw is not null)
            return raw.TagType == EchoType.String ? raw.StringValue : Scalar(raw);
        return null;
    }

    static string Str(EchoObject o, string key, string def = "")
        => o.TryGet(key, out EchoObject? v) && v is not null && v.TagType == EchoType.String ? v.StringValue : def;

    static string Scalar(EchoObject v) => v.TagType == EchoType.String ? v.StringValue : v.ToString() ?? "";

    static bool Bool(EchoObject o, string key, bool def = false)
    {
        if (!o.TryGet(key, out EchoObject? v) || v is null) return def;
        return v.TagType switch
        {
            EchoType.Bool => v.BoolValue,
            EchoType.Byte => v.ByteValue != 0,
            EchoType.Int => v.IntValue != 0,
            _ => def,
        };
    }

    static float Num(EchoObject o, string key, float def = 0f)
    {
        if (!o.TryGet(key, out EchoObject? v) || v is null) return def;
        return v.TagType switch
        {
            EchoType.Float => v.FloatValue,
            EchoType.Double => (float)v.DoubleValue,
            EchoType.Int => v.IntValue,
            EchoType.Long => v.LongValue,
            EchoType.Byte => v.ByteValue,
            _ => float.TryParse(v.StringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : def,
        };
    }

    static string ParamTypeName(float t) => (int)t switch { 1 => "Int", 2 => "Bool", 3 => "Trigger", _ => "Float" };
    static int ParseParamType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "int" => 1,
        "bool" => 2,
        "trigger" => 3,
        _ => 0,
    };
    static string BlendTypeName(float t) => (int)t switch
    {
        1 => "TwoDCartesian",
        2 => "TwoDDirectional",
        3 => "Direct",
        _ => "OneD",
    };
    static string BlendModeName(float t) => (int)t == 1 ? "Additive" : "Override";
    static string WrapName(float t) => (int)t switch { 1 => "Loop", 2 => "PingPong", 3 => "ClampForever", _ => "Once" };
    static string ConditionModeName(float t) => (int)t switch
    {
        1 => "IfNot",
        2 => "Greater",
        3 => "Less",
        4 => "Equals",
        5 => "NotEqual",
        _ => "If",
    };
    static string InterruptName(float t) => (int)t switch
    {
        1 => "CurrentState",
        2 => "NextState",
        3 => "CurrentStateThenNextState",
        4 => "NextStateThenCurrentState",
        _ => "None",
    };
}
