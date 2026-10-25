// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// M27: Echo-text clipboard for Animator states (Unity's CopyStateMachineToPasteboard analogue),
/// plus node-title formatting and parallel-edge fan-out used by the Animator window.
/// </summary>
internal static class AnimatorGraphClipboard
{
    public const string Header = "XEngineAnimatorStates:";
    public static readonly Float2 PasteOffset = new(40f, 40f);

    /// <summary>Last Echo payload written by <see cref="Copy"/>. Tests use this because
    /// <see cref="Input.Clipboard"/> is a no-op under <c>NullInputHandler</c>.</summary>
    internal static string LastText = string.Empty;

    public static string FormatStateTitle(AnimatorController.State state)
    {
        string title = state.Name ?? string.Empty;
        if (state.IsLooping)
            title += "  \u21bb";
        if (Math.Abs(state.Speed - 1f) > 0.001f)
            title += $"  \u00d7{state.Speed:0.##}";

        int childCount = BlendTreeEditorModel.ChildCount(state);
        if (childCount > 0)
            title += $"  ({childCount})";
        else if (BlendTreeEditorModel.HasTree(state))
            title += "  (BT)";

        string? motion = MotionLabel(state);
        if (!string.IsNullOrEmpty(motion))
            title += $"  \u00b7 {motion}";
        return title;
    }

    public static float StateNodeWidth(AnimatorController.State state)
    {
        string title = FormatStateTitle(state);
        float w = 130f + Math.Max(0, title.Length - 12) * 6.5f;
        if (w > 280f) w = 280f;
        if (w < 130f) w = 130f;
        return w;
    }

    /// <summary>
    /// Offset a parallel wire so same-(src,dst) edges fan instead of stacking.
    /// <paramref name="index"/> is 0-based among the group of <paramref name="count"/>.
    /// Returns false when no control point is needed (a single edge).
    /// </summary>
    public static bool TryFanControlPoint(Float2 from, Float2 to, int index, int count, out Float2 point)
    {
        point = default;
        if (count <= 1) return false;
        float mx = (from.X + to.X) * 0.5f;
        float my = (from.Y + to.Y) * 0.5f;
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float nx = -dy;
        float ny = dx;
        float len = MathF.Sqrt(nx * nx + ny * ny);
        if (len < 0.001f) { nx = 0f; ny = 1f; }
        else { nx /= len; ny /= len; }
        float t = index - (count - 1) * 0.5f;
        const float spread = 28f;
        point = new Float2(mx + nx * (t * spread), my + ny * (t * spread));
        return true;
    }

    public static string CopyToText(AnimatorController controller, int layer, IReadOnlyList<string> stateNames,
        IReadOnlyList<string>? path = null)
    {
        var payload = new Payload();
        var selected = new HashSet<string>();
        for (int i = 0; i < stateNames.Count; i++)
        {
            string name = stateNames[i];
            if (string.IsNullOrEmpty(name) || name.StartsWith("__", StringComparison.Ordinal))
                continue;
            selected.Add(name);
        }

        var states = AnimatorControllerEditGraph.States(controller, layer, path);
        var transitions = AnimatorControllerEditGraph.Transitions(controller, layer, path);
        for (int i = 0; i < states.Count; i++)
        {
            if (!selected.Contains(states[i].Name)) continue;
            payload.States.Add(CloneState(states[i]));
            var names = states[i].TransitionNames;
            for (int n = 0; n < names.Count; n++)
            {
                var t = FindTransition(transitions, names[n]);
                if (t != null)
                    payload.Transitions.Add(CloneTransition(t));
            }
        }

        for (int i = 0; i < controller.Parameters.Count; i++)
            payload.ParameterNames.Add(controller.Parameters[i].Name);

        EchoObject echo = Serializer.Serialize(payload, TypeMode.Aggressive);
        return Header + echo.WriteToString();
    }

    public static void Copy(AnimatorController controller, int layer, IReadOnlyList<string> stateNames,
        IReadOnlyList<string>? path = null)
    {
        LastText = CopyToText(controller, layer, stateNames, path);
        Input.Clipboard = LastText;
    }

    public static bool TryReadClipboard(out string text)
    {
        text = Input.Clipboard;
        if (!string.IsNullOrEmpty(text) && text.StartsWith(Header, StringComparison.Ordinal))
            return true;
        text = LastText;
        return !string.IsNullOrEmpty(text) && text.StartsWith(Header, StringComparison.Ordinal);
    }

    /// <summary>Paste clipboard states into <paramref name="controller"/>. Returns the new state names.</summary>
    public static List<string> Paste(AnimatorController controller, int layer, string? text = null, Float2? offset = null,
        IReadOnlyList<string>? path = null)
    {
        var created = new List<string>();
        if (text == null && !TryReadClipboard(out text))
            return created;
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Header, StringComparison.Ordinal))
            return created;

        Payload? payload;
        try
        {
            EchoObject echo = EchoObject.ReadFromString(text.Substring(Header.Length));
            payload = Serializer.Deserialize<Payload>(echo);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Animator] Paste failed: {ex.Message}");
            return created;
        }
        if (payload == null || payload.States.Count == 0)
            return created;

        Float2 d = offset ?? PasteOffset;
        var nameMap = new Dictionary<string, string>();
        var outgoing = new Dictionary<string, List<string>>();
        var destParams = new HashSet<string>();
        for (int i = 0; i < controller.Parameters.Count; i++)
            destParams.Add(controller.Parameters[i].Name);

        var states = AnimatorControllerEditGraph.States(controller, layer, path);
        var transitions = AnimatorControllerEditGraph.Transitions(controller, layer, path);

        for (int i = 0; i < payload.States.Count; i++)
        {
            var src = payload.States[i];
            string oldName = src.Name;
            outgoing[oldName] = new List<string>(src.TransitionNames);
            src.Name = AnimatorControllerEditGraph.UniqueStateName(controller, layer, oldName, path);
            src.PosX += d.X;
            src.PosY += d.Y;
            RemapBlendTreeParameter(src, destParams);
            src.TransitionNames.Clear();
            states.Add(src);
            nameMap[oldName] = src.Name;
            created.Add(src.Name);
        }

        for (int i = 0; i < payload.Transitions.Count; i++)
        {
            var t = payload.Transitions[i];
            if (nameMap.TryGetValue(t.DestinationStateName, out string? mappedDest))
                t.DestinationStateName = mappedDest;
            RemapConditions(t, destParams);
            string? fromOld = FindSourceOldName(outgoing, t);
            if (fromOld == null || !nameMap.TryGetValue(fromOld, out string? fromNew))
                continue;
            var source = AnimatorControllerEditGraph.FindState(controller, layer, fromNew, path);
            if (source == null) continue;
            t.Name = AnimatorControllerEditGraph.UniqueTransitionName(controller, layer, fromNew, t.DestinationStateName, path);
            transitions.Add(t);
            source.TransitionNames.Add(t.Name);
        }

        return created;
    }

    public static List<string> Duplicate(AnimatorController controller, int layer, IReadOnlyList<string> stateNames,
        IReadOnlyList<string>? path = null)
    {
        string text = CopyToText(controller, layer, stateNames, path);
        return Paste(controller, layer, text, PasteOffset, path);
    }

    private static string? MotionLabel(AnimatorController.State state)
    {
        BlendTree? tree = BlendTreeEditorModel.GetTree(state);
        if (tree != null)
            return string.IsNullOrEmpty(tree.Name) ? "Blend Tree" : tree.Name;
        AnimationClip? clip = state.Motion.Res;
        if (clip != null && !string.IsNullOrEmpty(clip.Name))
            return clip.Name;
        return null;
    }

    private static AnimatorController.State CloneState(AnimatorController.State src)
    {
        EchoObject echo = Serializer.Serialize(src, TypeMode.Aggressive);
        return Serializer.Deserialize<AnimatorController.State>(echo) ?? new AnimatorController.State { Name = src.Name };
    }

    private static AnimatorController.Transition CloneTransition(AnimatorController.Transition src)
    {
        EchoObject echo = Serializer.Serialize(src, TypeMode.Aggressive);
        return Serializer.Deserialize<AnimatorController.Transition>(echo)
            ?? new AnimatorController.Transition { Name = src.Name, DestinationStateName = src.DestinationStateName };
    }

    private static AnimatorController.Transition? FindTransition(List<AnimatorController.Transition> list, string name)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Name == name) return list[i];
        }
        return null;
    }

    private static string? FindSourceOldName(Dictionary<string, List<string>> outgoing, AnimatorController.Transition t)
    {
        foreach (var kv in outgoing)
        {
            var names = kv.Value;
            for (int n = 0; n < names.Count; n++)
            {
                if (names[n] == t.Name) return kv.Key;
            }
        }
        return null;
    }

    private static void RemapConditions(AnimatorController.Transition t, HashSet<string> destParams)
    {
        var conditions = t.Conditions;
        for (int i = conditions.Count - 1; i >= 0; i--)
        {
            string name = conditions[i].ParamName;
            if (string.IsNullOrEmpty(name) || destParams.Contains(name))
                continue;
            conditions.RemoveAt(i);
        }
    }

    private static void RemapBlendTreeParameter(AnimatorController.State state, HashSet<string> destParams)
    {
        BlendTree? tree = BlendTreeEditorModel.GetTree(state);
        if (tree == null) return;
        if (!string.IsNullOrEmpty(tree.ParameterName) && !destParams.Contains(tree.ParameterName))
            tree.ParameterName = string.Empty;
    }

    /// <summary>Echo payload written to the clipboard. Public fields so Echo can round-trip it.</summary>
    public sealed class Payload
    {
        public List<AnimatorController.State> States = new();
        public List<AnimatorController.Transition> Transitions = new();
        public List<string> ParameterNames = new();
    }
}
