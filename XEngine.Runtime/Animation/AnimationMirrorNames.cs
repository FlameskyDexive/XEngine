// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Animation;

/// <summary>M35: Unity-style Left/Right bone-name mirroring for Animator state Mirror.</summary>
public static class AnimationMirrorNames
{
    public static string Mirror(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        string swapped = ReplaceToken(name, "Left", "Right");
        if (swapped != name) return swapped;
        swapped = ReplaceToken(name, "Right", "Left");
        if (swapped != name) return swapped;
        swapped = ReplaceToken(name, "left", "right");
        if (swapped != name) return swapped;
        swapped = ReplaceToken(name, "right", "left");
        if (swapped != name) return swapped;
        if (name.EndsWith("_L", System.StringComparison.Ordinal))
            return name.Substring(0, name.Length - 2) + "_R";
        if (name.EndsWith("_R", System.StringComparison.Ordinal))
            return name.Substring(0, name.Length - 2) + "_L";
        if (name.EndsWith(".L", System.StringComparison.Ordinal))
            return name.Substring(0, name.Length - 2) + ".R";
        if (name.EndsWith(".R", System.StringComparison.Ordinal))
            return name.Substring(0, name.Length - 2) + ".L";
        return name;
    }

    private static string ReplaceToken(string name, string from, string to)
    {
        int i = name.IndexOf(from, System.StringComparison.Ordinal);
        if (i < 0) return name;
        return string.Concat(name.AsSpan(0, i), to, name.AsSpan(i + from.Length));
    }
}
