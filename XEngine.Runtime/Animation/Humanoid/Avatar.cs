// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.Text;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation.Humanoid;

/// <summary>
/// Rig description retaining the original Humanoid asset/API identity. Generic rigs use
/// <see cref="GenericSkeleton"/>; Humanoid rigs describe which skeleton bone plays
/// each <see cref="HumanoidBone"/> role, plus the T-pose snapshot (bind local TRS per mapped bone)
/// the muscle space (M54) derives its alignment and limits from. Built hierarchy-aware from a
/// skeleton root via <see cref="BuildAuto"/>; editable through the Avatar window (M59).
/// </summary>
[CreateAssetMenu("Animation/Humanoid Avatar", Extension = ".avatar", Order = 93)]
public partial class Avatar : EngineObject
{
    /// <summary>One humanoid role bound to a skeleton bone path, with the T-pose local TRS snapshot.</summary>
    public class BoneMapping
    {
        public HumanoidBone Bone;
        public string Path = "";

        // T-pose snapshot (bind local TRS) captured at build time.
        public Float3 LocalPosition;
        public Quaternion LocalRotation = Quaternion.Identity;
        public Float3 LocalScale = new(1f, 1f, 1f);
    }

    /// <summary>Role → bone path + T-pose. Bones absent from the rig are simply unmapped.</summary>
    public List<BoneMapping> Mappings = new();

    /// <summary>Optional explicit root-motion bone path (empty = Hips).</summary>
    public string RootMotionBonePath = "";

    /// <summary>Hips-to-feet distance in the T-pose, for cross-rig root-trajectory rescale (M55).</summary>
    public float LegLength;

    /// <summary>Shoulder-to-wrist distance in the T-pose.</summary>
    public float ArmLength;

    /// <summary>Linear scan for the mapped path of <paramref name="bone"/> (empty when unmapped).</summary>
    public bool TryGetPath(HumanoidBone bone, out string path)
    {
        if (!IsHumanoid) { path = ""; return false; }
        var mappings = Mappings;
        for (int i = 0; i < mappings.Count; i++)
        {
            if (mappings[i].Bone == bone)
            {
                path = mappings[i].Path;
                return true;
            }
        }
        path = "";
        return false;
    }

    /// <summary>Index of the mapping for <paramref name="bone"/>, or -1.</summary>
    public int IndexOf(HumanoidBone bone)
    {
        if (!IsHumanoid) return -1;
        var mappings = Mappings;
        for (int i = 0; i < mappings.Count; i++)
            if (mappings[i].Bone == bone)
                return i;
        return -1;
    }

    /// <summary>
    /// Validation report: null when the avatar can drive a humanoid clip, otherwise a newline list of
    /// problems (missing required bones — Hips plus at least one full limb chain — and mapping
    /// ambiguity notes recorded during auto-mapping).
    /// </summary>
    public string? Validate()
    {
        if (RigKind == AvatarRigKind.Generic) return ValidateGeneric();
        if (!IsHumanoid) return "unknown Avatar rig kind";
        var report = new StringBuilder();
        bool hasHips = IndexOf(HumanoidBone.Hips) >= 0;
        if (!hasHips)
            report.AppendLine("missing Hips (required)");

        bool anyLeg = IndexOf(HumanoidBone.LeftUpperLeg) >= 0 || IndexOf(HumanoidBone.RightUpperLeg) >= 0;
        bool anyArm = IndexOf(HumanoidBone.LeftUpperArm) >= 0 || IndexOf(HumanoidBone.RightUpperArm) >= 0;
        if (!anyLeg && !anyArm)
            report.AppendLine("no limb chains mapped (need at least one leg or arm chain)");

        return report.Length == 0 ? null : report.ToString();
    }

    /// <summary>
    /// Hierarchy-aware auto-mapping over <paramref name="skeletonRoot"/>'s subtree: name aliases from
    /// <see cref="HumanoidBoneMap"/> seeded by chain constraints (Hips = the common ancestor of the
    /// leg chains; limb segments verified as parent chains; spine links ordered by depth). The
    /// mapped bones' current local TRS is captured as the T-pose snapshot — call with the rig posed
    /// in its authored reference stance.
    /// </summary>
    public static Avatar BuildAuto(XEngine.Vector.Transform skeletonRoot, string name = "Avatar")
    {
        var avatar = new Avatar { Name = name };
        var nodes = new List<(string path, XEngine.Vector.Transform transform)>();
        Collect(skeletonRoot, "", nodes);

        var byRole = MapByHierarchy(nodes);

        foreach (KeyValuePair<HumanoidBone, XEngine.Vector.Transform> kv in byRole)
        {
            string path = PathOf(nodes, kv.Value);
            XEngine.Vector.Transform t = kv.Value;
            avatar.Mappings.Add(new BoneMapping
            {
                Bone = kv.Key,
                Path = path,
                LocalPosition = t.LocalPosition,
                LocalRotation = t.LocalRotation,
                LocalScale = t.LocalScale,
            });
        }

        avatar.MeasureLimbs();
        if (avatar.TryGetPath(HumanoidBone.Hips, out string hips))
            avatar.RootMotionBonePath = hips;
        return avatar;
    }

    /// <summary>Linear scan for the mapping of <paramref name="bone"/>.</summary>
    public bool TryGetMapping(HumanoidBone bone, out BoneMapping? mapping)
    {
        if (!IsHumanoid) { mapping = null; return false; }
        var mappings = Mappings;
        for (int i = 0; i < mappings.Count; i++)
        {
            if (mappings[i].Bone == bone)
            {
                mapping = mappings[i];
                return true;
            }
        }
        mapping = null;
        return false;
    }

    /// <summary>Recomputes <see cref="LegLength"/>/<see cref="ArmLength"/> from the T-pose snapshot.</summary>
    public void MeasureLimbs()
    {
        LegLength = 0f;
        ArmLength = 0f;
        if (TryGetMapping(HumanoidBone.Hips, out BoneMapping? hips))
        {
            float left = ChainLength(hips, HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot);
            float right = ChainLength(hips, HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot);
            LegLength = System.MathF.Max(left, right);
        }
        if (TryGetMapping(HumanoidBone.LeftUpperArm, out BoneMapping? luArm)
            && TryGetMapping(HumanoidBone.LeftHand, out BoneMapping? lHand))
            ArmLength = System.MathF.Max(ArmLength, Distance(luArm, lHand));
        if (TryGetMapping(HumanoidBone.RightUpperArm, out BoneMapping? ruArm)
            && TryGetMapping(HumanoidBone.RightHand, out BoneMapping? rHand))
            ArmLength = System.MathF.Max(ArmLength, Distance(ruArm, rHand));
    }

    private float ChainLength(BoneMapping from, params HumanoidBone[] chain)
    {
        float total = 0f;
        BoneMapping current = from;
        for (int i = 0; i < chain.Length; i++)
        {
            if (!TryGetMapping(chain[i], out BoneMapping? next))
                break;
            total += Distance(current, next!);
            current = next!;
        }
        return total;
    }

    private static float Distance(BoneMapping a, BoneMapping b)
        => Float3.Length(b.LocalPosition - a.LocalPosition);

    /// <summary>Depth-first path collection relative to <paramref name="root"/> (root itself excluded).</summary>
    private static void Collect(
        XEngine.Vector.Transform node,
        string prefix,
        List<(string path, XEngine.Vector.Transform transform)> nodes)
    {
        int count = node.ChildCount;
        for (int i = 0; i < count; i++)
        {
            XEngine.Vector.Transform child = node.GetChild(i);
            string childName = child.GameObject.Name;
            string path = prefix.Length == 0 ? childName : prefix + "/" + childName;
            nodes.Add((path, child));
            Collect(child, path, nodes);
        }
    }

    private static string PathOf(List<(string path, XEngine.Vector.Transform transform)> nodes, XEngine.Vector.Transform t)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (ReferenceEquals(nodes[i].transform, t))
                return nodes[i].path;
        return "";
    }

    // ---- hierarchy-aware role resolution ----

    private static Dictionary<HumanoidBone, XEngine.Vector.Transform> MapByHierarchy(
        List<(string path, XEngine.Vector.Transform transform)> nodes)
    {
        var result = new Dictionary<HumanoidBone, XEngine.Vector.Transform>();

        // 1. Name-alias candidates per role (leaf name only).
        var candidates = new Dictionary<HumanoidBone, List<XEngine.Vector.Transform>>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (HumanoidBoneMap.TryResolve(nodes[i].transform.GameObject.Name, out HumanoidBone role))
            {
                if (!candidates.TryGetValue(role, out List<XEngine.Vector.Transform>? list))
                {
                    list = new List<XEngine.Vector.Transform>(2);
                    candidates[role] = list;
                }
                list.Add(nodes[i].transform);
            }
        }

        // 2. Limb chains, hand-first: verify lower/upper segments by walking parents. The "root"
        // alias never qualifies as a hand/arm/leg segment unless the chain check passes.
        ResolveLimb(result, candidates, HumanoidBone.LeftHand, HumanoidBone.LeftLowerArm, HumanoidBone.LeftUpperArm, HumanoidBone.LeftShoulder);
        ResolveLimb(result, candidates, HumanoidBone.RightHand, HumanoidBone.RightLowerArm, HumanoidBone.RightUpperArm, HumanoidBone.RightShoulder);
        ResolveLimb(result, candidates, HumanoidBone.LeftFoot, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftUpperLeg);
        ResolveLimb(result, candidates, HumanoidBone.RightFoot, HumanoidBone.RightLowerLeg, HumanoidBone.RightUpperLeg);
        ResolveToes(result, candidates, HumanoidBone.LeftToes, HumanoidBone.LeftFoot);
        ResolveToes(result, candidates, HumanoidBone.RightToes, HumanoidBone.RightFoot);

        // 3. Hips: deepest candidate that is an ancestor of at least one mapped upper leg; the bare
        // "root" alias only qualifies with a spine/hips child so top-level scene roots never win.
        XEngine.Vector.Transform? hips = null;
        int hipsDepth = -1;
        if (candidates.TryGetValue(HumanoidBone.Hips, out List<XEngine.Vector.Transform>? hipsCandidates))
        {
            foreach (XEngine.Vector.Transform c in hipsCandidates)
            {
                bool ancestorOfLeg = IsAncestorOrSelf(c, result, HumanoidBone.LeftUpperLeg)
                    || IsAncestorOrSelf(c, result, HumanoidBone.RightUpperLeg);
                if (!ancestorOfLeg)
                    continue;
                if (HumanoidBoneMap.Normalize(c.GameObject.Name) == "root"
                    && !HasChildRole(c, HumanoidBone.Spine, HumanoidBone.Hips))
                    continue; // scene root trap: keep looking.
                int depth = Depth(c);
                if (depth > hipsDepth)
                {
                    hips = c;
                    hipsDepth = depth;
                }
            }
        }
        if (hips != null)
            result[HumanoidBone.Hips] = hips;

        // 4. Spine links ordered by depth between Hips and Neck/Head.
        ResolveSpineChain(result, candidates);

        // 5. Neck / Head: head is the head-alias not under a hand; neck is its parent when aliased.
        if (candidates.TryGetValue(HumanoidBone.Head, out List<XEngine.Vector.Transform>? heads))
        {
            foreach (XEngine.Vector.Transform h in heads)
            {
                if (IsUnder(h, result, HumanoidBone.LeftHand) || IsUnder(h, result, HumanoidBone.RightHand))
                    continue; // finger "head" bones etc.
                result[HumanoidBone.Head] = h;
                if (h.Parent != null && HumanoidBoneMap.TryResolve(h.Parent.GameObject.Name, out HumanoidBone pr)
                    && pr == HumanoidBone.Neck)
                    result[HumanoidBone.Neck] = h.Parent;
                break;
            }
        }

        return result;
    }

    private static void ResolveLimb(
        Dictionary<HumanoidBone, XEngine.Vector.Transform> result,
        Dictionary<HumanoidBone, List<XEngine.Vector.Transform>> candidates,
        HumanoidBone end,
        HumanoidBone middle,
        HumanoidBone upper,
        HumanoidBone? shoulder = null)
    {
        if (!candidates.TryGetValue(end, out List<XEngine.Vector.Transform>? ends) || ends == null)
            return;

        foreach (XEngine.Vector.Transform e in ends)
        {
            XEngine.Vector.Transform? mid = FindAliasedAncestor(e.Parent, middle, maxHops: 2);
            XEngine.Vector.Transform? up = mid != null ? FindAliasedAncestor(mid.Parent, upper, maxHops: 2) : null;
            if (mid == null || up == null)
                continue;

            result[end] = e;
            result[middle] = mid;
            result[upper] = up;
            if (shoulder.HasValue && up.Parent != null
                && HumanoidBoneMap.TryResolve(up.Parent.GameObject.Name, out HumanoidBone pr) && pr == shoulder.Value)
                result[shoulder.Value] = up.Parent;
            return; // first complete chain wins.
        }
    }

    private static void ResolveToes(
        Dictionary<HumanoidBone, XEngine.Vector.Transform> result,
        Dictionary<HumanoidBone, List<XEngine.Vector.Transform>> candidates,
        HumanoidBone toes,
        HumanoidBone foot)
    {
        if (!candidates.TryGetValue(toes, out List<XEngine.Vector.Transform>? list) || list == null)
            return;
        foreach (XEngine.Vector.Transform t in list)
        {
            if (result.TryGetValue(foot, out XEngine.Vector.Transform? f) && IsAncestorOf(f, t))
            {
                result[toes] = t;
                return;
            }
        }
    }

    private static void ResolveSpineChain(
        Dictionary<HumanoidBone, XEngine.Vector.Transform> result,
        Dictionary<HumanoidBone, List<XEngine.Vector.Transform>> candidates)
    {
        if (!result.TryGetValue(HumanoidBone.Hips, out XEngine.Vector.Transform? hips))
            return;

        // Collect all spine-alias nodes strictly above Hips (depth order).
        var links = new List<(HumanoidBone role, XEngine.Vector.Transform t, int depth)>();
        TryAddSpine(links, candidates, HumanoidBone.Spine, hips);
        TryAddSpine(links, candidates, HumanoidBone.Chest, hips);
        TryAddSpine(links, candidates, HumanoidBone.UpperChest, hips);
        links.Sort((a, b) => a.depth.CompareTo(b.depth));

        // Spine must be the shallowest (directly above hips); Chest and UpperChest follow by depth.
        for (int i = 0; i < links.Count; i++)
            if (!result.ContainsKey(links[i].role))
                result[links[i].role] = links[i].t;
    }

    private static void TryAddSpine(
        List<(HumanoidBone role, XEngine.Vector.Transform t, int depth)> links,
        Dictionary<HumanoidBone, List<XEngine.Vector.Transform>> candidates,
        HumanoidBone role,
        XEngine.Vector.Transform hips)
    {
        if (!candidates.TryGetValue(role, out List<XEngine.Vector.Transform>? list) || list == null)
            return;
        foreach (XEngine.Vector.Transform t in list)
        {
            if (!IsAncestorOf(hips, t))
                continue; // not on the spine path above Hips.
            links.Add((role, t, Depth(t)));
        }
    }

    private static XEngine.Vector.Transform? FindAliasedAncestor(XEngine.Vector.Transform? start, HumanoidBone role, int maxHops)
    {
        for (int hop = 0; hop < maxHops && start != null; hop++)
        {
            if (HumanoidBoneMap.TryResolve(start.GameObject.Name, out HumanoidBone r) && r == role)
                return start;
            start = start.Parent;
        }
        return null;
    }

    private static bool IsAncestorOf(XEngine.Vector.Transform ancestor, XEngine.Vector.Transform node)
    {
        XEngine.Vector.Transform? p = node.Parent;
        while (p != null)
        {
            if (ReferenceEquals(p, ancestor))
                return true;
            p = p.Parent;
        }
        return false;
    }

    private static bool IsUnder(XEngine.Vector.Transform node, Dictionary<HumanoidBone, XEngine.Vector.Transform> map, HumanoidBone role)
        => map.TryGetValue(role, out XEngine.Vector.Transform? t) && t != null && IsAncestorOf(t, node);

    private static bool IsAncestorOrSelf(
        XEngine.Vector.Transform candidate,
        Dictionary<HumanoidBone, XEngine.Vector.Transform> map,
        HumanoidBone role)
    {
        if (!map.TryGetValue(role, out XEngine.Vector.Transform? t) || t == null)
            return false;
        return ReferenceEquals(candidate, t) || IsAncestorOf(candidate, t);
    }

    private static bool HasChildRole(XEngine.Vector.Transform node, params HumanoidBone[] roles)
    {
        int count = node.ChildCount;
        for (int i = 0; i < count; i++)
        {
            if (HumanoidBoneMap.TryResolve(node.GetChild(i).GameObject.Name, out HumanoidBone r))
            {
                for (int j = 0; j < roles.Length; j++)
                    if (r == roles[j])
                        return true;
            }
        }
        return false;
    }

    private static int Depth(XEngine.Vector.Transform t)
    {
        int depth = 0;
        XEngine.Vector.Transform? p = t.Parent;
        while (p != null)
        {
            depth++;
            p = p.Parent;
        }
        return depth;
    }
}
