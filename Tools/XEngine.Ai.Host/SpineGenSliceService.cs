// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// SpineGen stage 1 — slice a character illustration into layered parts and pack the atlas page.
/// Deterministic and offline (no provider calls): identical input bytes + options produce an
/// identical output directory, addressed by content hash so re-runs are idempotent.
/// </summary>
internal sealed class SpineGenSliceService
{
    internal const string AlgorithmVersion = "spinegen-slice-v5-source-raster-joints-seams";
    private static readonly JsonSerializerOptions ManifestWriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ProjectDescriptor _project;

    public SpineGenSliceService(ProjectDescriptor project)
    {
        _project = project;
    }

    public SpineGenSliceResult Slice(
        string image,
        int? alphaThreshold,
        int? minAreaPixels,
        int? cropPadding,
        int? packingPadding,
        string? split,
        int? markerMinDepth,
        int? markerSpacing,
        bool dryRun,
        string? recipe = null)
    {
        string sourcePath = ResolvePath(image);
        byte[] pngBytes = File.ReadAllBytes(sourcePath);
        byte[] rgba = PngRgba.Decode(pngBytes, out int width, out int height);
        byte[]? recipeBytes = recipe == null ? null : File.ReadAllBytes(ResolvePath(recipe));
        var assembly = recipeBytes == null ? null : JsonSerializer.Deserialize<XEngine.Ai.SpineGen.AssemblyRecipe>(recipeBytes)
            ?? throw new ArgumentException("Recipe is null.");
        assembly?.Validate(width, height);
        string? recipeHash = recipeBytes == null ? null : Convert.ToHexString(SHA256.HashData(recipeBytes)).ToLowerInvariant();
        var images=new Dictionary<string,XEngine.Ai.SpineGen.AssemblyImage>(StringComparer.Ordinal);
        var sourceHashes=new SortedDictionary<string,string>(StringComparer.Ordinal);
        if(assembly!=null)foreach(var p in assembly.Parts)
        {
            if(!p.Enabled || p.Image==null)continue;
            byte[] bytes=File.ReadAllBytes(ResolvePath(p.Image));
            sourceHashes.Add(p.Name,Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            byte[] pixels=PngRgba.Decode(bytes,out int sw,out int sh);
            images.Add(p.Name,new(SpineGenRaster.Resize(pixels,sw,sh,p.ImageWidth,p.ImageHeight),p.ImageWidth,p.ImageHeight));
        }

        var splitMode = ParseSplit(split);
        var options = new XEngine.Ai.SpineGen.SliceOptions
        {
            AlphaThreshold = alphaThreshold ?? 8,
            MinAreaPixels = minAreaPixels ?? 48,
            CropPadding = cropPadding ?? 0,
            Split = splitMode,
            MarkerMinDepth = Math.Max(0, markerMinDepth ?? 0),
            MarkerSpacing = Math.Max(0, markerSpacing ?? 0),
        };
        int pagePad = packingPadding ?? 2;
        if(pagePad<0 || pagePad>XEngine.Ai.SpineGen.AtlasPacker.MaxPage/2)
            throw new ArgumentOutOfRangeException(nameof(packingPadding));

        string hash = HashInput(pngBytes, width, height, options, pagePad);
        if (recipeHash != null) hash += "-" + recipeHash[..12];
        if(sourceHashes.Count>0)hash+="-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sourceHashes)))).ToLowerInvariant()[..12];
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string relativeOutDir = Path.Combine(".xengine", "spinegen", "slice", $"{stem}-{hash}");
        string outDir = Path.Combine(_project.ProjectRoot, relativeOutDir);

        if (dryRun)
        {
            return new SpineGenSliceResult
            {
                InputHash = hash,
                OutputDirectory = relativeOutDir.Replace('\\', '/'),
                DryRun = true,
            };
        }

        XEngine.Ai.SpineGen.SliceRunResult run =
            assembly?.Slice(rgba, width, height,images) ?? XEngine.Ai.SpineGen.Slicer.Slice(rgba, width, height, options);

        // The packer owns all four padding edges; content coordinates exclude padding.
        var requests = run.Parts
            .Select(p => new XEngine.Ai.SpineGen.PackRequest { Index = p.LayerIndex, Width = p.Width, Height = p.Height })
            .ToList();
        XEngine.Ai.SpineGen.PackResult pack = XEngine.Ai.SpineGen.AtlasPacker.Pack(requests, pagePad);

        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "parts"));

        var manifest = new XEngine.Ai.SpineGen.SliceManifest
        {
            AlgorithmVersion = AlgorithmVersion,
            Recipe = assembly,
            RecipeHash = recipeHash,
            SourceHashes = sourceHashes,
            Source = sourcePath,
            ImageWidth = width,
            ImageHeight = height,
            AlphaThreshold = options.AlphaThreshold,
            Split = run.Split.ToString(),
            MarkerMinDepth = run.MarkerMinDepth,
            MarkerSpacing = run.MarkerSpacing,
            OpaqueRatio = run.OpaqueRatio,
            DiscardedNoiseParts = run.DiscardedNoiseParts,
            Page = new XEngine.Ai.SpineGen.SlicePageInfo
            {
                Name = $"{stem}.png",
                Width = pack.PageWidth,
                Height = pack.PageHeight,
                Padding = pagePad,
            },
        };

        using (var atlasWriter = new StreamWriter(Path.Combine(outDir, $"{stem}.atlas.txt")))
        {
            var regions = new List<XEngine.Ai.SpineGen.SpineAtlasRegion>();
            foreach (XEngine.Ai.SpineGen.SlicePartImage part in run.Parts)
            {
                string partPngRelative = $"parts/{part.Name}.png";
                File.WriteAllBytes(
                    Path.Combine(outDir, "parts", $"{part.Name}.png"),
                    PngRgba.Encode(part.Rgba, part.Width, part.Height));

                // Persist the inpaint seed ring as a PNG mask (alpha channel) for the fill stage.
                var seedRgba = new byte[part.Width * part.Height * 4];
                for (int m = 0; m < part.Width * part.Height; m++)
                {
                    if (part.InpaintSeedMask != null && part.InpaintSeedMask[m] != 0)
                    {
                        seedRgba[m * 4] = 255;
                        seedRgba[m * 4 + 1] = 255;
                        seedRgba[m * 4 + 2] = 255;
                        seedRgba[m * 4 + 3] = 255;
                    }
                }

                string seedPngRelative = $"parts/{part.Name}.seed.png";
                File.WriteAllBytes(
                    Path.Combine(outDir, "parts", $"{part.Name}.seed.png"),
                    PngRgba.Encode(seedRgba, part.Width, part.Height));

                XEngine.Ai.SpineGen.PackPlacement at = pack.Placements[part.LayerIndex];
                regions.Add(new XEngine.Ai.SpineGen.SpineAtlasRegion
                {
                    Name = part.Name,
                    X = at.X,
                    Y = at.Y,
                    Width = part.Width,
                    Height = part.Height,
                });

                manifest.Parts.Add(new XEngine.Ai.SpineGen.SlicePartEntry
                {
                    Name = part.Name,
                    SourceIsland = part.SourceIsland,
                    X = part.X,
                    Y = part.Y,
                    Width = part.Width,
                    Height = part.Height,
                    PixelCount = part.PixelCount,
                    InpaintSeedPixels = part.InpaintSeedPixels,
                    InpaintSeedRect = part.InpaintSeedRect,
                    Layer = part.LayerIndex,
                    Png = partPngRelative,
                    SeedPng = seedPngRelative,
                    Atlas = new XEngine.Ai.SpineGen.SliceAtlasPlacement
                    {
                        X = at.X,
                        Y = at.Y,
                        Width = part.Width,
                        Height = part.Height,
                    },
                });
            }

            XEngine.Ai.SpineGen.SpineAtlasWriter.Write(atlasWriter, manifest.Page.Name, pack.PageWidth,
                pack.PageHeight, regions, premultipliedAlpha: true);
        }

        SpineGenAtlasPage.Write(outDir,manifest);
        string manifestPath = Path.Combine(outDir, "slice_manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestWriteOptions));

        return new SpineGenSliceResult
        {
            InputHash = hash,
            OutputDirectory = relativeOutDir.Replace('\\', '/'),
            Manifest = relativeOutDir.Replace('\\', '/') + "/slice_manifest.json",
            Atlas = relativeOutDir.Replace('\\', '/') + $"/{stem}.atlas.txt",
            PagePng = relativeOutDir.Replace('\\', '/') + $"/{stem}.png",
            ImageWidth = width,
            ImageHeight = height,
            OpaqueRatio = run.OpaqueRatio,
            DiscardedNoiseParts = run.DiscardedNoiseParts,
            PartCount = manifest.Parts.Count,
            TotalInpaintSeedPixels = manifest.Parts.Sum(p => p.InpaintSeedPixels),
            PageWidth = pack.PageWidth,
            PageHeight = pack.PageHeight,
            DryRun = false,
        };
    }

    private static XEngine.Ai.SpineGen.SliceSplitMode ParseSplit(string? split)
    {
        switch (split?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "components":
            case "connectedcomponents":
                return XEngine.Ai.SpineGen.SliceSplitMode.ConnectedComponents;
            case "watershed":
            case "distancewatershed":
                return XEngine.Ai.SpineGen.SliceSplitMode.DistanceWatershed;
            default:
                throw new ArgumentException(
                    $"Unknown --split '{split}'. Expected components | watershed.");
        }
    }

    private string ResolvePath(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
            throw new ArgumentException("--image is required.");
        if (Path.IsPathRooted(image) && File.Exists(image))
            return image;

        foreach (string candidate in new[]
                 {
                     Path.Combine(_project.ProjectRoot, image),
                     Path.Combine(_project.ProjectRoot, "Assets", image),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Image not found: {image}");
    }

    private static string HashInput(byte[] pngBytes, int width, int height,
        XEngine.Ai.SpineGen.SliceOptions options, int pagePad)
    {
        // Deterministic content address: source bytes + every knob that changes the output.
        byte[][] chunks =
        [
            System.Text.Encoding.UTF8.GetBytes(AlgorithmVersion),
            BitConverter.GetBytes(width),
            BitConverter.GetBytes(height),
            BitConverter.GetBytes(options.AlphaThreshold),
            BitConverter.GetBytes(options.MinAreaPixels),
            BitConverter.GetBytes(options.CropPadding),
            BitConverter.GetBytes((int)options.Split),
            BitConverter.GetBytes(options.MarkerMinDepth),
            BitConverter.GetBytes(options.MarkerSpacing),
            BitConverter.GetBytes(pagePad),
            pngBytes,
        ];
        int total = 0;
        foreach (byte[] chunk in chunks)
            total += chunk.Length;
        var all = new byte[total];
        int offset = 0;
        foreach (byte[] chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, all, offset, chunk.Length);
            offset += chunk.Length;
        }

        return Convert.ToHexString(SHA256.HashData(all)).ToLowerInvariant()[..8];
    }


}

public sealed class SpineGenSliceResult
{
    [JsonPropertyName("inputHash")] public string InputHash { get; set; } = "";
    [JsonPropertyName("outputDirectory")] public string OutputDirectory { get; set; } = "";
    [JsonPropertyName("manifest")] public string? Manifest { get; set; }
    [JsonPropertyName("atlas")] public string? Atlas { get; set; }
    [JsonPropertyName("pagePng")] public string? PagePng { get; set; }
    [JsonPropertyName("imageWidth")] public int ImageWidth { get; set; }
    [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }
    [JsonPropertyName("opaqueRatio")] public float OpaqueRatio { get; set; }
    [JsonPropertyName("discardedNoiseParts")] public int DiscardedNoiseParts { get; set; }
    [JsonPropertyName("partCount")] public int PartCount { get; set; }
    /// <summary>Sum across parts — the fill workload awaiting M3's inpaint stage.</summary>
    [JsonPropertyName("totalInpaintSeedPixels")] public int TotalInpaintSeedPixels { get; set; }
    [JsonPropertyName("pageWidth")] public int PageWidth { get; set; }
    [JsonPropertyName("pageHeight")] public int PageHeight { get; set; }
    [JsonPropertyName("dryRun")] public bool DryRun { get; set; }
}
