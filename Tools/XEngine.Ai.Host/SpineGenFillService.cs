// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using XEngine.Ai.Gateway;
using XEngine.Ai.SpineGen;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// SpineGen stage 2 — fill the inpaint seeds each sliced part carries. Every part gets a candidate
/// gallery: three deterministic offline strategies plus (when a real image provider is selected)
/// one generative completion via the same <see cref="IGenProvider"/> stack the gen tools use. The
/// lowest seam score wins and is composited into <c>parts/&lt;name&gt;.png</c>; the gallery is kept
/// on disk for human review.
/// </summary>
internal sealed class SpineGenFillService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Provider calls can take minutes — bounded so CLI stays script-friendly.</summary>
    private static readonly TimeSpan MaxProviderWait = TimeSpan.FromMinutes(5);

    private readonly ProjectDescriptor _project;
    private readonly Func<string, IGenProvider?> _providerResolver;

    public SpineGenFillService(ProjectDescriptor project, GenProviderRegistry registry)
        : this(project, name =>
        {
            if (!registry.TryGet(name, out IGenProvider? found) || found == null)
                return null;
            return found;
        })
    {
    }

    internal SpineGenFillService(ProjectDescriptor project, Func<string, IGenProvider?> providerResolver)
    {
        _project = project;
        _providerResolver = providerResolver;
    }

    public async Task<SpineGenFillResult> FillAsync(
        string sliceDir,
        string? providerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sliceDir))
            throw new ArgumentException("--slice is required (slice output directory).");

        sliceDir = sliceDir.Replace('/', Path.DirectorySeparatorChar);
        string absSliceDir = Path.IsPathRooted(sliceDir)
            ? sliceDir
            : Path.Combine(_project.ProjectRoot, sliceDir);
        string manifestPath = Path.Combine(absSliceDir, "slice_manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"slice_manifest.json not found under {absSliceDir}.");

        SliceManifest slice = JsonSerializer.Deserialize<SliceManifest>(
            File.ReadAllText(manifestPath)) ?? throw new InvalidOperationException("Unreadable slice manifest.");

        // Fail before gallery, backups or any part mutation on obsolete padded layouts.
        SpineGenAtlasPage.Validate(absSliceDir,slice);

        providerName ??= "mock";
        IGenProvider? provider = null;
        if (providerName != "mock")
        {
            provider = _providerResolver(providerName)
                       ?? throw new InvalidOperationException(
                           $"Provider '{providerName}' is not registered (check {providerName.ToUpperInvariant()}_API_KEY / .xengine/secrets.json).");
        }

        var report = new FillManifest
        {
            SliceManifest = RelativeToProject(manifestPath),
            Provider = providerName,
        };
        int filledParts = 0;
        int filledPixels = 0;

        foreach (SlicePartEntry part in slice.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FillPartReport partReport = await FillPartAsync(absSliceDir, part, providerName, provider,
                cancellationToken).ConfigureAwait(false);
            report.Parts.Add(partReport);
            if (partReport.Status == "filled")
            {
                filledParts++;
                filledPixels += partReport.SeedPixels;
            }
        }

        if(filledParts>0) SpineGenAtlasPage.Write(absSliceDir,slice);
        File.WriteAllText(Path.Combine(absSliceDir, "fill_manifest.json"),
            JsonSerializer.Serialize(report, JsonOpts));

        return new SpineGenFillResult
        {
            SliceDirectory = RelativeToProject(absSliceDir),
            Provider = providerName,
            PartsFilled = filledParts,
            PixelsFilled = filledPixels,
            Gallery = RelativeToProject(Path.Combine(absSliceDir, "fill_manifest.json")),
        };
    }

    private async Task<FillPartReport> FillPartAsync(string sliceDir, SlicePartEntry part,
        string providerName, IGenProvider? provider, CancellationToken ct)
    {
        var report = new FillPartReport
        {
            Name = part.Name,
            SeedPixels = part.InpaintSeedPixels,
        };
        if (part.InpaintSeedPixels == 0 || string.IsNullOrEmpty(part.SeedPng))
            return report; // nothing to fill

        string partPngPath = Path.Combine(sliceDir, part.Png.Replace('/', Path.DirectorySeparatorChar));
        byte[] originalRgba = PngRgba.Decode(File.ReadAllBytes(partPngPath), out int w, out int h);

        // The PNG decoder preserves alpha but our masks encode "white+alpha"; rebuild from alpha.
        var seedMask = new byte[w * h];
        using (var seedReader = new MemoryStream(File.ReadAllBytes(Path.Combine(sliceDir,
                   part.SeedPng.Replace('/', Path.DirectorySeparatorChar)))))
        {
            byte[] seedRgba = PngRgba.Decode(seedReader.ToArray(), out int sw, out int sh);
            if (sw != w || sh != h)
                throw new InvalidOperationException($"Seed mask size mismatch for {part.Name}.");
            for (int i = 0; i < w * h; i++)
                seedMask[i] = seedRgba[i * 4 + 3];
        }

        // First run keeps an untouched backup next to the live part png.
        string origPath = Path.Combine(sliceDir, "parts", $"{part.Name}.orig.png");
        if (!File.Exists(origPath))
            File.WriteAllBytes(origPath, PngRgba.Encode(originalRgba, w, h));

        Directory.CreateDirectory(Path.Combine(sliceDir, "fill"));
        string fillDir = Path.Combine(sliceDir, "fill", part.Name);
        Directory.CreateDirectory(fillDir);

        foreach (XEngine.Ai.SpineGen.FillStrategy strategy in XEngine.Ai.SpineGen.PartFiller.MockStrategies)
        {
            byte[] candidate = XEngine.Ai.SpineGen.PartFiller.Fill(originalRgba, w, h, seedMask, strategy);
            double score = XEngine.Ai.SpineGen.PartFiller.SeamScore(candidate, w, h, seedMask);
            string file = Path.Combine("fill", part.Name, $"{strategy}.png".ToLowerInvariant());
            File.WriteAllBytes(Path.Combine(sliceDir, file), PngRgba.Encode(candidate, w, h));
            report.Candidates.Add(new FillCandidate
            {
                Source = $"mock:{strategy}",
                Png = file.Replace('\\', '/'),
                SeamScore = Math.Round(score, 3),
            });
        }

        if (provider != null)
        {
            try
            {
                FillCandidate generated = await GenerateViaProviderAsync(sliceDir, part, providerName!, provider,
                    originalRgba, seedMask, w, h, ct).ConfigureAwait(false);
                report.Candidates.Add(generated);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.Error = $"provider '{providerName}' failed: {ex.Message}";
            }
        }

        FillCandidate? best = report.Candidates.Count == 0
            ? null
            : report.Candidates.OrderBy(c => c.SeamScore).First();
        if (best != null && !string.Equals(best.Source, "none", StringComparison.Ordinal))
        {
            byte[] chosenRgba;
            if (best.Source.StartsWith("mock:", StringComparison.Ordinal))
            {
                chosenRgba = XEngine.Ai.SpineGen.PartFiller.Fill(originalRgba, w, h, seedMask,
                    Enum.Parse<XEngine.Ai.SpineGen.FillStrategy>(best.Source["mock:".Length..], ignoreCase: true));
            }
            else
            {
                string path = Path.Combine(sliceDir, best.Png.Replace('/', Path.DirectorySeparatorChar));
                chosenRgba = PngRgba.Decode(File.ReadAllBytes(path), out _, out _);
            }

            File.WriteAllBytes(partPngPath, PngRgba.Encode(chosenRgba, w, h));
            report.Chosen = best;
            report.Status = "filled";
        }

        return report;
    }

    /// <summary>Asks the image model to continue the painted silhouette into the transparent seed
    /// zone of the part crop, then pastes ONLY the returned seed pixels back. The input is the
    /// part-local crop — cross-part surroundings are a later refinement once seams need them.</summary>
    private async Task<FillCandidate> GenerateViaProviderAsync(string sliceDir, SlicePartEntry part,
        string providerName, IGenProvider provider, byte[] originalRgba, byte[] seedMask, int w, int h,
        CancellationToken ct)
    {
        string taskIdHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(originalRgba)[..8]).ToLowerInvariant();
        string inputRel = Path.Combine(".xengine", "spinegen", "fill-inputs", $"{part.Name}-{taskIdHash}.png")
            .Replace(Path.DirectorySeparatorChar, '/');
        Directory.CreateDirectory(Path.Combine(_project.ProjectRoot, ".xengine", "spinegen", "fill-inputs"));
        File.WriteAllBytes(Path.Combine(_project.ProjectRoot, inputRel), PngRgba.Encode(originalRgba, w, h));

        var recipe = new GenRecipe(
            Provider: providerName,
            Kind: GenRecipe.Kinds.Texture,
            Prompt:
                "Complete the cut-out sprite body part: extend the existing colored silhouette into the fully " +
                "transparent pixels at its cut edge so the shape continues naturally underneath. Keep the exact " +
                "art style, palette and edge crispness; do not move or redraw any existing pixel.",
            Extra: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["inputImagePath"] = inputRel,
                ["purpose"] = "spinegen-fill",
            });

        GenSubmitResult submitted = await provider.SubmitAsync(recipe, ct).ConfigureAwait(false);
        GenTaskStatus status = await WaitUntilDoneAsync(provider, submitted.TaskId, ct).ConfigureAwait(false);
        if (status.State != GenTaskState.Succeeded)
            throw new InvalidOperationException($"task ended as '{status.State}' ({status.Message}).");

        string downloadDir = Path.Combine(_project.ProjectRoot, ".xengine", "gen-downloads", submitted.TaskId);
        IReadOnlyList<GenArtifact> artifacts =
            await provider.FetchAsync(submitted.TaskId, recipe, downloadDir, ct).ConfigureAwait(false);
        GenArtifact pngArtifact = artifacts.First(a =>
            a.LocalPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));

        // Returned image may be a different resolution — resize to the exact part rect.
        byte[] returnedRgba = PngRgba.Decode(File.ReadAllBytes(pngArtifact.LocalPath), out int rw, out int rh);
        if (rw != w || rh != h)
            returnedRgba = PngRgba.Resize(returnedRgba, rw, rh, w, h);

        byte[] composited = (byte[])originalRgba.Clone();
        for (int i = 0; i < w * h; i++)
        {
            if (seedMask[i] == 0)
                continue;
            Buffer.BlockCopy(returnedRgba, i * 4, composited, i * 4, 4);
            composited[i * 4 + 3] = 255;
        }

        string file = Path.Combine("fill", part.Name, "generated.png");
        File.WriteAllBytes(Path.Combine(sliceDir, file), PngRgba.Encode(composited, w, h));

        return new FillCandidate
        {
            Source = $"{providerName}:{recipe.Model ?? "default"}",
            Png = file.Replace(Path.DirectorySeparatorChar, '/'),
            SeamScore = Math.Round(XEngine.Ai.SpineGen.PartFiller.SeamScore(composited, w, h, seedMask), 3),
        };
    }

    private async Task<GenTaskStatus> WaitUntilDoneAsync(IGenProvider provider, string taskId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + MaxProviderWait;
        while (DateTime.UtcNow < deadline)
        {
            GenTaskStatus status = await provider.PollAsync(taskId, ct).ConfigureAwait(false);
            if (status.State is GenTaskState.Succeeded or GenTaskState.Failed)
                return status;
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"Generation task '{taskId}' did not finish within {MaxProviderWait.TotalMinutes} minutes.");
    }

    private string RelativeToProject(string absolutePath) =>
        Path.GetRelativePath(_project.ProjectRoot, absolutePath).Replace('\\', '/');
}

public sealed class SpineGenFillResult
{
    [JsonPropertyName("sliceDirectory")] public string SliceDirectory { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("partsFilled")] public int PartsFilled { get; set; }
    [JsonPropertyName("pixelsFilled")] public int PixelsFilled { get; set; }
    [JsonPropertyName("gallery")] public string Gallery { get; set; } = "";
}
