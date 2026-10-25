using System;
using System.Collections.Generic;

using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>★ M37 — Offline animator_validate diagnostics.</summary>
internal static partial class AnimationAssetOps
{
    public static AnimatorValidateResult ValidateController(string text, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        var diagnostics = new List<AnimatorDiagnostic>();
        var paramNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (EchoObject p in Items(root, "Parameters"))
        {
            string n = Str(p, "Name");
            if (!string.IsNullOrEmpty(n))
                paramNames.Add(n);
        }

        IReadOnlyList<EchoObject> layerItems = Items(root, "Layers");
        if (layerItems.Count == 0)
        {
            ValidateMachine(root, "Base Layer", "", paramNames, diagnostics);
            if (Items(root, "States").Count == 0 && Items(root, "Root").Count == 0)
            {
                // Root-as-machine already covered.
            }
        }
        else
        {
            if (Items(root, "States").Count > 0)
                ValidateMachine(root, "Base Layer", "", paramNames, diagnostics);
            foreach (EchoObject layer in layerItems)
            {
                string layerName = Str(layer, "Name", "Base Layer");
                float weight = Num(layer, "Weight", 1f);
                if (weight <= 0f)
                {
                    diagnostics.Add(new AnimatorDiagnostic(
                        "layer-weight-zero", "warning",
                        $"Layer '{layerName}' has weight 0 and will not contribute.",
                        Layer: layerName));
                }
                ValidateMachine(NestedLayerMachine(layer), layerName, "", paramNames, diagnostics);
            }
        }

        return new AnimatorValidateResult(targetAsset, diagnostics, ProvenancedDelta.ContentHash(root));
    }

    static void ValidateMachine(
        EchoObject machine,
        string layerName,
        string path,
        HashSet<string> paramNames,
        List<AnimatorDiagnostic> diagnostics)
    {
        var states = new List<EchoObject>();
        foreach (EchoObject s in Items(machine, "States"))
            states.Add(s);

        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < states.Count; i++)
        {
            string n = Str(states[i], "Name");
            if (!string.IsNullOrEmpty(n))
                nameSet.Add(n);
        }

        var incoming = new HashSet<string>(StringComparer.Ordinal);
        string defaultName = Str(machine, "DefaultStateName");
        if (!string.IsNullOrEmpty(defaultName))
            incoming.Add(defaultName);

        ValidateTransitionList(Items(machine, "Transitions"), layerName, path, paramNames, nameSet, incoming, diagnostics, anyState: false);
        ValidateTransitionList(Items(machine, "AnyStateTransitions"), layerName, path, paramNames, nameSet, incoming, diagnostics, anyState: true);
        ValidateTransitionList(Items(machine, "EntryTransitions"), layerName, path, paramNames, nameSet, incoming, diagnostics, anyState: true);

        var transByName = new Dictionary<string, EchoObject>(StringComparer.Ordinal);
        foreach (EchoObject t in Items(machine, "Transitions"))
        {
            string tn = Str(t, "Name");
            if (!string.IsNullOrEmpty(tn))
                transByName[tn] = t;
        }

        for (int i = 0; i < states.Count; i++)
        {
            EchoObject s = states[i];
            string stateName = Str(s, "Name");
            string loc = Qualify(layerName, path, stateName);

            bool hasMotion = HasMotion(s);
            bool hasTree = EmbeddedTree(s) != null;
            if (!hasMotion && !hasTree)
            {
                diagnostics.Add(new AnimatorDiagnostic(
                    "missing-motion", "warning",
                    $"State '{loc}' has no motion and no BlendTree.",
                    State: loc, Layer: layerName));
            }

            CheckParam(s, "SpeedMultiplierParameter", loc, layerName, paramNames, diagnostics);
            CheckParam(s, "MirrorParameter", loc, layerName, paramNames, diagnostics);
            CheckParam(s, "CycleOffsetParameter", loc, layerName, paramNames, diagnostics);
            CheckParam(s, "TimeParameter", loc, layerName, paramNames, diagnostics);
            EchoObject? tree = EmbeddedTree(s);
            if (tree != null)
            {
                CheckParam(tree, "ParameterName", loc, layerName, paramNames, diagnostics);
                CheckParam(tree, "ParameterNameY", loc, layerName, paramNames, diagnostics);
            }

            var outgoing = new List<EchoObject>();
            foreach (EchoObject n in Items(s, "TransitionNames"))
            {
                string tn = Scalar(n);
                if (transByName.TryGetValue(tn, out EchoObject? t))
                    outgoing.Add(t);
            }
            bool anySolo = false;
            for (int t = 0; t < outgoing.Count; t++)
            {
                if (Bool(outgoing[t], "Solo") && !Bool(outgoing[t], "Mute"))
                {
                    anySolo = true;
                    break;
                }
            }
            for (int t = 0; t < outgoing.Count; t++)
            {
                EchoObject tr = outgoing[t];
                string tn = Str(tr, "Name");
                if (Bool(tr, "Mute"))
                {
                    diagnostics.Add(new AnimatorDiagnostic(
                        "muted-transition", "warning",
                        $"Transition '{tn}' on '{loc}' is muted.",
                        State: loc, Transition: tn, Layer: layerName));
                }
                else if (anySolo && !Bool(tr, "Solo"))
                {
                    diagnostics.Add(new AnimatorDiagnostic(
                        "solo-shadowed-transition", "warning",
                        $"Transition '{tn}' on '{loc}' is shadowed by a Solo sibling.",
                        State: loc, Transition: tn, Layer: layerName));
                }
            }

            if (!incoming.Contains(stateName))
            {
                diagnostics.Add(new AnimatorDiagnostic(
                    "unreachable-state", "warning",
                    $"State '{loc}' is not the default and has no incoming transition.",
                    State: loc, Layer: layerName));
            }
        }

        foreach (EchoObject child in Items(machine, "StateMachines"))
        {
            string childName = Str(child, "Name");
            string childPath = string.IsNullOrEmpty(path) ? childName : path + "/" + childName;
            ValidateMachine(child, layerName, childPath, paramNames, diagnostics);
        }
    }

    static void ValidateTransitionList(
        IReadOnlyList<EchoObject> list,
        string layerName,
        string path,
        HashSet<string> paramNames,
        HashSet<string> stateNames,
        HashSet<string> incoming,
        List<AnimatorDiagnostic> diagnostics,
        bool anyState)
    {
        for (int i = 0; i < list.Count; i++)
        {
            EchoObject t = list[i];
            string tn = Str(t, "Name");
            string dest = Str(t, "DestinationStateName");
            if (!string.IsNullOrEmpty(dest) && dest != "Exit" && stateNames.Contains(dest))
                incoming.Add(dest);

            bool hasExit = Bool(t, "HasExitTime");
            int condCount = Items(t, "Conditions").Count;
            if (!hasExit && condCount == 0 && !anyState)
            {
                diagnostics.Add(new AnimatorDiagnostic(
                    "dead-transition", "error",
                    $"Transition '{tn}' has neither ExitTime nor conditions.",
                    Transition: tn, Layer: layerName));
            }

            foreach (EchoObject c in Items(t, "Conditions"))
            {
                string pn = Str(c, "ParamName");
                if (string.IsNullOrEmpty(pn) || paramNames.Contains(pn))
                    continue;
                diagnostics.Add(new AnimatorDiagnostic(
                    "missing-parameter", "error",
                    $"Transition '{tn}' references missing parameter '{pn}'.",
                    Transition: tn, Parameter: pn, Layer: layerName));
            }
        }
    }

    static void CheckParam(
        EchoObject o, string field, string state, string layer,
        HashSet<string> paramNames, List<AnimatorDiagnostic> diagnostics)
    {
        string pn = Str(o, field);
        if (string.IsNullOrEmpty(pn) || paramNames.Contains(pn))
            return;
        diagnostics.Add(new AnimatorDiagnostic(
            "missing-parameter", "error",
            $"State '{state}' field {field} references missing parameter '{pn}'.",
            State: state, Parameter: pn, Layer: layer));
    }

    static bool HasMotion(EchoObject s)
    {
        if (!s.TryGet("Motion", out EchoObject? m) || m is null)
            return false;
        string? id = GetAssetId(m);
        return !string.IsNullOrEmpty(id) && id != Guid.Empty.ToString();
    }

    static string Qualify(string layer, string path, string state)
    {
        if (string.IsNullOrEmpty(path))
            return state;
        return path + "/" + state;
    }
}
