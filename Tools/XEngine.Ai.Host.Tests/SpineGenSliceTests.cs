// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using SpineRT = global::Spine;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// M2 slicing acceptance on synthetic art. The bear fixture geometry is deliberately spaced by
/// SeedReach-sized gaps: parts stay separate connected components while still being close enough
/// to generate inpaint seeds against each other.
/// </summary>
public sealed class SpineGenSliceTests
{
    internal const int W = 96;
    internal const int H = 96;

    /// <summary>Synthetic character: torso ellipse, head circle, two detached arms, eye dots merged
    /// into the head component.</summary>
    public static byte[] BuildBearRgba(out List<(string name, int x, int y, int w, int h)> shapes)
    {
        var rgba = new byte[W * H * 4];
        var recorded = new List<(string, int, int, int, int)>();
        shapes = recorded;

        void FillEllipse(string name, int cx, int cy, int rx, int ry, byte r, byte g, byte b)
        {
            for (int y = cy - ry; y <= cy + ry; y++)
            {
                for (int x = cx - rx; x <= cx + rx; x++)
                {
                    if (x < 0 || y < 0 || x >= W || y >= H)
                        continue;
                    float dx = (x - cx) / (float)rx;
                    float dy = (y - cy) / (float)ry;
                    if (dx * dx + dy * dy <= 1f)
                        Put(rgba, W, x, y, r, g, b);
                }
            }
            recorded.Add((name, cx - rx, cy - ry, rx * 2, ry * 2));
        }

        void FillRect(string name, int x0, int y0, int w, int h, byte r, byte g, byte b)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    Put(rgba, W, x, y, r, g, b);
            recorded.Add((name, x0, y0, w, h));
        }

        // Torso (largest, back layer). Head circle sits 2px above its top edge — within SeedReach
        // so inpaint seeds fire, yet the shapes stay distinct connected components. Arms keep a
        // >=5px sideways gap to the torso silhouette.
        FillEllipse("torso", 48, 62, 26, 22, 150, 100, 40);
        FillEllipse("head", 48, 24, 14, 14, 170, 120, 60);
        // Eyes live INSIDE the head's fill (must merge, never become own parts).
        FillRect("eyeL", 43, 18, 3, 3, 30, 30, 30);
        FillRect("eyeR", 52, 18, 3, 3, 30, 30, 30);
        // Arms clear the torso silhouette by >=3 empty columns (the ellipse reaches x=22 at its
        // widest row) — wide enough to avoid 8-connected diagonal bridges, narrow enough that the
        // SeedReach ring between arm and torso still produces fill seeds on the back layer.
        FillRect("armL", 13, 44, 6, 24, 140, 95, 45);
        FillRect("armR", 77, 44, 6, 24, 140, 95, 45);
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
    public void Slicer_SplitsCharacterIntoExpectedPartsAndSeeds()
    {
        byte[] rgba = BuildBearRgba(out _);

        SliceRunResult run = Slicer.Slice(rgba, W, H, new SliceOptions());

        Assert.Equal(4, run.Parts.Count); // torso, head, two arms — eyes merged, noise dropped
        Assert.Equal(0, run.DiscardedNoiseParts);
        Assert.Equal(0, run.Parts[0].LayerIndex);

        SlicePartImage torso = run.Parts[0]; // largest area is always the back layer
        Assert.True(torso.PixelCount > 900);
        Assert.True(run.Parts.IndexOf(torso) == 0, "the big ellipse must be the back layer");

        SlicePartImage head = run.Parts.Single(p => p != torso && p.PixelCount > 400);
        Assert.True(head.InpaintSeedPixels > 0, "head cut against the torso needs fill seeds");
        Assert.True(torso.InpaintSeedPixels > 0, "torso cut against head/arms needs fill seeds");

        foreach (SlicePartImage part in run.Parts)
            Assert.NotEqual(0, part.PixelCount);

        // Arms are the front-most arm layers (smallest areas, drawn last in painter order).
        Assert.True(run.Parts[^1].PixelCount < run.Parts[0].PixelCount / 4,
            "smallest parts belong on top");
    }

    [Fact]
    public void Slicer_DropsTinyNoiseComponents()
    {
        byte[] rgba = BuildBearRgba(out _);
        Put(rgba, W, 4, 4, 255, 255, 255); // 1px speck
        Put(rgba, W, 5, 4, 255, 255, 255);

        SliceRunResult run = Slicer.Slice(rgba, W, H, new SliceOptions());
        Assert.Equal(1, run.DiscardedNoiseParts);
        Assert.Equal(4, run.Parts.Count);
    }

    /// <summary>Two solid rects overlapping into ONE connected component — the classic
    /// mech/armor case connected components cannot separate.</summary>
    private static byte[] BuildOverlappingSlabsRgba(out int opaquePixels)
    {
        const int size = 128;
        var rgba = new byte[size * size * 4];
        opaquePixels = 0;
        void FillRect(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    Put(rgba, size, x, y, 150, 100, 40);
        }
        FillRect(10, 10, 50, 50);
        FillRect(40, 40, 50, 50); // overlaps the first slab: one solid island, saddle through the corner
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0)
                opaquePixels++;
        return rgba;
    }

    [Fact]
    public void Slicer_WatershedSplitsSolidTouchingParts()
    {
        byte[] rgba = BuildOverlappingSlabsRgba(out int opaque);

        SliceRunResult cc = Slicer.Slice(rgba, 128, 128, new SliceOptions());
        Assert.Equal(1, cc.Parts.Count); // one solid island — CC cannot cut it
        Assert.Equal(SliceSplitMode.ConnectedComponents, cc.Split);
        Assert.Equal(0, cc.MarkerMinDepth);

        SliceRunResult ws = Slicer.Slice(rgba, 128, 128, new SliceOptions
        {
            Split = SliceSplitMode.DistanceWatershed,
        });
        Assert.Equal(SliceSplitMode.DistanceWatershed, ws.Split);
        Assert.True(ws.MarkerMinDepth > 0 && ws.MarkerSpacing > 0, "resolved marker tuning must be echoed");
        Assert.Equal(2, ws.Parts.Count); // cut at the saddle through the overlap corner
        Assert.Equal(opaque, ws.Parts.Sum(p => p.PixelCount)); // no pixels lost or duplicated
        Assert.All(ws.Parts, p => Assert.True(p.PixelCount > 900, "both slabs stay sizeable parts"));
    }

    [Fact]
    public void Slicer_WatershedKeepsDetachedShapesAndThinPartsIntact()
    {
        byte[] rgba = BuildBearRgba(out _);

        SliceRunResult cc = Slicer.Slice(rgba, W, H, new SliceOptions());
        SliceRunResult ws = Slicer.Slice(rgba, W, H, new SliceOptions
        {
            Split = SliceSplitMode.DistanceWatershed,
        });

        // Detached bear shapes must survive unchanged; the thin arms stay whole (below the
        // auto marker depth), the head/torso plateaus seed one marker each.
        Assert.Equal(cc.Parts.Count, ws.Parts.Count);
        Assert.Equal(cc.Parts.Sum(p => p.PixelCount), ws.Parts.Sum(p => p.PixelCount));
    }

    [Fact]
    public void Slicer_WatershedDeterministicForIdenticalInput()
    {
        byte[] rgba = BuildOverlappingSlabsRgba(out _);

        SliceRunResult a = Slicer.Slice(rgba, 128, 128, new SliceOptions
        {
            Split = SliceSplitMode.DistanceWatershed,
            MarkerMinDepth = 6,
            MarkerSpacing = 10,
        });
        SliceRunResult b = Slicer.Slice(rgba, 128, 128, new SliceOptions
        {
            Split = SliceSplitMode.DistanceWatershed,
            MarkerMinDepth = 6,
            MarkerSpacing = 10,
        });

        Assert.Equal(a.Parts.Count, b.Parts.Count);
        foreach ((SlicePartImage pa, SlicePartImage pb) in a.Parts.Zip(b.Parts))
        {
            Assert.Equal((pa.X, pa.Y, pa.Width, pa.Height, pa.PixelCount),
                         (pb.X, pb.Y, pb.Width, pb.Height, pb.PixelCount));
            Assert.Equal(pa.Rgba, pb.Rgba);
        }
    }

    [Fact]
    public void AtlasPacker_DeterministicNonOverlappingPowerOfTwoPage()
    {
        var items = new[]
        {
            new PackRequest { Index = 0, Width = 33, Height = 21 },
            new PackRequest { Index = 1, Width = 12, Height = 40 },
            new PackRequest { Index = 2, Width = 10, Height = 10 },
        };

        PackResult result = AtlasPacker.Pack(items, padding: 2);
        PackResult again = AtlasPacker.Pack(items, padding: 2);

        Assert.Equal(result.PageWidth, again.PageWidth);
        Assert.Equal(result.PageHeight, again.PageHeight);
        Assert.Equal(result.Placements.Count, again.Placements.Count);
        for (int i = 0; i < result.Placements.Count; i++)
            Assert.Equal(result.Placements[i], again.Placements[i]);
        Assert.True(AtlasPacker.NextPowerOfTwo(result.PageWidth) == result.PageWidth);
        Assert.True(AtlasPacker.NextPowerOfTwo(result.PageHeight) == result.PageHeight);

        for (int i = 0; i < result.Placements.Count; i++)
        {
            PackPlacement a = result.Placements[i];
            Assert.True(a.Right <= result.PageWidth && a.Bottom <= result.PageHeight);
            for (int j = i + 1; j < result.Placements.Count; j++)
            {
                PackPlacement b = result.Placements[j];
                bool overlap = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
                Assert.False(overlap, $"placement {i} overlaps {j}");
            }
        }
    }

    [Fact]
    public void SliceService_EndToEnd_WritesManifestAtlasAndPagePng()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            byte[] rgba = BuildBearRgba(out _);
            byte[] png = PngRgba.Encode(rgba, W, H);
            File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"), png);

            var project = new ProjectDescriptor(
                ProjectId: "test", ProjectRoot: root, EngineVersion: null, SolutionPath: null,
                GitBranch: null, Fingerprint: "fingerprint");
            var service = new SpineGenSliceService(project);

            SpineGenSliceResult result = service.Slice("Assets/bear.png", null, null, null, null, null, null, null, dryRun: false);
            Assert.False(result.DryRun);
            Assert.Equal(4, result.PartCount);
            Assert.True(result.TotalInpaintSeedPixels > 0);
            Assert.Contains(".xengine/spinegen/slice/bear-", result.OutputDirectory.Replace('\\', '/'));

            string outDir = Path.Combine(root, result.OutputDirectory);

            var manifest = JsonSerializer.Deserialize<SliceManifest>(
                File.ReadAllText(Path.Combine(outDir, "slice_manifest.json")));
            Assert.NotNull(manifest);
            Assert.Equal(W, manifest!.ImageWidth);
            Assert.Equal(H, manifest.ImageHeight);
            Assert.Equal(4, manifest.Parts.Count);
            Assert.All(manifest.Parts, p => Assert.False(p.InpaintSeedRect.IsEmpty == false && p.Width == 0));

            foreach (SlicePartEntry part in manifest.Parts)
                Assert.True(File.Exists(Path.Combine(outDir, part.Png)), part.Png);

            byte[] pageBack = File.ReadAllBytes(Path.Combine(outDir, "bear.png"));
            byte[] pageRgba = PngRgba.Decode(pageBack, out int pageW, out int pageH);
            Assert.Equal(manifest.Page.Width, pageW);
            Assert.Equal(manifest.Page.Height, pageH);
            Assert.Equal(AtlasPacker.NextPowerOfTwo(pageW), pageW);
            Assert.Equal(AtlasPacker.NextPowerOfTwo(pageH), pageH);

            using var atlasReader = new StringReader(File.ReadAllText(Path.Combine(outDir, "bear.atlas.txt")));
            var atlas = new SpineRT.Atlas(atlasReader, "", new XEngine.Spine.SpineNoopTextureLoader());
            Assert.Equal(manifest.Parts.Count, atlas.Regions.Count);
            foreach (SpineRT.AtlasRegion region in atlas.Regions)
            {
                SlicePartEntry? entry = manifest.Parts.FirstOrDefault(p => p.Name == region.name);
                Assert.NotNull(entry);
                Assert.Equal(entry!.Atlas.X, region.x);
                Assert.Equal(entry.Atlas.Y, region.y);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SliceService_SplitWatershed_RecordsManifestAndSplitsSolidArt()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            byte[] rgba = BuildOverlappingSlabsRgba(out _);
            File.WriteAllBytes(Path.Combine(root, "Assets", "slabs.png"), PngRgba.Encode(rgba, 128, 128));
            var project = new ProjectDescriptor(
                ProjectId: "test", ProjectRoot: root, EngineVersion: null, SolutionPath: null,
                GitBranch: null, Fingerprint: "fingerprint");
            var service = new SpineGenSliceService(project);

            SpineGenSliceResult result = service.Slice(
                "Assets/slabs.png", null, null, null, null, "watershed", null, null, dryRun: false);
            Assert.Equal(2, result.PartCount);

            string outDir = Path.Combine(root, result.OutputDirectory);
            var manifest = JsonSerializer.Deserialize<SliceManifest>(
                File.ReadAllText(Path.Combine(outDir, "slice_manifest.json")));
            Assert.NotNull(manifest);
            Assert.Equal("DistanceWatershed", manifest!.Split);
            Assert.True(manifest.MarkerMinDepth > 0 && manifest.MarkerSpacing > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SliceService_DryRun_WritesNothingButHash()
    {
        string root = NewTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            File.WriteAllBytes(Path.Combine(root, "Assets", "bear.png"),
                PngRgba.Encode(BuildBearRgba(out _), W, H));
            var project = new ProjectDescriptor("test", root, null, null, null, "fp");
            var service = new SpineGenSliceService(project);

            SpineGenSliceResult result = service.Slice("Assets/bear.png", null, null, null, null, null, null, null, dryRun: true);
            Assert.True(result.DryRun);
            Assert.False(Directory.Exists(Path.Combine(root, result.OutputDirectory)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>Evidence dump used by the M2 acceptance screenshots — same pipeline the docs use.</summary>
    public static void WriteM2EvidenceFiles(SpineGenSliceResult result, string projectRoot, string evidenceDir)
    {
        Directory.CreateDirectory(evidenceDir);
        string outDir = Path.Combine(projectRoot, result.OutputDirectory);
        File.Copy(Path.Combine(outDir, Path.GetFileName(result.PagePng)!),
            Path.Combine(evidenceDir, "m2-slice-atlas-page.png"), overwrite: true);
        File.WriteAllText(Path.Combine(evidenceDir, "m2-slice-manifest.json"),
            File.ReadAllText(Path.Combine(outDir, "slice_manifest.json")));
    }

    private static string NewTempProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-spinegen-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
