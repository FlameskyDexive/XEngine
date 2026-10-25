// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;

namespace XEngine.Editor.AnimationEditor;

/// <summary>★ M44 — Bone-path tree rows for the Avatar Mask inspector.</summary>
public static class AvatarMaskEditorModel
{
    public static readonly string[] HumanoidPartNames =
    {
        "Root", "Body", "Head", "Left Arm", "Right Arm", "Left Leg", "Right Leg",
        "Left Fingers", "Right Fingers",
    };

    public readonly struct BoneRow
    {
        public readonly int Depth;
        public readonly int EntryIndex;
        public readonly string Label;

        public BoneRow(int depth, int entryIndex, string label)
        {
            Depth = depth;
            EntryIndex = entryIndex;
            Label = label;
        }
    }

    public static List<BoneRow> BuildBoneTree(IReadOnlyList<AvatarMask.Entry> entries)
    {
        int n = entries.Count;
        var order = new int[n];
        for (int i = 0; i < n; i++)
            order[i] = i;
        Array.Sort(order, (a, b) => string.CompareOrdinal(entries[a].Path, entries[b].Path));

        var rows = new List<BoneRow>(n);
        for (int i = 0; i < n; i++)
        {
            int idx = order[i];
            string path = entries[idx].Path ?? "";
            int depth = 0;
            for (int c = 0; c < path.Length; c++)
                if (path[c] == '/') depth++;
            string label = path;
            int slash = path.LastIndexOf('/');
            if (slash >= 0 && slash < path.Length - 1)
                label = path.Substring(slash + 1);
            if (string.IsNullOrEmpty(label))
                label = "(root)";
            rows.Add(new BoneRow(depth, idx, label));
        }
        return rows;
    }
}
