// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json.Serialization;

namespace XEngine.Ai.SpineGen;

public enum RigRole
{
    /// <summary>The largest part — the mass the whole body hangs from; bound to hips.</summary>
    Torso,
    Head,
    /// <summary>Chest/abdomen core pieces; bound to spine.</summary>
    Spine,
    /// <summary>Waist/hip-skirt pieces; bound to hips.</summary>
    Hips,
    ArmLeft,
    ArmRight,
    LegLeft,
    LegRight,
    Tail,
    Prop,
}

/// <summary>Classification + anchor decision for one sliced part.</summary>
public sealed class RigPartBinding
{
    [JsonPropertyName("part")] public required string Part { get; init; }
    [JsonPropertyName("role")] public required RigRole Role { get; init; }
    /// <summary>Bone this part hangs on (the joint it pivots around).</summary>
    [JsonPropertyName("bone")] public required string Bone { get; init; }

    /// <summary>Semantic joint used by the skinning weight field. The slot still hangs from its
    /// stable per-part bone so region fallbacks remain valid.</summary>
    [JsonPropertyName("deformBone")] public string DeformBone { get; init; } = "";

    /// <summary>Setup-pose relocation used for detached props (for example, moving a separately
    /// drawn sword from the side of the canvas into the inferred hand).</summary>
    [JsonPropertyName("offsetX")] public float OffsetX { get; init; }
    [JsonPropertyName("offsetY")] public float OffsetY { get; init; }
}

public sealed class RigBoneInfo
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("parent")] public string? Parent { get; init; }
    /// <summary>Anchor in IMAGE coordinates (y down) — converted to spine space at export.</summary>
    [JsonPropertyName("imageX")] public required float ImageX { get; init; }
    [JsonPropertyName("imageY")] public required float ImageY { get; init; }
    /// <summary>Visual length hint for editor readability only (setup rotation stays 0).</summary>
    [JsonPropertyName("length")] public float Length { get; init; }
}

/// <summary>Rig layout produced before export; serialized to rig_manifest.json for the wizard and
/// the animation stage (which needs stable bone names).</summary>
public sealed class RigManifest
{
    [JsonPropertyName("imageWidth")] public required int ImageWidth { get; init; }
    [JsonPropertyName("imageHeight")] public required int ImageHeight { get; init; }
    [JsonPropertyName("bones")] public List<RigBoneInfo> Bones { get; set; } = [];
    [JsonPropertyName("parts")] public List<RigPartBinding> Parts { get; set; } = [];
    [JsonPropertyName("bodyIslands")] public List<int> BodyIslands { get; set; } = [];

    public string? FindBoneOf(string partName) =>
        Parts.FirstOrDefault(p => p.Part == partName)?.Bone;
}

/// <summary>
/// Rule-based rig inference for stylized single characters (biped template):
///  1. largest part = torso mass — the root/hips/spine template chain hangs from it;
///  2. remaining parts are banded into head / arm_l / arm_r / spine core / hips / leg_l / leg_r
///     by image-proportional position (frontal standing biped assumption);
///  3. every part gets its OWN bone parented to its role bone, so role-level animation
///     (arm_r slash, leg_l step) swings the whole limb and per-part motion stays possible later.
///
/// Every anchor sits at the part's natural swing joint (top-center for limbs, neck for head),
/// so rotating a bone swings exactly that limb without teleporting. Reconstruction exactness is
/// proven against the vendored runtime in tests, not assumed.
/// </summary>
public static class Rigger
{
    public static (SpineSkeletonDoc Doc, RigManifest Manifest) Build(SliceManifest slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        if (slice.Recipe != null) return slice.Recipe.Build(slice);
        List<SlicePartEntry> orderedParts = [.. slice.Parts.OrderBy(p => p.Layer)];
        if (orderedParts.Count == 0)
            throw new InvalidOperationException("Slice manifest has no parts.");

        HashSet<int> bodyIslands = FindBodyIslands(orderedParts);
        var bodyParts = new List<SlicePartEntry>(orderedParts.Count);
        var detachedParts = new List<SlicePartEntry>();
        foreach (SlicePartEntry part in orderedParts)
        {
            if (bodyIslands.Contains(part.SourceIsland))
                bodyParts.Add(part);
            else
                detachedParts.Add(part);
        }
        Bounds body = Bounds.Of(bodyParts);
        float bodyCenterX = WeightedCenterX(bodyParts);
        float bodyHeight = Math.Max(1f, body.Height);
        SlicePartEntry torso = FindTorso(bodyParts, body);

        var orderedBodyIslands = new List<int>(bodyIslands);
        orderedBodyIslands.Sort();

        var manifest = new RigManifest
        {
            ImageWidth = slice.ImageWidth,
            ImageHeight = slice.ImageHeight,
            BodyIslands = orderedBodyIslands,
        };

        // The template is derived from the complete body silhouette, never from the largest
        // watershed shard (on U8 the largest shard is a thigh, which previously put the spine in
        // the leg). Anchors are image-space anatomical bands and remain stable with canvas props.
        manifest.Bones.Add(new RigBoneInfo
        {
            Name = "root", ImageX = bodyCenterX, ImageY = body.Bottom,
            Length = bodyHeight * 0.12f,
        });
        Ensure(manifest, "hips", "root", bodyCenterX, body.Top + bodyHeight * 0.57f,
            bodyHeight * 0.21f);
        Ensure(manifest, "spine", "hips", bodyCenterX, body.Top + bodyHeight * 0.36f,
            bodyHeight * 0.22f);

        var roles = new Dictionary<string, RigRole>(StringComparer.Ordinal);
        foreach (SlicePartEntry part in orderedParts)
        {
            roles[part.Name] = !bodyIslands.Contains(part.SourceIsland)
                ? RigRole.Prop
                : ReferenceEquals(part, torso)
                    ? RigRole.Torso
                    : ClassifyBodyPart(part, torso, body);
        }

        var deformBoneByPart = new Dictionary<string, string>(StringComparer.Ordinal);
        BuildHead(manifest, PartsOfRole(bodyParts, roles, RigRole.Head), body, deformBoneByPart);
        BuildArm(manifest, PartsOfRole(bodyParts, roles, RigRole.ArmLeft), left: true, body,
            deformBoneByPart);
        BuildArm(manifest, PartsOfRole(bodyParts, roles, RigRole.ArmRight), left: false, body,
            deformBoneByPart);
        BuildLeg(manifest, PartsOfRole(bodyParts, roles, RigRole.LegLeft), left: true, body,
            deformBoneByPart);
        BuildLeg(manifest, PartsOfRole(bodyParts, roles, RigRole.LegRight), left: false, body,
            deformBoneByPart);

        foreach (SlicePartEntry part in bodyParts)
        {
            RigRole role = roles[part.Name];
            string deformBone = role switch
            {
                RigRole.Torso or RigRole.Spine => "spine",
                RigRole.Hips => "hips",
                RigRole.Head => deformBoneByPart.GetValueOrDefault(part.Name, "head"),
                RigRole.ArmLeft => deformBoneByPart.GetValueOrDefault(part.Name, "arm_l"),
                RigRole.ArmRight => deformBoneByPart.GetValueOrDefault(part.Name, "arm_r"),
                RigRole.LegLeft => deformBoneByPart.GetValueOrDefault(part.Name, "leg_l"),
                RigRole.LegRight => deformBoneByPart.GetValueOrDefault(part.Name, "leg_r"),
                _ => "spine",
            };
            string parentBone = RoleBoneName(role);
            string partBone = $"b_{part.Name}";
            float centerX = part.X + part.Width / 2f;
            float centerY = part.Y + part.Height / 2f;
            Ensure(manifest, partBone, parentBone, centerX, centerY,
                MathF.Max(part.Width, part.Height));
            manifest.Parts.Add(new RigPartBinding
            {
                Part = part.Name,
                Role = role,
                Bone = partBone,
                DeformBone = deformBone,
            });
        }

        // Every alpha island outside the connected body cluster is a detached prop. Preserve all
        // of its watershed shards as one rigid assembly and snap its inferred grip into the
        // nearest hand; the original source pixels and atlas rects stay untouched.
        int propOrdinal = 0;
        foreach (List<SlicePartEntry> propParts in GroupDetachedProps(detachedParts, body))
        {
            Bounds prop = Bounds.Of(propParts);
            bool left = prop.CenterX < bodyCenterX;
            string hand = left ? "hand_l" : "hand_r";
            string arm = left ? "arm_l" : "arm_r";
            RigBoneInfo? target = FindBone(manifest.Bones, hand) ?? FindBone(manifest.Bones, arm) ??
                FindBone(manifest.Bones, "spine");
            if (target == null)
                throw new InvalidOperationException("Generated rig has no prop parent bone.");
            string parent = target.Name;

            bool tall = prop.Height >= prop.Width;
            float sourceGripX = tall
                ? prop.CenterX
                : left ? prop.Right - prop.Width * 0.18f : prop.Left + prop.Width * 0.18f;
            float sourceGripY = tall
                ? prop.Top + prop.Height * 0.18f
                : prop.CenterY;
            float offsetX = target.ImageX - sourceGripX;
            float offsetY = target.ImageY - sourceGripY;
            string propBone = $"prop_{++propOrdinal:00}";
            Ensure(manifest, propBone, parent, target.ImageX, target.ImageY,
                MathF.Max(prop.Width, prop.Height));

            foreach (SlicePartEntry part in propParts)
            {
                string partBone = $"b_{part.Name}";
                float centerX = part.X + part.Width / 2f + offsetX;
                float centerY = part.Y + part.Height / 2f + offsetY;
                Ensure(manifest, partBone, propBone, centerX, centerY,
                    MathF.Max(part.Width, part.Height));
                manifest.Parts.Add(new RigPartBinding
                {
                    Part = part.Name,
                    Role = RigRole.Prop,
                    Bone = partBone,
                    DeformBone = propBone,
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                });
            }
        }

        return (BuildDoc(manifest, slice), manifest);
    }

    private static List<SlicePartEntry> PartsOfRole(List<SlicePartEntry> parts,
        Dictionary<string, RigRole> roles, RigRole role)
    {
        var result = new List<SlicePartEntry>();
        foreach (SlicePartEntry part in parts)
            if (roles[part.Name] == role)
                result.Add(part);
        return result;
    }

    private static RigBoneInfo? FindBone(List<RigBoneInfo> bones, string name)
    {
        foreach (RigBoneInfo bone in bones)
            if (bone.Name == name)
                return bone;
        return null;
    }

    private static string RoleBoneName(RigRole role) => role switch
    {
        RigRole.Torso => "hips",
        RigRole.Head => "head",
        RigRole.Spine => "spine",
        RigRole.Hips => "hips",
        RigRole.ArmLeft => "arm_l",
        RigRole.ArmRight => "arm_r",
        RigRole.LegLeft => "leg_l",
        RigRole.LegRight => "leg_r",
        RigRole.Tail => "tail",
        RigRole.Prop => "spine",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static RigRole ClassifyBodyPart(SlicePartEntry part, SlicePartEntry torso, Bounds body)
    {
        float cx = part.X + part.Width / 2f;
        float cy = part.Y + part.Height / 2f;
        float nx = (cx - body.Left) / Math.Max(1f, body.Width);
        float ny = (cy - body.Top) / Math.Max(1f, body.Height);

        // A head must be both high and central. The previous top-band-only rule classified U8's
        // two shoulder stacks as head pieces merely because their pauldrons reached the crown.
        if (ny < 0.20f && MathF.Abs(nx - 0.5f) < 0.14f)
            return RigRole.Head;

        // Arms flank the core down through the wrist. Chibi arms can sit as low as their torso;
        // the torso-relative floor handles that case without swallowing humanoid hip plates.
        float armFloor = MathF.Min(body.Bottom,
            MathF.Max(body.Top + body.Height * 0.56f, torso.Y + torso.Height * 1.75f));
        if (cy <= armFloor)
        {
            // Near the shoulder an arm may touch the torso, but toward the hand it must stay
            // progressively farther outside the body core.  A fixed 43/57 split swallowed U8's
            // upper-thigh guards (ny≈.45, nx≈.35/.68) and made them fly away with attack swings.
            float outerBand = ny < 0.34f ? 0.43f : ny < 0.43f ? 0.34f : 0.24f;
            if (nx < outerBand)
                return RigRole.ArmLeft;
            if (nx > 1f - outerBand)
                return RigRole.ArmRight;
        }

        if (ny is >= 0.43f and < 0.62f && MathF.Abs(nx - 0.5f) < 0.12f)
            return RigRole.Hips;
        if (ny >= 0.43f)
            return nx < 0.5f ? RigRole.LegLeft : RigRole.LegRight;
        return RigRole.Spine;
    }

    private static HashSet<int> FindBodyIslands(List<SlicePartEntry> parts)
    {
        Dictionary<int, List<SlicePartEntry>> islands = GroupBySourceIsland(parts);
        int primary = int.MaxValue;
        long largestArea = -1;
        foreach ((int island, List<SlicePartEntry> islandParts) in islands)
        {
            long area = 0;
            foreach (SlicePartEntry part in islandParts)
                area += part.PixelCount;
            if (area > largestArea || area == largestArea && island < primary)
            {
                largestArea = area;
                primary = island;
            }
        }
        Bounds primaryBounds = Bounds.Of(islands[primary]);
        float joinDistance = MathF.Max(4f, MathF.Min(primaryBounds.Width, primaryBounds.Height) * 0.02f);
        var body = new HashSet<int> { primary };

        bool changed;
        do
        {
            changed = false;
            foreach ((int island, List<SlicePartEntry> islandParts) in islands)
            {
                if (body.Contains(island))
                    continue;
                Bounds candidate = Bounds.Of(islandParts);
                if (!TouchesAnyIsland(body, islands, candidate, joinDistance))
                    continue;
                body.Add(island);
                changed = true;
            }
        } while (changed);

        return body;
    }

    private static List<List<SlicePartEntry>> GroupDetachedProps(List<SlicePartEntry> parts, Bounds body)
    {
        if (parts.Count == 0)
            return [];
        Dictionary<int, List<SlicePartEntry>> islands = GroupBySourceIsland(parts);
        float joinDistance = MathF.Max(6f, MathF.Min(body.Width, body.Height) * 0.01f);
        var remaining = new HashSet<int>(islands.Keys);
        var result = new List<List<SlicePartEntry>>();
        while (remaining.Count > 0)
        {
            int seed = int.MaxValue;
            foreach (int island in remaining)
                seed = Math.Min(seed, island);
            remaining.Remove(seed);
            var assemblyIds = new HashSet<int> { seed };
            bool changed;
            do
            {
                changed = false;
                var candidates = new List<int>(remaining);
                foreach (int candidate in candidates)
                {
                    Bounds candidateBounds = Bounds.Of(islands[candidate]);
                    if (!TouchesAnyIsland(assemblyIds, islands, candidateBounds, joinDistance))
                        continue;
                    remaining.Remove(candidate);
                    assemblyIds.Add(candidate);
                    changed = true;
                }
            } while (changed);

            var orderedIds = new List<int>(assemblyIds);
            orderedIds.Sort();
            var assembly = new List<SlicePartEntry>();
            foreach (int island in orderedIds)
                assembly.AddRange(islands[island]);
            result.Add(assembly);
        }
        return result;
    }

    private static Dictionary<int, List<SlicePartEntry>> GroupBySourceIsland(
        List<SlicePartEntry> parts)
    {
        var result = new Dictionary<int, List<SlicePartEntry>>();
        foreach (SlicePartEntry part in parts)
        {
            if (!result.TryGetValue(part.SourceIsland, out List<SlicePartEntry>? islandParts))
            {
                islandParts = [];
                result.Add(part.SourceIsland, islandParts);
            }
            islandParts.Add(part);
        }
        return result;
    }

    private static bool TouchesAnyIsland(HashSet<int> ids,
        Dictionary<int, List<SlicePartEntry>> islands, Bounds candidate, float joinDistance)
    {
        foreach (int id in ids)
            if (Bounds.Distance(Bounds.Of(islands[id]), candidate) <= joinDistance)
                return true;
        return false;
    }

    private static SlicePartEntry FindTorso(List<SlicePartEntry> parts, Bounds body)
    {
        SlicePartEntry best = parts[0];
        float bestScore = float.MinValue;
        foreach (SlicePartEntry part in parts)
        {
            float nx = (part.X + part.Width / 2f - body.Left) / Math.Max(1f, body.Width);
            float ny = (part.Y + part.Height / 2f - body.Top) / Math.Max(1f, body.Height);
            float centrality = 1f - MathF.Min(1f, MathF.Abs(nx - 0.5f) * 2f);
            float verticality = 1f - MathF.Min(1f, MathF.Abs(ny - 0.32f) / 0.55f);
            float score = part.PixelCount * (0.25f + centrality * 0.75f) *
                          (0.35f + verticality * 0.65f);
            if (score > bestScore || score == bestScore && part.Layer < best.Layer)
            {
                best = part;
                bestScore = score;
            }
        }
        return best;
    }

    private static void BuildHead(RigManifest manifest, List<SlicePartEntry> parts, Bounds body,
        Dictionary<string, string> deformByPart)
    {
        if (parts.Count == 0)
            return;
        float headBottom = float.MinValue;
        foreach (SlicePartEntry part in parts)
            headBottom = MathF.Max(headBottom, part.Y + part.Height);
        float neckY = MathF.Min(body.Top + body.Height * 0.24f, headBottom);
        Ensure(manifest, "head", "spine", WeightedCenterX(parts), neckY,
            MathF.Max(1f, Bounds.Of(parts).Height));
        foreach (SlicePartEntry part in parts)
            deformByPart[part.Name] = "head";
    }

    private static void BuildArm(RigManifest manifest, List<SlicePartEntry> parts, bool left,
        Bounds body, Dictionary<string, string> deformByPart)
    {
        if (parts.Count == 0)
            return;
        Bounds cluster = Bounds.Of(parts);
        string side = left ? "l" : "r";
        float shoulderY = cluster.Top + cluster.Height * 0.22f;
        float elbowY = cluster.Top + cluster.Height * 0.62f;
        float wristY = cluster.Top + cluster.Height * 0.86f;
        var shoulderParts = new List<SlicePartEntry>();
        float shoulderBandBottom = cluster.Top + cluster.Height * 0.42f;
        foreach (SlicePartEntry part in parts)
            if (part.Y + part.Height / 2f <= shoulderBandBottom)
                shoulderParts.Add(part);
        if (shoulderParts.Count == 0)
            shoulderParts = parts;
        float shoulderX = left ? float.MinValue : float.MaxValue;
        foreach (SlicePartEntry part in shoulderParts)
            shoulderX = left
                ? MathF.Max(shoulderX, part.X + part.Width)
                : MathF.Min(shoulderX, part.X);
        shoulderX = Math.Clamp(shoulderX, body.Left, body.Right);
        float elbowX = CenterNearY(parts, elbowY);
        // The largest distal shard is commonly a forearm guard rather than the hand (U8's
        // gauntlet is a good example).  A weighted average therefore pulls the wrist back into
        // the torso and makes a snapped weapon hang between the legs.  Use the outward-most
        // substantial shard in the distal arm band for X, while keeping the stable percentile Y.
        float wristX = OutwardDistalCenterX(parts, left, cluster);

        Ensure(manifest, $"arm_{side}", "spine", shoulderX, shoulderY,
            Distance(shoulderX, shoulderY, elbowX, elbowY));
        Ensure(manifest, $"forearm_{side}", $"arm_{side}", elbowX, elbowY,
            Distance(elbowX, elbowY, wristX, wristY));
        Ensure(manifest, $"hand_{side}", $"forearm_{side}", wristX, wristY,
            MathF.Max(1f, cluster.Bottom - wristY));

        float upperCut = (shoulderY + elbowY) * 0.5f;
        float lowerCut = (elbowY + wristY) * 0.5f;
        foreach (SlicePartEntry part in parts)
        {
            float cy = part.Y + part.Height / 2f;
            deformByPart[part.Name] = cy < upperCut
                ? $"arm_{side}"
                : cy < lowerCut ? $"forearm_{side}" : $"hand_{side}";
        }
    }

    private static void BuildLeg(RigManifest manifest, List<SlicePartEntry> parts, bool left,
        Bounds body, Dictionary<string, string> deformByPart)
    {
        if (parts.Count == 0)
            return;
        Bounds cluster = Bounds.Of(parts);
        string side = left ? "l" : "r";
        float hipY = MathF.Min(cluster.Top, body.Top + body.Height * 0.58f);
        float kneeY = cluster.Top + cluster.Height * 0.46f;
        float ankleY = cluster.Top + cluster.Height * 0.82f;
        float hipX = CenterNearY(parts, cluster.Top + cluster.Height * 0.08f);
        float kneeX = CenterNearY(parts, kneeY);
        float ankleX = CenterNearY(parts, ankleY);

        Ensure(manifest, $"leg_{side}", "hips", hipX, hipY,
            Distance(hipX, hipY, kneeX, kneeY));
        Ensure(manifest, $"shin_{side}", $"leg_{side}", kneeX, kneeY,
            Distance(kneeX, kneeY, ankleX, ankleY));
        Ensure(manifest, $"foot_{side}", $"shin_{side}", ankleX, ankleY,
            MathF.Max(1f, cluster.Bottom - ankleY));

        float upperCut = (hipY + kneeY) * 0.5f;
        float lowerCut = (kneeY + ankleY) * 0.5f;
        foreach (SlicePartEntry part in parts)
        {
            float cy = part.Y + part.Height / 2f;
            deformByPart[part.Name] = cy < upperCut
                ? $"leg_{side}"
                : cy < lowerCut ? $"shin_{side}" : $"foot_{side}";
        }
    }

    private static float CenterNearY(List<SlicePartEntry> parts, float targetY)
    {
        int take = Math.Max(1, parts.Count / 3);
        var nearest = new List<SlicePartEntry>(parts);
        nearest.Sort((a, b) =>
        {
            float da = MathF.Abs(a.Y + a.Height / 2f - targetY);
            float db = MathF.Abs(b.Y + b.Height / 2f - targetY);
            int byDistance = da.CompareTo(db);
            return byDistance != 0 ? byDistance : a.Layer.CompareTo(b.Layer);
        });
        if (nearest.Count > take)
            nearest.RemoveRange(take, nearest.Count - take);
        return WeightedCenterX(nearest);
    }

    private static float OutwardDistalCenterX(List<SlicePartEntry> parts, bool left, Bounds cluster)
    {
        int largestPixels = 1;
        foreach (SlicePartEntry part in parts)
            largestPixels = Math.Max(largestPixels, part.PixelCount);
        float distalStart = cluster.Top + cluster.Height * 0.55f;
        var candidates = new List<SlicePartEntry>();
        foreach (SlicePartEntry part in parts)
            if (part.Y + part.Height / 2f >= distalStart &&
                part.PixelCount >= largestPixels * 0.05f)
                candidates.Add(part);
        if (candidates.Count == 0)
            candidates = parts;

        SlicePartEntry outward = candidates[0];
        float outwardX = outward.X + outward.Width / 2f;
        for (int i = 1; i < candidates.Count; i++)
        {
            SlicePartEntry candidate = candidates[i];
            float candidateX = candidate.X + candidate.Width / 2f;
            if (left ? candidateX < outwardX : candidateX > outwardX)
            {
                outward = candidate;
                outwardX = candidateX;
            }
        }
        return outward.X + outward.Width / 2f;
    }

    private static float WeightedCenterX(List<SlicePartEntry> parts)
    {
        double total = 0;
        double weightedX = 0;
        foreach (SlicePartEntry part in parts)
        {
            int weight = Math.Max(1, part.PixelCount);
            total += weight;
            weightedX += (part.X + part.Width / 2.0) * weight;
        }
        return (float)(weightedX / total);
    }

    private static float Distance(float ax, float ay, float bx, float by)
    {
        float dx = bx - ax;
        float dy = by - ay;
        return MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
    }

    private readonly record struct Bounds(float Left, float Top, float Right, float Bottom)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
        public float CenterX => (Left + Right) * 0.5f;
        public float CenterY => (Top + Bottom) * 0.5f;

        public static Bounds Of(IEnumerable<SlicePartEntry> source)
        {
            float left = float.MaxValue;
            float top = float.MaxValue;
            float right = float.MinValue;
            float bottom = float.MinValue;
            bool any = false;
            foreach (SlicePartEntry part in source)
            {
                any = true;
                left = MathF.Min(left, part.X);
                top = MathF.Min(top, part.Y);
                right = MathF.Max(right, part.X + part.Width);
                bottom = MathF.Max(bottom, part.Y + part.Height);
            }
            if (!any)
                throw new InvalidOperationException("Cannot calculate bounds for an empty part set.");
            return new Bounds(left, top, right, bottom);
        }

        public static float Distance(Bounds a, Bounds b)
        {
            float dx = MathF.Max(0, MathF.Max(a.Left - b.Right, b.Left - a.Right));
            float dy = MathF.Max(0, MathF.Max(a.Top - b.Bottom, b.Top - a.Bottom));
            return MathF.Sqrt(dx * dx + dy * dy);
        }
    }

    private static void Ensure(RigManifest manifest, string name, string parent, float imageX,
        float imageY, float length)
    {
        if (manifest.Bones.Any(b => b.Name == name))
            return;
        manifest.Bones.Add(new RigBoneInfo
        {
            Name = name,
            Parent = parent,
            ImageX = imageX,
            ImageY = imageY,
            Length = length > 0 ? length : 1f,
        });
    }

    private static SpineSkeletonDoc BuildDoc(RigManifest manifest, SliceManifest slice)
    {
        var doc = new SpineSkeletonDoc
        {
            Skeleton = new SpineSkeletonMeta
            {
                X = 0,
                Y = 0,
                Width = slice.ImageWidth,
                Height = slice.ImageHeight,
                Fps = 30,
                Images = ".",
            },
            Bones = [],
            Slots = [],
            Skins = [new SpineSkinDef()],
            Animations = [],
        };

        // Spine is y-up, the pipeline is y-down; bones store PARENT-RELATIVE positions in spine
        // space: dx = imgX_child - imgX_parent, dySpine = imgY_parent - imgY_child. The root keeps
        // absolute spine position (imgX, imageHeight - imgY).
        Dictionary<string, RigBoneInfo> byName = manifest.Bones.ToDictionary(b => b.Name);
        foreach (string name in OrderBones(byName))
        {
            RigBoneInfo info = byName[name];
            float x;
            float y;
            if (info.Parent is { } parentName)
            {
                RigBoneInfo parent = byName[parentName];
                x = info.ImageX - parent.ImageX;
                y = parent.ImageY - info.ImageY;
            }
            else
            {
                x = info.ImageX;
                y = manifest.ImageHeight - info.ImageY;
            }

            doc.Bones.Add(new SpineBoneDef
            {
                Name = name,
                Parent = info.Parent,
                X = x,
                Y = y,
                Rotation = 0f,
            });
        }

        var bindings = manifest.Parts.ToDictionary(b => b.Part, b => b);
        var orderedParts = new List<SlicePartEntry>(slice.Parts);
        orderedParts.Sort((a, b) =>
        {
            int aProp = bindings[a.Name].Role == RigRole.Prop ? 1 : 0;
            int bProp = bindings[b.Name].Role == RigRole.Prop ? 1 : 0;
            int byRole = aProp.CompareTo(bProp);
            return byRole != 0 ? byRole : a.Layer.CompareTo(b.Layer);
        });
        foreach (SlicePartEntry part in orderedParts)
        {
            RigPartBinding binding = bindings[part.Name];
            RigBoneInfo anchorBone = byName[binding.Bone];

            doc.Slots.Add(new SpineSlotDef
            {
                Name = $"{part.Name}_slot",
                Bone = binding.Bone,
                Attachment = part.Name,
            });

            // Attachment center relative to its joint; spine +y points up so the image dy flips.
            float centerX = part.X + part.Width / 2f + binding.OffsetX;
            float centerY = part.Y + part.Height / 2f + binding.OffsetY;
            doc.Skins[0].Add($"{part.Name}_slot", part.Name, SpineAttachment.Region(
                path: part.Name,
                x: centerX - anchorBone.ImageX,
                y: anchorBone.ImageY - centerY,
                rotation: 0,
                width: part.Width,
                height: part.Height));
        }

        return doc;
    }

    private static List<string> OrderBones(Dictionary<string, RigBoneInfo> byName)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name)
        {
            if (!visited.Add(name))
                return;
            if (byName[name].Parent is { } parent)
                Visit(parent);
            result.Add(name);
        }
        foreach (string name in byName.Keys)
            Visit(name);
        return result;
    }
}
