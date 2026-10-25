// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Animation;

namespace XEngine.Editor.AnimationEditor;

internal static partial class AnimatorControllerEditGraph
{
    /// <summary>Walks nested machines on <paramref name="layer"/> until <paramref name="stateName"/> is found.</summary>
    public static bool TryFindStatePath(
        AnimatorController controller, int layer, string stateName, List<string> pathOut)
    {
        pathOut.Clear();
        return TryFindStatePath(EffectiveRoot(controller, layer), stateName, pathOut);
    }

    /// <summary>Walks nested machines until a state whose name hashes to <paramref name="nameHash"/> is found.</summary>
    public static bool TryFindStatePathByHash(
        AnimatorController controller, int layer, int nameHash, List<string> pathOut, out string stateName)
    {
        pathOut.Clear();
        return TryFindStatePathByHash(EffectiveRoot(controller, layer), nameHash, pathOut, out stateName);
    }

    private static bool TryFindStatePath(AnimatorController.StateMachine sm, string stateName, List<string> pathOut)
    {
        if (FindStateIn(sm.States, stateName) != null)
            return true;
        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            pathOut.Add(children[i].Name);
            if (TryFindStatePath(children[i], stateName, pathOut))
                return true;
            pathOut.RemoveAt(pathOut.Count - 1);
        }
        return false;
    }

    private static bool TryFindStatePathByHash(
        AnimatorController.StateMachine sm, int nameHash, List<string> pathOut, out string stateName)
    {
        var states = sm.States;
        for (int i = 0; i < states.Count; i++)
        {
            if (AnimationNameHash.Hash(states[i].Name) == nameHash)
            {
                stateName = states[i].Name;
                return true;
            }
        }
        var children = sm.StateMachines;
        for (int i = 0; i < children.Count; i++)
        {
            pathOut.Add(children[i].Name);
            if (TryFindStatePathByHash(children[i], nameHash, pathOut, out stateName))
                return true;
            pathOut.RemoveAt(pathOut.Count - 1);
        }
        stateName = string.Empty;
        return false;
    }
}
