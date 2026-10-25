// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>★ M45 — Live Animator snapshots for in-process MCP tools (no Host dependency).</summary>
public static class RuntimeAnimatorInspect
{
    public static string? ResolveStateName(AnimatorController? controller, int nameHash)
    {
        if (controller == null || nameHash == 0) return null;
        string? hit = FindName(controller.States, nameHash);
        if (hit != null) return hit;
        var layers = controller.Layers;
        for (int i = 0; i < layers.Count; i++)
        {
            hit = FindName(layers[i].States, nameHash);
            if (hit != null) return hit;
            if (layers[i].Root != null)
            {
                hit = FindNameInMachine(layers[i].Root!, nameHash);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    static string? FindName(List<AnimatorController.State> states, int nameHash)
    {
        for (int i = 0; i < states.Count; i++)
            if (AnimationNameHash.Hash(states[i].Name) == nameHash)
                return states[i].Name;
        return null;
    }

    static string? FindNameInMachine(AnimatorController.StateMachine sm, int nameHash)
    {
        string? hit = FindName(sm.States, nameHash);
        if (hit != null) return hit;
        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            hit = FindNameInMachine(children[i], nameHash);
            if (hit != null) return hit;
        }
        return null;
    }

    public static List<AnimatorLiveParameter> ReadParameters(Animator animator)
    {
        var list = new List<AnimatorLiveParameter>();
        AnimatorController? author = animator.AuthoringController;
        if (author == null) return list;
        var parameters = author.Parameters;
        for (int i = 0; i < parameters.Count; i++)
        {
            AnimatorController.Parameter p = parameters[i];
            string type = p.Type.ToString();
            float f = 0f;
            int n = 0;
            bool b = false;
            switch (p.Type)
            {
                case AnimatorParameterType.Int:
                    n = animator.GetInteger(p.Name);
                    f = n;
                    break;
                case AnimatorParameterType.Bool:
                case AnimatorParameterType.Trigger:
                    b = animator.GetBool(p.Name);
                    f = b ? 1f : 0f;
                    break;
                default:
                    f = animator.GetFloat(p.Name);
                    break;
            }
            list.Add(new AnimatorLiveParameter(p.Name, type, f, n, b));
        }
        return list;
    }

    public static bool SetParameter(Animator animator, string name, string type, float value)
    {
        string t = (type ?? "Float").Trim();
        if (t.Equals("Int", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Integer", StringComparison.OrdinalIgnoreCase))
        {
            animator.SetInteger(name, (int)value);
            return true;
        }
        if (t.Equals("Bool", StringComparison.OrdinalIgnoreCase))
        {
            animator.SetBool(name, value != 0f);
            return true;
        }
        if (t.Equals("Trigger", StringComparison.OrdinalIgnoreCase))
        {
            if (value == 0f)
                animator.ResetTrigger(name);
            else
                animator.SetTrigger(name);
            return true;
        }
        animator.SetFloat(name, value);
        return true;
    }
}

public readonly record struct AnimatorLiveParameter(
    string Name, string Type, float FloatValue, int IntValue, bool BoolValue);
