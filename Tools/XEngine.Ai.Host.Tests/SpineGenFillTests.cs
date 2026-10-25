// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using XEngine.Ai.Gateway;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineGenFillTests
{
    private const int W = 32;
    private const int H = 32;

    /// <summary>32x32 crop: opaque square at (8..23, 12..27), 3px seed ring hugging its right edge
    /// — "42 seed pixels" of cut continuation for every strategy to complete.</summary>
    private static byte[] BuildPart(out byte[] seedMask)
    {
        var rgba = new byte[W * H * 4];
        var seeds = new byte[W * H];
        for (int y = 12; y < 28; y++)
            for (int x = 8; x < 24; x++)
                Put(rgba, x, y, 180, 120, 60);

        for (int y = 13; y < 27; y++)
            for (int x = 24; x < 27; x++)
            {
                Put(rgba, x, y, 0, 0, 0);
                rgba[(y * W + x) * 4 + 3] = 0;
                seeds[y * W + x] = 255;
            }

        seedMask = seeds;
        return rgba;
    }

    private static void Put(byte[] rgba, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * W + x) * 4;
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
        rgba[i + 3] = 255;
    }

    private static void PaintCheckerboard(byte[] rgba, byte[] seeds)
    {
        for (int i = 0; i < W * H; i++)
        {
            if (seeds[i] == 0)
                continue;
            bool odd = ((i % W) + (i / W)) % 2 == 0;
            Put(rgba, i % W, i / W, (byte)(odd ? 255 : 10), (byte)(odd ? 5 : 240), 128);
        }
    }

    [Fact]
    public void MockStrategies_FillEverySeed_AndAreDeterministic()
    {
        byte[] original = BuildPart(out byte[] seeds);
        foreach (FillStrategy strategy in PartFiller.MockStrategies)
        {
            byte[] first = PartFiller.Fill(original, W, H, seeds, strategy);
            byte[] second = PartFiller.Fill(original, W, H, seeds, strategy);
            Assert.Equal(first, second); // deterministic

            int filledSeeds = 0;
            for (int i = 0; i < W * H; i++)
            {
                if (seeds[i] != 0 && first[i * 4 + 3] != 0)
                    filledSeeds++;
                if (seeds[i] == 0)
                {
                    // Original untouched pixels stay bit-exact.
                    Assert.Equal(original.Skip(i * 4).Take(4), first.Skip(i * 4).Take(4));
                }
            }
            Assert.Equal(14 * 3, filledSeeds);
        }
    }

    [Fact]
    public void SeamScore_RanksSmoothContinuationAboveCheckerboard()
    {
        byte[] original = BuildPart(out byte[] seeds);

        byte[] smooth = PartFiller.Fill(original, W, H, seeds, FillStrategy.Nearest);
        double smoothScore = PartFiller.SeamScore(smooth, W, H, seeds);

        byte[] garbage = PartFiller.Fill(original, W, H, seeds, FillStrategy.Nearest);
        PaintCheckerboard(garbage, seeds);
        double garbageScore = PartFiller.SeamScore(garbage, W, H, seeds);

        Assert.True(garbageScore > smoothScore,
            $"checkerboard ({garbageScore:F1}) must score worse than smooth ({smoothScore:F1})");
    }

    [Fact]
    public void RowStretch_DeGenerateRingsFallBackVerticalOrStayTransparent()
    {
        // Watershed-cut part: opaque slab at the TOP of the crop, seed ring along the BOTTOM
        // edge spanning the full crop width — no opaque pixel left/right on the seed rows.
        var rgba = new byte[W * H * 4];
        var seeds = new byte[W * H];
        for (int x = 4; x < 28; x++)
            for (int y = 4; y < 12; y++)
                Put(rgba, x, y, 90, 200, 80);
        for (int x = 0; x < W; x++)
        {
            seeds[12 * W + x] = 255; // row 12 hugs the slab's bottom edge — full-width run
            seeds[31 * W + x] = 255; // row 31 (last): no opaque flank anywhere on the row
        }

        byte[] filled = PartFiller.Fill(rgba, W, H, seeds, FillStrategy.RowStretch);

        for (int x = 0; x < W; x++)
        {
            // Row 12 has no horizontal flank but stretches vertically from the slab's color.
            Assert.Equal(90, filled[(12 * W + x) * 4]);
            Assert.Equal(255, filled[(12 * W + x) * 4 + 3]);
            // Row 31 has no donor in any direction — stays transparent instead of throwing.
            Assert.Equal(0, filled[(31 * W + x) * 4 + 3]);
        }
    }

    [Fact]
    public async Task FillService_MockOnly_ProducesGalleryAndCompositesWinner()
    {
        string root = NewTempProject();
        try
        {
            string sliceRel = Path.Combine(".xengine", "spinegen", "slice", "part");
            string sliceDir = Path.Combine(root, sliceRel);
            Directory.CreateDirectory(Path.Combine(sliceDir, "parts"));

            byte[] rgba = BuildPart(out byte[] seeds);
            var manifest = new SliceManifest
            {
                Source = "Assets/part.png",
                ImageWidth = W,
                ImageHeight = H,
                Page = new(){Name="part.png",Width=64,Height=64,Padding=2},
                Parts =
                [
                    new SlicePartEntry
                    {
                        Name = "part_000",
                        Width = W,
                        Height = H,
                        PixelCount = 14 * 16,
                        InpaintSeedPixels = seeds.Count(s => s != 0),
                        InpaintSeedRect = new SliceRect { X = 24, Y = 13, Width = 3, Height = 14 },
                        Png = "parts/part_000.png",
                        SeedPng = "parts/part_000.seed.png",
                        Atlas = new(){X=2,Y=2,Width=W,Height=H},
                    },
                ],
            };
            File.WriteAllText(Path.Combine(sliceDir, "slice_manifest.json"),
                JsonSerializer.Serialize(manifest));
            File.WriteAllBytes(Path.Combine(sliceDir, "parts", "part_000.png"), PngRgba.Encode(rgba, W, H));
            File.WriteAllBytes(Path.Combine(sliceDir, "parts", "part_000.seed.png"),
                PngRgba.Encode(SeedRgba(seeds), W, H));

            var project = new ProjectDescriptor("test", root, null, null, null, "fp");
            var registry = new GenProviderRegistry();
            var service = new SpineGenFillService(project, registry);

            SpineGenFillResult result = await service.FillAsync(sliceRel, providerName: "mock",
                CancellationToken.None);

            Assert.Equal(1, result.PartsFilled);

            var gallery = JsonSerializer.Deserialize<FillManifest>(File.ReadAllText(
                Path.Combine(root, result.Gallery.Replace('/', Path.DirectorySeparatorChar))))!;
            FillPartReport part = gallery.Parts.Single();
            Assert.Equal("filled", part.Status);
            Assert.True(part.Candidates.Count >= 3, "three mock candidates expected");
            Assert.NotNull(part.Chosen);
            Assert.Equal(part.Chosen!.SeamScore, part.Candidates.Min(c => c.SeamScore), 6);

            byte[] updated = PngRgba.Decode(File.ReadAllBytes(Path.Combine(sliceDir, "parts", "part_000.png")),
                out _, out _);
            for (int i = 0; i < W * H; i++)
                if (seeds[i] != 0)
                    Assert.NotEqual(0, updated[i * 4 + 3]);

            byte[] page=PngRgba.Decode(File.ReadAllBytes(Path.Combine(sliceDir,"part.png")),out int pw,out _);
            for(int y=0;y<H;y++)for(int x=0;x<W;x++)
            {
                int s=(y*W+x)*4,d=((y+2)*pw+x+2)*4,a=updated[s+3];
                Assert.Equal(a,page[d+3]);
                for(int c=0;c<3;c++)Assert.Equal((updated[s+c]*a+127)/255,page[d+c]);
            }

            Assert.True(File.Exists(Path.Combine(sliceDir, "parts", "part_000.orig.png")),
                "first fill run keeps an untouched backup");

            // Pre-v2 manifests described the padded outer rectangle, not content.
            // Reject them before changing the existing successful part or gallery.
            byte[] successfulPart=File.ReadAllBytes(Path.Combine(sliceDir,"parts/part_000.png"));
            byte[] successfulGallery=File.ReadAllBytes(Path.Combine(sliceDir,"fill_manifest.json"));
            manifest.Parts[0].Atlas.Width+=4;
            File.WriteAllText(Path.Combine(sliceDir,"slice_manifest.json"),JsonSerializer.Serialize(manifest));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.FillAsync(sliceRel,"mock",CancellationToken.None));
            Assert.Equal(successfulPart,File.ReadAllBytes(Path.Combine(sliceDir,"parts/part_000.png")));
            Assert.Equal(successfulGallery,File.ReadAllBytes(Path.Combine(sliceDir,"fill_manifest.json")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static byte[] SeedRgba(byte[] mask)
    {
        var rgba = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
            if (mask[i] != 0)
            {
                rgba[i * 4] = 255;
                rgba[i * 4 + 1] = 255;
                rgba[i * 4 + 2] = 255;
                rgba[i * 4 + 3] = 255;
            }
        return rgba;
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
