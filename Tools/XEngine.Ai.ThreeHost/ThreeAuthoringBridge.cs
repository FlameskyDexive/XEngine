using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using XEngine.Ai.Protocol;
using XEngine.Ai.ThreeFx;

namespace XEngine.Ai.ThreeHost;

/// <summary>
/// Executes validated authoring operations against the three.js page: parses the <c>.threefx</c>
/// recipe, renders deterministic frames through the page, and produces staged, idempotent
/// exports whose cache identity covers source content, dependency contents, seed, export
/// parameters and tool versions. Runs on the UI thread only (WebView2 affinity). All filesystem
/// writes are clamped below the project root as a second line of defence behind the protocol
/// validator.
/// </summary>
internal sealed class ThreeAuthoringBridge : IDisposable
{
    public const string ExporterVersion = "xengine-three-exporter/0.2";
    public const string PreviewFolder = ".xengine/three/preview";
    private const string StateFileName = ThreeFxExportStateFile.FileName;

    private readonly ThreeHostOptions _options;
    private readonly ThreeHostWindow _window;
    private TaskCompletionSource<bool> _pageReadyTcs = NewPageReadyTcs();

    public ThreeAuthoringBridge(ThreeHostOptions options, ThreeHostWindow window)
    {
        _options = options;
        _window = window;
        _window.PageReady += OnPageReady;
    }

    public async Task<ThreeAuthoringResponse> ExecuteAsync(ThreeAuthoringRequest request)
    {
        List<ThreeAuthoringDiagnostic> diagnostics = [];
        var stopwatch = Stopwatch.StartNew();

        if (!string.Equals(request.ProtocolVersion, ThreeAuthoringProtocol.Version, StringComparison.Ordinal))
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("protocol_version", "error",
                "Unsupported authoring protocol version."));
            return Fail(request, "protocol_version", "Unsupported protocol version.", diagnostics);
        }

        bool operationParsed = Enum.TryParse(request.Operation, ignoreCase: true, out ThreeAuthoringOperation operation);
        if (!operationParsed)
            return Fail(request, "invalid_request", "Unknown operation.", diagnostics);

        // The crash hook runs before any work so the client observes a real process death.
        if (TryGetIntOption(request, "__testCrashHost", out int crash) && crash != 0)
        {
            Console.Error.WriteLine("__testCrashHost: terminating host process (simulated crash).");
            Environment.Exit(9);
        }

        if (TryGetIntOption(request, "__testHangMs", out int hangMs) && hangMs > 0)
            await Task.Delay(hangMs).ConfigureAwait(true);

        if (!_window.IsPageReady)
        {
            Task waitPage = _pageReadyTcs.Task;
            Task timeout = Task.Delay(Math.Min(request.TimeoutMs, 30_000));
            if (await Task.WhenAny(waitPage, timeout).ConfigureAwait(true) != waitPage)
            {
                diagnostics.Add(new ThreeAuthoringDiagnostic("page_not_ready", "error",
                    "The authoring page did not become ready in time."));
                return Fail(request, "page_not_ready", "Authoring page is not ready.", diagnostics);
            }
        }

        string sourceFullPath = ThreeAuthoringPath.ResolveBelowRoot(_options.ProjectRoot, request.SourcePath);
        if (!File.Exists(sourceFullPath))
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("source_not_found", "error",
                $"Source file not found: {request.SourcePath}"));
            return Fail(request, "source_not_found", $"Source not found: {request.SourcePath}", diagnostics);
        }

        // Parse + validate the recipe. All operations (including validate) fail on an invalid
        // document: the recipe is the single authoritative source for every rendered artifact.
        string sourceText = File.ReadAllText(sourceFullPath);
        if (!ThreeFxDocument.TryParse(sourceText, out ThreeFxRecipe? recipe, out List<ThreeAuthoringDiagnostic> parseIssues))
        {
            diagnostics.AddRange(parseIssues);
            return Fail(request, "recipe_invalid", "Recipe failed to parse.", diagnostics);
        }

        diagnostics.AddRange(ThreeFxValidator.Validate(recipe!));
        ThreeFxValidator.ValidateDependencies(recipe!, _options.ProjectRoot, diagnostics);
        foreach (ThreeAuthoringDiagnostic diagnostic in diagnostics)
        {
            if (string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                return Fail(request, "recipe_invalid", "Recipe failed validation.", diagnostics);
        }

        string normalizedSource = ThreeFxDocument.Normalize(sourceText);
        Dictionary<string, string> dependencyHashes = HashDependencies(recipe!);
        string cacheIdentity = ThreeFxCacheIdentity.Compute(
            normalizedSource,
            dependencyHashes,
            recipe!.Seed,
            recipe.ExportWidth,
            recipe.ExportHeight,
            "r" + _window.ThreeRevision,
            ExporterVersion,
            ThisHostVersion(),
            request.TimeSeconds);
        diagnostics.Add(new ThreeAuthoringDiagnostic("cache_identity", "info", cacheIdentity));

        if (operation == ThreeAuthoringOperation.Validate)
        {
            diagnostics.Add(new ThreeAuthoringDiagnostic("validate_ok", "info",
                $"Recipe valid: {request.SourcePath} (stableId {recipe.StableId})"));
            return new ThreeAuthoringResponse(
                request.ProtocolVersion, request.RequestId, request.Operation,
                Succeeded: true, ErrorCode: null, Message: "Recipe valid.",
                Manifest: null, Diagnostics: diagnostics);
        }

        bool needsOutput = operation is ThreeAuthoringOperation.Export or ThreeAuthoringOperation.Bake;
        string? outputRelative = request.OutputDirectory;
        string? outputFull = needsOutput ? ThreeAuthoringPath.ResolveBelowRoot(_options.ProjectRoot, outputRelative!) : null;

        // Idempotency: an export into a directory whose state file carries the identical cache
        // identity is skipped entirely — repeated fetches neither re-render nor touch artifacts.
        bool forceRender = TryGetIntOption(request, "__forceRender", out int force) && force != 0;
        if (needsOutput && !forceRender)
        {
            ThreeFxExportState? existing = ThreeFxExportStateFile.TryRead(outputFull!);
            if (existing is not null && existing.CacheIdentity == cacheIdentity)
            {
                diagnostics.Add(new ThreeAuthoringDiagnostic("cache_hit", "info",
                    "Identical cache identity; existing artifacts reused."));
                return new ThreeAuthoringResponse(
                    request.ProtocolVersion, request.RequestId, request.Operation,
                    Succeeded: true, ErrorCode: null,
                    Message: "Export cached (identity match); artifacts untouched.",
                    Manifest: null, Diagnostics: diagnostics);
            }
        }

        int delayMs = TryGetIntOption(request, "__testDelayMs", out int d) ? d : 0;
        bool renderFail = TryGetIntOption(request, "__testRenderFail", out int rf) && rf != 0;

        // Page-side delay is impossible (render is synchronous because ExecuteScriptAsync does
        // not await promises), so the timeout test hook delays here on the host instead.
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(true);

        int width = operation == ThreeAuthoringOperation.Preview || operation == ThreeAuthoringOperation.Bake
            ? request.Width
            : recipe.ExportWidth;
        int height = operation == ThreeAuthoringOperation.Preview || operation == ThreeAuthoringOperation.Bake
            ? request.Height
            : recipe.ExportHeight;

        RenderAsyncRequest renderRequest = new(request, width, height, request.TimeSeconds, recipe, renderFail);
        byte[] png = (await RenderFrameAsync(renderRequest).ConfigureAwait(true)).Png;

        string stableId = recipe.StableId;

        // A3 — model artifact: the same recipe exports a GLB (geometry/hierarchy/animation) and
        // runs through the controlled conversion layer for the per-material mapping report.
        byte[]? glb = null;
        ThreeGltfReport? gltfReport = null;
        if (operation is ThreeAuthoringOperation.Export or ThreeAuthoringOperation.Bake)
        {
            glb = await ExportGltfAsync(request, recipe).ConfigureAwait(true);
            gltfReport = ThreeGltfConversion.Analyze(glb, stableId);
        }

        // A5 — flipbook bake: loop-phase-aligned frame times (t_i = i/N · duration), per-frame
        // PNGs plus a near-square column-major sheet. Any frame failure aborts via exception and
        // the staged-export dispose keeps the previous artifacts intact.
        List<(string Name, byte[] Png)> frames = [];
        string? sheetName = null;
        byte[]? sheetPng = null;
        if (operation == ThreeAuthoringOperation.Bake && recipe.Bake is { } bakeSpec)
        {
            for (int i = 0; i < bakeSpec.Frames; i++)
            {
                double t = i * bakeSpec.Duration / bakeSpec.Frames; // loop seam: t=0 ≡ t=duration
                var frame = await RenderFrameAsync(renderRequest with { TimeSeconds = t }).ConfigureAwait(true);
                frames.Add(($"frame_{i:D4}.png", frame.Png));
            }

            if (bakeSpec.Layout is "sheet" or "both")
            {
                sheetName = $"{stableId}_sheet.png";
                sheetPng = BakeSheetComposer.Compose(
                    frames.Select(f => f.Png).ToList(), width, height, bakeSpec.Transparent);
            }
        }

        string contentHash = Program.Sha256Hex(png);
        string sourceHash = Program.Sha256Hex(sourceText);

        using ThreeFxStagedExport staging = new(
            needsOutput ? outputFull! : ThreeAuthoringPath.ResolveBelowRoot(_options.ProjectRoot, PreviewFolder));
        string artifactFileName = needsOutput ? $"{stableId}.png" : $"{request.RequestId}.png";
        staging.WriteFile(artifactFileName, png);
        if (glb is not null && gltfReport is not null)
        {
            staging.WriteFile($"{stableId}.glb", glb);
            staging.WriteTextFile($"{stableId}.materials.json", gltfReport.ToJson());
        }
        foreach ((string frameName, byte[] framePng) in frames)
            staging.WriteFile(frameName, framePng);
        if (sheetName is not null && sheetPng is not null)
            staging.WriteFile(sheetName, sheetPng);

        string dir = needsOutput
            ? outputRelative!.Replace('\\', '/').TrimEnd('/')
            : PreviewFolder;
        string artifactRelativePath = $"{dir}/{artifactFileName}";

        string status = operation == ThreeAuthoringOperation.Bake ? "baked" : "native";
        var bakeFrameEntries = frames.Select(f => new ThreeArtifactEntry(
            StableId: $"{stableId}_{Path.GetFileNameWithoutExtension(f.Name)}",
            RelativePath: $"{dir}/{f.Name}",
            Kind: "texture/png",
            Status: "baked",
            ContentHash: Program.Sha256Hex(f.Png),
            Dependencies: [artifactRelativePath])).ToList();

        var manifest = new ThreeExportManifest(
            SchemaVersion: ThreeAuthoringProtocol.SchemaVersion,
            ProtocolVersion: ThreeAuthoringProtocol.Version,
            ThreeVersion: "r" + _window.ThreeRevision,
            ExporterVersion: ExporterVersion,
            HostVersion: ThisHostVersion(),
            SourceHash: sourceHash,
            Artifacts:
            [
                new ThreeArtifactEntry(
                    StableId: stableId,
                    RelativePath: artifactRelativePath,
                    Kind: "texture/png",
                    Status: status,
                    ContentHash: contentHash,
                    Dependencies: [request.SourcePath, .. recipe.Dependencies]),
                .. BuildModelArtifacts(stableId, artifactRelativePath, glb, gltfReport, outputRelative),
                .. bakeFrameEntries,
            ],
            Diagnostics: []);

        staging.WriteTextFile("manifest.json", JsonSerialization.Serialize(manifest));
        if (needsOutput)
        {
            staging.WriteTextFile(StateFileName, JsonSerialization.Serialize(new ThreeFxExportState(
                CacheIdentity: cacheIdentity,
                ArtifactFile: artifactFileName,
                ArtifactSha256: contentHash)));
        }

        // Promote only after every staged file exists and is verifiable; a throw anywhere above
        // disposed the staging folder and left the previous artifacts in place.
        staging.Promote();

        diagnostics.Add(new ThreeAuthoringDiagnostic("render_ok", "info",
            $"Rendered {width}x{height} @ sha256 {contentHash[..12]}"));
        if (frames.Count > 0)
        {
            string sheetNote = sheetPng is not null ? " + sheet" : string.Empty;
            string transparentNote = recipe.Bake is { Transparent: true } ? ", transparent" : string.Empty;
            diagnostics.Add(new ThreeAuthoringDiagnostic("bake_ok", "info",
                $"Baked {frames.Count} loop-aligned frames{sheetNote}{transparentNote}; "
                + "compute-less builds consume the baked variant."));
        }

        return new ThreeAuthoringResponse(
            request.ProtocolVersion, request.RequestId, request.Operation,
            Succeeded: true, ErrorCode: null,
            Message: $"{operation} completed in {stopwatch.ElapsedMilliseconds} ms.",
            Manifest: manifest, Diagnostics: diagnostics);
    }

    private Dictionary<string, string> HashDependencies(ThreeFxRecipe recipe)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string dependency in recipe.Dependencies)
        {
            string full = ThreeAuthoringPath.ResolveBelowRoot(_options.ProjectRoot, dependency);
            hashes[dependency] = Program.Sha256Hex(File.ReadAllBytes(full));
        }
        return hashes;
    }

    private sealed record RenderAsyncRequest(
        ThreeAuthoringRequest Request,
        int Width,
        int Height,
        double TimeSeconds,
        ThreeFxRecipe Recipe,
        bool Fail);

    /// <summary>Renders one frame through the page; throws on page-side failure.</summary>
    private async Task<RenderedFrame> RenderFrameAsync(RenderAsyncRequest r)
    {
        RenderResult render = await RenderAsync(
            r.Request, r.Width, r.Height, r.TimeSeconds, r.Recipe, r.Fail).ConfigureAwait(true);
        if (!render.Ok)
        {
            Console.Error.WriteLine($"host-trace: render frame failed: {render.Error}");
            throw new IOException($"Page render failed: {render.Error ?? "unknown error"}");
        }

        return new RenderedFrame(
            Png: Convert.FromBase64String(StripDataUrlPrefix(render.DataUrl!)),
            ThreeRevision: render.ThreeRevision,
            Ms: render.Ms);
    }

    private sealed record RenderedFrame(byte[] Png, string? ThreeRevision, double Ms);

    /// <summary>
    /// Calls into the page. <c>window.__threeHost.render</c> is synchronous, so
    /// <see cref="CoreWebView2.ExecuteScriptAsync"/> resolves with the result JSON directly.
    /// </summary>
    private async Task<RenderResult> RenderAsync(
        ThreeAuthoringRequest request,
        int width,
        int height,
        double timeSeconds,
        ThreeFxRecipe recipe,
        bool renderFail)
    {
        if (_window.Core is null)
            return new RenderResult(false, null, null, "webview-not-initialized", 0);

        string payload = JsonSerialization.Serialize(new RenderPayload(
            RequestId: request.RequestId,
            Width: width,
            Height: height,
            TimeSeconds: timeSeconds,
            Seed: recipe.Seed,
            Fail: renderFail,
            Mesh: recipe.Recipe.Mesh,
            Hue: recipe.Recipe.Hue,
            Metalness: recipe.Recipe.Metalness,
            Roughness: recipe.Recipe.Roughness,
            Background: recipe.Recipe.Background,
            Effects: recipe.Effects,
            Transparent: recipe.Bake is { Transparent: true }));
        string script = $"window.__threeHost.render({payload})";
        string? json = await _window.Core!.ExecuteScriptAsync(script).ConfigureAwait(true);

        RenderResult? result = JsonSerialization.Deserialize<RenderResult>(json ?? "null");
        return result ?? new RenderResult(false, null, null, "null-render-result", 0);
    }

    /// <summary>Manifest entries for the GLB + material report (export/bake only).</summary>
    private static IEnumerable<ThreeArtifactEntry> BuildModelArtifacts(
        string stableId,
        string pngRelativePath,
        byte[]? glb,
        ThreeGltfReport? report,
        string? outputRelative)
    {
        if (glb is null || report is null || outputRelative is null)
            yield break;

        string dir = outputRelative.Replace('\\', '/').TrimEnd('/');
        yield return new ThreeArtifactEntry(
            StableId: stableId + "_model",
            RelativePath: $"{dir}/{stableId}.glb",
            Kind: "model/glb",
            Status: "native",
            ContentHash: Program.Sha256Hex(glb),
            Dependencies: [$"{dir}/{Path.GetFileName(pngRelativePath)}"]);

        string reportJson = report.ToJson();
        yield return new ThreeArtifactEntry(
            StableId: stableId + "_materials",
            RelativePath: $"{dir}/{stableId}.materials.json",
            Kind: "conversion/report",
            Status: "report",
            ContentHash: Program.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(reportJson)),
            Dependencies: [$"{dir}/{stableId}.glb"]);
    }

    /// <summary>
    /// Drives the page's async GLTFExporter via polling (ExecuteScriptAsync does not await
    /// promises): start the job, then poll until ok/error or the request deadline.
    /// </summary>
    private async Task<byte[]> ExportGltfAsync(ThreeAuthoringRequest request, ThreeFxRecipe recipe)
    {
        if (_window.Core is null)
            throw new IOException("WebView2 is not initialized; cannot export GLB.");

        string payload = JsonSerialization.Serialize(new GltfExportPayload(
            Mesh: recipe.Recipe.Mesh,
            Hue: recipe.Recipe.Hue,
            Metalness: recipe.Recipe.Metalness,
            Roughness: recipe.Recipe.Roughness));
        await _window.Core.ExecuteScriptAsync($"window.__threeHost.exportGltf({payload})").ConfigureAwait(true);

        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(request.TimeoutMs - 1_000, 5_000));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(true);
            string? json = await _window.Core.ExecuteScriptAsync("window.__threeHost.pollGltf()").ConfigureAwait(true);
            if (json is null || json == "null" || json == "{}")
                continue;

            GltfPollResult? poll = JsonSerialization.Deserialize<GltfPollResult>(json);
            if (poll is null)
                continue;
            if (string.Equals(poll.State, "ok", StringComparison.Ordinal) && poll.Base64 is not null)
                return Convert.FromBase64String(poll.Base64);
            if (string.Equals(poll.State, "error", StringComparison.Ordinal))
                throw new IOException($"GLB export failed on the page: {poll.Error}");
        }

        throw new IOException("GLB export timed out waiting for the page.");
    }

    private void OnPageReady() => _pageReadyTcs.TrySetResult(true);

    private static TaskCompletionSource<bool> NewPageReadyTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool TryGetIntOption(ThreeAuthoringRequest request, string name, out int value)
    {
        value = 0;
        return request.Options is not null
            && request.Options.TryGetValue(name, out string? raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string StripDataUrlPrefix(string dataUrl) =>
        dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            ? dataUrl["data:image/png;base64,".Length..]
            : dataUrl;

    private static string ThisHostVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "0.0" : version.ToString(3);
    }

    private static ThreeAuthoringResponse Fail(
        ThreeAuthoringRequest request,
        string code,
        string message,
        List<ThreeAuthoringDiagnostic> diagnostics) =>
        new(
            request.ProtocolVersion, request.RequestId, request.Operation,
            Succeeded: false, ErrorCode: code, Message: message,
            Manifest: null, Diagnostics: diagnostics);

    public void Dispose() => _window.PageReady -= OnPageReady;

    private sealed record RenderPayload(
        string RequestId,
        int Width,
        int Height,
        double TimeSeconds,
        int Seed,
        bool Fail,
        string Mesh,
        double Hue,
        double Metalness,
        double Roughness,
        IReadOnlyList<double> Background,
        ThreeFxEffects Effects,
        bool Transparent);

    private sealed record GltfExportPayload(
        string Mesh, double Hue, double Metalness, double Roughness);

    private sealed record GltfPollResult(string? State, string? Base64, string? Error);

    public sealed record RenderResult(
        bool Ok, string? DataUrl, string? ThreeRevision, string? Error, double Ms);
}
