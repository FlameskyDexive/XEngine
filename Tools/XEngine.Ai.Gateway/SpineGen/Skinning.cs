// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json.Serialization;

namespace XEngine.Ai.SpineGen;

public sealed class SkinParams
{
    /// <summary>Alpha (of the filled part png) at/above which a pixel joins the mesh silhouette.</summary>
    [JsonPropertyName("alphaThreshold")] public int AlphaThreshold { get; init; } = 8;
    /// <summary>Douglas–Peucker simplification tolerance, in source pixels.</summary>
    [JsonPropertyName("simplifyEpsilon")] public float SimplifyEpsilon { get; init; } = 1.5f;
    /// <summary>Cap on kept hull vertices (largest-perimeter-relevant subset after simplify).</summary>
    [JsonPropertyName("maxHullVertices")] public int MaxHullVertices { get; init; } = 64;
    /// <summary>Parts below this opaque-pixel count stay plain regions.</summary>
    [JsonPropertyName("minMeshPixels")] public int MinMeshPixels { get; init; } = 24;
}

public sealed class SkinPartResult
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("meshed")] public required bool Meshed { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("hullVertexCount")] public int HullVertexCount { get; init; }
    [JsonPropertyName("triangleCount")] public int TriangleCount { get; init; }
    [JsonPropertyName("coverage")] public double Coverage { get; init; }
    [JsonPropertyName("weighted")] public bool Weighted { get; init; }
    [JsonPropertyName("influenceBones")] public List<string> InfluenceBones { get; init; } = [];
}

/// <summary>
/// Turns each sliced part's alpha silhouette into a Spine mesh attachment: Moore-neighborhood
/// boundary trace → Douglas-Peucker simplification → ear-clipping triangulation of the (possibly
/// concave) polygon. Vertex positions land in the same bone-local frame the region path proved
/// (x = imgX − anchorImgX, y = anchorImgY − imgY); UVs are region-relative and normalized to
/// 0..1 with y top-down (Spine mesh convention — ComputeUVs scales them by the region rect).
/// Concave shapes keep their real silhouette instead of a bounding quad — the whole point of meshes.
/// </summary>
public static class Skinning
{
    public static SpineAttachment BuildMeshAttachment(
        SlicePartEntry part, SliceAtlasPlacement atlas,
        byte[] partRgba, RigPartBinding binding, RigBoneInfo anchorBone,
        SkinParams parameters, out SkinPartResult result)
        => BuildMeshAttachmentCore(part, atlas, partRgba, binding, anchorBone,
            rig: null, orderedBones: null, parameters, out result);

    /// <summary>Builds a production mesh with a two-bone weight field around anatomical joints.
    /// The explicit ordered bone list is the Spine skeleton index contract.</summary>
    public static SpineAttachment BuildMeshAttachment(
        SlicePartEntry part, SliceAtlasPlacement atlas,
        byte[] partRgba, RigPartBinding binding, RigBoneInfo anchorBone,
        RigManifest rig, IReadOnlyList<SpineBoneDef> orderedBones,
        SkinParams parameters, out SkinPartResult result)
        => BuildMeshAttachmentCore(part, atlas, partRgba, binding, anchorBone,
            rig, orderedBones, parameters, out result);

    private static SpineAttachment BuildMeshAttachmentCore(
        SlicePartEntry part, SliceAtlasPlacement atlas,
        byte[] partRgba, RigPartBinding binding, RigBoneInfo anchorBone,
        RigManifest? rig, IReadOnlyList<SpineBoneDef>? orderedBones,
        SkinParams parameters, out SkinPartResult result)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(partRgba);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(anchorBone);
        ArgumentNullException.ThrowIfNull(parameters);

        int w = part.Width;
        int h = part.Height;
        if (partRgba.Length < w * h * 4)
            throw new ArgumentException("Part RGBA does not match its manifest dims.");

        byte[] mask = MaskFromAlpha(partRgba, w, h, parameters.AlphaThreshold);
        int pixels = CountMask(mask);
        if (pixels < parameters.MinMeshPixels)
        {
            result = new SkinPartResult { Name = part.Name, Meshed = false, Reason = $"too small ({pixels}px)" };
            return RegionFallback(part, atlas, binding, anchorBone);
        }

        List<(int x, int y)> contour = TraceContour(mask, w, h);
        if (contour.Count < 3)
        {
            result = new SkinPartResult { Name = part.Name, Meshed = false, Reason = "degenerate contour" };
            return RegionFallback(part, atlas, binding, anchorBone);
        }

        List<(float x, float y)> poly = Simplify(contour.Select(p => ((float)p.x, (float)p.y)),
            parameters.SimplifyEpsilon);
        poly = EnforceMax(poly, parameters.MaxHullVertices);

        List<int> triangles = TriangulateEarClip(poly);
        if (triangles.Count < 3)
        {
            result = new SkinPartResult { Name = part.Name, Meshed = false, Reason = "triangulation failed" };
            return RegionFallback(part, atlas, binding, anchorBone);
        }

        // Bone-local vertex coords + region-relative normalized UVs (Spine mesh convention:
        // regionUVs are 0..1 inside the region rect, y top-down — matches AtlasRegion v being
        // computed top-down in the vendored runtime, and matches the packer's crop orientation).
        var unweightedVertices = new float[poly.Count * 2];
        var uvs = new float[poly.Count * 2];
        for (int i = 0; i < poly.Count; i++)
        {
            (float px, float py) = poly[i];
            // px/py are CROP-local; convert to image space first, then into anchor frame.
            float imgX = part.X + px + binding.OffsetX;
            float imgY = part.Y + py + binding.OffsetY;
            unweightedVertices[i * 2] = imgX - anchorBone.ImageX;
            unweightedVertices[i * 2 + 1] = anchorBone.ImageY - imgY;

            uvs[i * 2] = px / atlas.Width;
            uvs[i * 2 + 1] = py / atlas.Height;
        }

        double polygonArea = Math.Abs(PolygonAreaTwice(poly)) / 2.0;
        double coverage = polygonArea <= 0 ? 0 : pixels / polygonArea;

        SpineMeshVertices meshVertices = SpineMeshVertices.Unweighted(unweightedVertices);
        List<string> influenceNames = [];
        if (rig != null && orderedBones != null && binding.Role != RigRole.Prop &&
            TryBuildWeightedVertices(poly, part, binding, rig, orderedBones,
                out SpineMeshVertices? weighted, out influenceNames))
        {
            meshVertices = weighted;
        }

        result = new SkinPartResult
        {
            Name = part.Name,
            Meshed = true,
            Reason = "meshed",
            HullVertexCount = poly.Count,
            TriangleCount = triangles.Count / 3,
            Coverage = Math.Round(Math.Min(coverage, 1.0), 3),
            Weighted = influenceNames.Count > 1,
            InfluenceBones = influenceNames,
        };

        return SpineAttachment.Mesh(
            path: part.Name,
            vertices: meshVertices,
            uvs: uvs,
            triangles: triangles.ToArray(),
            hullPointCount: poly.Count,
            width: part.Width,
            height: part.Height);
    }

    public static byte[] MaskFromAlpha(byte[] rgba, int w, int h, int threshold)
    {
        var mask = new byte[w * h];
        for (int i = 0; i < w * h; i++)
            if (rgba[i * 4 + 3] >= threshold)
                mask[i] = 255;
        return mask;
    }

    private static int CountMask(byte[] mask)
    {
        int n = 0;
        foreach (byte b in mask)
            if (b != 0)
                n++;
        return n;
    }

    private static SpineAttachment RegionFallback(SlicePartEntry part, SliceAtlasPlacement atlas,
        RigPartBinding binding, RigBoneInfo anchorBone)
    {
        float centerX = part.X + part.Width / 2f + binding.OffsetX;
        float centerY = part.Y + part.Height / 2f + binding.OffsetY;
        return SpineAttachment.Region(
            path: part.Name,
            x: centerX - anchorBone.ImageX,
            y: anchorBone.ImageY - centerY,
            rotation: 0,
            width: atlas.Width,
            height: atlas.Height);
    }

    private static bool TryBuildWeightedVertices(
        IReadOnlyList<(float x, float y)> poly, SlicePartEntry part, RigPartBinding binding,
        RigManifest rig, IReadOnlyList<SpineBoneDef> orderedBones,
        out SpineMeshVertices vertices, out List<string> influenceNames)
    {
        vertices = SpineMeshVertices.Unweighted([]);
        influenceNames = [];
        string childName = string.IsNullOrEmpty(binding.DeformBone) ? binding.Bone : binding.DeformBone;
        var byName = new Dictionary<string, RigBoneInfo>(rig.Bones.Count, StringComparer.Ordinal);
        foreach (RigBoneInfo bone in rig.Bones)
            byName.Add(bone.Name, bone);
        if (!byName.TryGetValue(childName, out RigBoneInfo? child) || child.Parent == null ||
            !byName.TryGetValue(child.Parent, out RigBoneInfo? parent))
            return false;

        // Rigid hip plates should not smear into root translation. Every articulated chain and
        // the torso/head use a parent+child field around the joint.
        if (binding.Role == RigRole.Hips)
            return false;

        var indices = new Dictionary<string, int>(orderedBones.Count, StringComparer.Ordinal);
        for (int i = 0; i < orderedBones.Count; i++)
            indices.Add(orderedBones[i].Name, i);
        if (!indices.TryGetValue(parent.Name, out int parentIndex) ||
            !indices.TryGetValue(child.Name, out int childIndex))
            return false;

        RigBoneInfo? next = FindSemanticChild(childName, byName);
        float dirX;
        float dirY;
        float segmentLength;
        if (next != null)
        {
            dirX = next.ImageX - child.ImageX;
            dirY = next.ImageY - child.ImageY;
            segmentLength = MathF.Sqrt(dirX * dirX + dirY * dirY);
        }
        else
        {
            // Continue away from the parent (head, hand and foot terminal bones).
            dirX = child.ImageX - parent.ImageX;
            dirY = child.ImageY - parent.ImageY;
            segmentLength = MathF.Sqrt(dirX * dirX + dirY * dirY);
        }
        if (segmentLength < 1f)
            return false;
        dirX /= segmentLength;
        dirY /= segmentLength;

        var bones = new int[poly.Count * 3]; // 2,parent,child for every vertex.
        var values = new float[poly.Count * 6];
        for (int i = 0; i < poly.Count; i++)
        {
            float imgX = part.X + poly[i].x + binding.OffsetX;
            float imgY = part.Y + poly[i].y + binding.OffsetY;
            float along = ((imgX - child.ImageX) * dirX + (imgY - child.ImageY) * dirY) /
                          segmentLength;
            // Parent owns pixels immediately behind the joint; the child takes over smoothly
            // through the first quarter of the next segment. Adjacent shards evaluate the same
            // global field, so both sides of a watershed seam stay welded.
            float childWeight = SmoothStep(-0.08f, 0.25f, along);
            float parentWeight = 1f - childWeight;

            int bi = i * 3;
            bones[bi] = 2;
            bones[bi + 1] = parentIndex;
            bones[bi + 2] = childIndex;

            int vi = i * 6;
            values[vi] = imgX - parent.ImageX;
            values[vi + 1] = parent.ImageY - imgY;
            values[vi + 2] = parentWeight;
            values[vi + 3] = imgX - child.ImageX;
            values[vi + 4] = child.ImageY - imgY;
            values[vi + 5] = childWeight;
        }

        vertices = SpineMeshVertices.Weighted(bones, values);
        influenceNames = [parent.Name, child.Name];
        return true;
    }

    private static RigBoneInfo? FindSemanticChild(string parent,
        Dictionary<string, RigBoneInfo> bones)
    {
        string? expected = parent switch
        {
            "arm_l" => "forearm_l",
            "arm_r" => "forearm_r",
            "forearm_l" => "hand_l",
            "forearm_r" => "hand_r",
            "leg_l" => "shin_l",
            "leg_r" => "shin_r",
            "shin_l" => "foot_l",
            "shin_r" => "foot_r",
            _ => null,
        };
        return expected != null && bones.TryGetValue(expected, out RigBoneInfo? child) ? child : null;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Moore-neighborhood boundary tracing starting from the topmost-leftmost mask pixel;
    /// yields the OUTER contour clockwise (image coords).</summary>
    internal static List<(int x, int y)> TraceContour(byte[] mask, int w, int h)
    {
        int start = -1;
        for (int y = 0; y < h && start < 0; y++)
            for (int x = 0; x < w; x++)
                if (mask[y * w + x] != 0)
                {
                    start = y * w + x;
                    break;
                }
        if (start < 0)
            return [];

        // 8 directions, east-first clockwise scan (Jacob's stopping criterion).
        (int dx, int dy)[] dirs =
        [
            (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1),
        ];
        int startX = start % w;
        int startY = start / w;
        var contour = new List<(int x, int y)> { (startX, startY) };
        int dir = 6; // arrived scanning down; backtrack direction west-ish
        int current = start;
        int guard = w * h * 8;

        while (guard-- > 0)
        {
            bool found = false;
            for (int k = 0; k < 8; k++)
            {
                int d = (dir + k) % 8;
                int nx = current % w + dirs[d].dx;
                int ny = current / w + dirs[d].dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || mask[ny * w + nx] == 0)
                    continue;
                current = ny * w + nx;
                contour.Add((nx, ny));
                dir = (d + 6) % 8; // reverse-and-step ordering for next search
                found = true;
                break;
            }

            if (!found)
                break;
            if (current == start && contour.Count > 2)
                break;
        }

        return contour;
    }

    /// <summary>Douglas–Peucker on a closed polyline (baseline = the two farthest-apart points).</summary>
    internal static List<(float x, float y)> Simplify(IEnumerable<(float x, float y)> contour, float epsilon)
    {
        var points = contour.ToList();
        if (points.Count <= 4)
            return points;

        int farA = 0, farB = 0;
        double best = -1;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                double d = Dist(points[i], points[j]);
                if (d > best)
                {
                    best = d;
                    farA = i;
                    farB = j;
                }
            }
        }

        var keep = new bool[points.Count];
        keep[farA] = keep[farB] = true;
        SimplifyRange(points, keep, farA, farB, epsilon);
        SimplifyRange(points, keep, farB, farA, epsilon);

        var result = new List<(float x, float y)>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i])
                result.Add(points[i]);
        return result;
    }

    private static void SimplifyRange(List<(float x, float y)> pts, bool[] keep, int a, int b, float eps)
    {
        if ((a + 1) % pts.Count == b || a == b)
            return;
        double maxDist = -1;
        int maxIdx = -1;
        for (int i = (a + 1) % pts.Count; i != b; i = (i + 1) % pts.Count)
        {
            double d = SegmentDistance(pts[i], pts[a], pts[b]);
            if (d > maxDist)
            {
                maxDist = d;
                maxIdx = i;
            }
        }

        if (maxIdx >= 0 && maxDist > eps)
        {
            keep[maxIdx] = true;
            SimplifyRange(pts, keep, a, maxIdx, eps);
            SimplifyRange(pts, keep, maxIdx, b, eps);
        }
    }

    internal static List<(float x, float y)> EnforceMax(List<(float x, float y)> poly, int max)
    {
        if (poly.Count <= max || max < 3)
            return poly;
        double step = poly.Count / (double)max;
        var reduced = new List<(float x, float y)>(max);
        for (double i = 0; (int)i < poly.Count; i += step)
            reduced.Add(poly[(int)i]);
        return reduced;
    }

    /// <summary>Signed-twice-area positive ⇒ counter-clockwise.</summary>
    internal static double PolygonAreaTwice(IReadOnlyList<(float x, float y)> poly)
    {
        double sum = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var (x1, y1) = poly[i];
            var (x2, y2) = poly[(i + 1) % poly.Count];
            sum += x1 * (y2 - y1) - y1 * (x2 - x1);
        }
        return sum;
    }

    /// <summary>Ear clipping; tolerates concave outlines. Returns index triples.</summary>
    internal static List<int> TriangulateEarClip(IReadOnlyList<(float x, float y)> poly)
    {
        var indices = Enumerable.Range(0, poly.Count).ToList();
        // Ensure CCW ordering for the classic algorithm.
        if (PolygonAreaTwice(poly) < 0)
            indices.Reverse();

        var triangles = new List<int>(Math.Max(0, (indices.Count - 2) * 3));
        int guard = indices.Count * indices.Count + 16;
        while (indices.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < indices.Count; i++)
            {
                int prev = indices[(i + indices.Count - 1) % indices.Count];
                int cur = indices[i];
                int next = indices[(i + 1) % indices.Count];

                if (IsReflex(poly[prev], poly[cur], poly[next]))
                    continue;

                bool containsOther = false;
                foreach (int other in indices)
                {
                    if (other == prev || other == cur || other == next)
                        continue;
                    if (PointInTriangle(poly[other], poly[prev], poly[cur], poly[next]))
                    {
                        containsOther = true;
                        break;
                    }
                }

                if (containsOther)
                    continue;

                triangles.AddRange([prev, cur, next]);
                indices.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
                break; // numeric fallback: bail out rather than loop forever
        }

        if (indices.Count == 3)
            triangles.AddRange([indices[0], indices[1], indices[2]]);
        return triangles;
    }

    private static bool IsReflex((float x, float y) a, (float x, float y) b, (float x, float y) c)
    {
        float cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
        return cross < 0;
    }

    private static bool PointInTriangle((float x, float y) p,
        (float x, float y) a, (float x, float y) b, (float x, float y) c)
    {
        const float e = 1e-4f;
        float d1 = Cross(p, a, b);
        float d2 = Cross(p, b, c);
        float d3 = Cross(p, c, a);
        bool hasNeg = d1 < -e || d2 < -e || d3 < -e;
        bool hasPos = d1 > e || d2 > e || d3 > e;
        return !(hasNeg && hasPos);
    }

    private static float Cross((float x, float y) p, (float x, float y) a, (float x, float y) b) =>
        (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

    private static double Dist((float x, float y) p, (float x, float y) q)
    {
        float dx = p.x - q.x;
        float dy = p.y - q.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SegmentDistance((float x, float y) p, (float x, float y) a, (float x, float y) b)
    {
        float abx = b.x - a.x;
        float aby = b.y - a.y;
        float apx = p.x - a.x;
        float apy = p.y - a.y;
        double lenSq = abx * abx + aby * aby;
        if (lenSq <= 0)
            return Dist(p, a);
        double t = Math.Clamp((apx * abx + apy * aby) / lenSq, 0, 1);
        float cx = p.x - (a.x + (float)t * abx);
        float cy = p.y - (a.y + (float)t * aby);
        return Math.Sqrt(cx * cx + cy * cy);
    }
}
