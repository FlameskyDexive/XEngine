// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using SpineRT = global::Spine;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>M5 mesh-skinning acceptance on exact geometry (square = 4 hulls / 2 triangles,
/// concave L keeps its notch), then a full bear run asserting atlas UV bounds.</summary>
public sealed class SpineGenSkinningTests
{
    private static byte[] MaskSquare(int w, int h, int x0, int y0, int x1, int y1)
    {
        var rgba = new byte[w * h * 4];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                Put(rgba, w, x, y, 200, 100, 50);
        return rgba;
    }

    private static void Put(byte[] rgba, int width, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * width + x) * 4;
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
        rgba[i + 3] = 255;
    }

    [Fact]
    public void Square_MeshesToFourCornersAndTwoTriangles()
    {
        const int w = 20, h = 20;
        byte[] rgba = MaskSquare(w, h, 4, 5, 13, 14); // 10x10 square
        byte[] mask = Skinning.MaskFromAlpha(rgba, w, h, 8);

        var contour = Skinning.TraceContour(mask, w, h);
        // Perimeter of a 10x10 block is 40; the tracer may fold the closing corner once.
        Assert.InRange(contour.Count, 36, 42);

        var poly = Skinning.Simplify(contour.Select(p => ((float)p.x, (float)p.y)), epsilon: 0.6f);
        // Corner-only silhouette after DP.
        Assert.True(poly.Count is >= 4 and <= 8, $"got {poly.Count}: {string.Join(';', poly)}");
        foreach ((float px, float py) in poly)
            Assert.True(
                (px is > 3.4f and < 5f || px is > 12.4f && px is < 14f) ||
                (py is > 4.4f and < 6f || py is > 13.4f && py is < 15f),
                $"kept vertex {px},{py} must hug the square's edges");

        List<int> tris = Skinning.TriangulateEarClip(poly);
        Assert.Equal((poly.Count - 2) * 3, tris.Count);

        foreach (int idx in tris)
            Assert.InRange(idx, 0, poly.Count - 1);
    }

    [Fact]
    public void ConcaveL_TriangulatesWithoutLosingArea()
    {
        const int w = 24, h = 24;
        var mask = new byte[w * h];
        for (int y = 2; y < 20; y++)
            for (int x = 2; x < 20; x++)
                if (!(x > 11 && y > 11))
                    mask[y * w + x] = 255; // L: full square minus its bottom-right quadrant

        var contour = Skinning.TraceContour(mask, w, h);
        var poly = Skinning.Simplify(contour.Select(p => ((float)p.x, (float)p.y)), epsilon: 0.8f);
        Assert.InRange(poly.Count, 6, 8); // six corners plus at most one notch midpoint
        Assert.Contains(poly, p => p.x is > 9f and < 14f && p.y is > 9f and < 14f);
        // the notch (concave corner region) must survive simplification

        List<int> tris = Skinning.TriangulateEarClip(poly);
        Assert.Equal((poly.Count - 2) * 3, tris.Count);

        double triAreaSum = SumTriangleArea(poly, tris);
        double polyArea = Math.Abs(Skinning.PolygonAreaTwice(poly)) / 2.0;
        Assert.Equal(polyArea, triAreaSum, 3);
    }

    [Fact]
    public async Task BearSkin_UpgradesPartsToMeshesWithinAtlasBounds()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"),
                PngRgba.Encode(SpineGenSliceTests.BuildBearRgba(out _),
                    SpineGenSliceTests.W, SpineGenSliceTests.H));
            var project = new ProjectDescriptor("t", root, null, null, null, "fp");
            string sliceDir = new SpineGenSliceService(project)
                .Slice("Assets/bear.png", null, null, null, null, null, null, null, false).OutputDirectory;

            new SpineGenRigService(project).Rig(sliceDir);

            SpineGenSkinResult skinned =
                new SpineGenSkinService(project).Skin(sliceDir, null, null, null);
            Assert.True(skinned.MeshedParts >= 3, $"{skinned.MeshedParts} parts meshed");

            string absDir = Path.Combine(root, sliceDir.Replace('/', Path.DirectorySeparatorChar));
            var sliceManifest = JsonSerializer.Deserialize<SliceManifest>(
                File.ReadAllText(Path.Combine(absDir, "slice_manifest.json")))!;
            string skeletonJson = File.ReadAllText(Path.Combine(absDir, "rig", "skeleton.json"));

            SpineRT.SkeletonData data = new SpineRT.SkeletonJson(new SpineGenRigTests.StubLoader())
                .ReadSkeletonData(new StringReader(skeletonJson));
            var skel = new SpineRT.Skeleton(data);
            skel.UpdateWorldTransform(SpineRT.Physics.Update);

            int meshesSeen = 0;
            foreach (SpineRT.Slot slot in skel.Slots.Items.Take(skel.Slots.Count))
            {
                if (slot.Pose.Attachment is not SpineRT.MeshAttachment mesh)
                    continue;
                meshesSeen++;

                SlicePartEntry entry = sliceManifest.Parts.First(
                    p => $"{p.Name}_slot" == slot.Data.Name);
                SliceAtlasPlacement atlas = entry.Atlas;

                var world = new float[mesh.WorldVerticesLength];
                mesh.ComputeWorldVertices(skel, slot, world);

                float imgH = sliceManifest.ImageHeight;
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue,
                    maxY = float.MinValue;
                for (int v = 0; v < world.Length; v += 2)
                {
                    minX = MathF.Min(minX, world[v]);
                    maxX = MathF.Max(maxX, world[v]);
                    minY = MathF.Min(minY, imgH - world[v + 1]);
                    maxY = MathF.Max(maxY, imgH - world[v + 1]);
                }

                // Hull hugs the silhouette: never wider than the crop, at least half its span.
                Assert.True(minX >= entry.X - 2.5f && maxX <= entry.X + entry.Width + 2.5f,
                    $"{entry.Name} bbox {minX:F1}..{maxX:F1} vs crop {entry.X}..{entry.X + entry.Width}");
                Assert.True(maxX - minX >= entry.Width * 0.5f && maxY - minY >= entry.Height * 0.5f,
                    $"{entry.Name} hull collapsed?");

                // Region-relative normalized 0..1 (y top-down) — what MeshAttachment.ComputeUVs
                // scales by the region rect. Pixel-space UVs here sample far outside the page.
                for (int u = 0; u < mesh.regionUVs.Length; u += 2)
                {
                    Assert.InRange(mesh.regionUVs[u], 0f, 1f);
                    Assert.InRange(mesh.regionUVs[u + 1], 0f, 1f);
                }
            }
            Assert.Equal(skinned.MeshedParts, meshesSeen);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void MeshUVs_AreRegionNormalized_AndFollowContourPixels()
    {
        // 20x20 crop whose silhouette is a 10x10 square at (4..13, 5..14); atlas region at a
        // non-zero page offset — UVs must stay region-relative, not page-absolute pixels.
        const int w = 20, h = 20;
        byte[] rgba = MaskSquare(w, h, 4, 5, 13, 14);
        var part = new SlicePartEntry { Name = "part_000", X = 100, Y = 200, Width = w, Height = h, PixelCount = 100, Layer = 0 };
        var atlas = new SliceAtlasPlacement { X = 1484, Y = 0, Width = w, Height = h };
        var binding = new RigPartBinding { Part = part.Name, Role = RigRole.Torso, Bone = "hips" };
        var anchor = new RigBoneInfo { Name = "hips", ImageX = 110, ImageY = 250 };

        SpineAttachment att = Skinning.BuildMeshAttachment(
            part, atlas, rgba, binding, anchor, new SkinParams(), out SkinPartResult result);

        Assert.True(result.Meshed, result.Reason);
        float[] uvs = att.Uvs!;
        float[] verts = att.Vertices!.Values!;

        Assert.Equal(uvs.Length, verts.Length);
        for (int i = 0; i < uvs.Length; i += 2)
        {
            // Invert the bone-local frame back to crop-local px/py and require the exact law.
            float imgX = verts[i] + anchor.ImageX;
            float imgY = anchor.ImageY - verts[i + 1];
            float px = imgX - part.X;
            float py = imgY - part.Y;
            Assert.Equal(px / atlas.Width, uvs[i], 3);
            Assert.Equal(py / atlas.Height, uvs[i + 1], 3);
            Assert.InRange(uvs[i], 0f, 1f);
            Assert.InRange(uvs[i + 1], 0f, 1f);
        }
    }

    private static double SumTriangleArea(IReadOnlyList<(float x, float y)> poly, List<int> tris)
    {
        double sum = 0;
        for (int t = 0; t < tris.Count; t += 3)
        {
            (float ax, float ay) = poly[tris[t]];
            (float bx, float by) = poly[tris[t + 1]];
            (float cx, float cy) = poly[tris[t + 2]];
            sum += Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) / 2.0;
        }
        return sum;
    }

    private static string NewTempProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-spinegen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
