using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// CLI runner: maps a <see cref="HostCommand"/> to its service call and prints structured JSON.
/// This is the human/script entry point (the MCP-server entry point is <c>serve</c> in Program).
/// Shares the same services/DTOs as the MCP path so both surfaces stay in sync (XMcp.XMcpCli pattern).
/// </summary>
internal static class CliRunner
{
    public static async Task RunAsync(
        HostCommand command,
        IReadOnlyList<string> args,
        IServiceProvider services)
    {
        // ★ P2-7 — telemetry scope: emits one TELEM line to stderr per command (name/duration/
        // success). Wraps the whole invocation so a shell wrapper can observe every call.
        string toolName = command.ToString();
        var sw = Stopwatch.StartNew();
        try
        {
            object result = await InvokeCommand(command, args, services);
            JsonOutput.Write(result);
            TelemetrySink.EmitOk(toolName, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            TelemetrySink.EmitError(toolName, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    private static async Task<object> InvokeCommand(
        HostCommand command,
        IReadOnlyList<string> args,
        IServiceProvider services)
    {
        return command switch
        {
            HostCommand.Health => services.GetRequiredService<HealthService>().GetSnapshot(),
            HostCommand.Inspect => services.GetRequiredService<ProjectDescriptor>(),
            HostCommand.Capabilities => services.GetRequiredService<CapabilityCatalog>().GetSnapshot(),
            HostCommand.HarnessInstall => HarnessCli.Install(args, services),
            HostCommand.HarnessStatus => HarnessCli.Status(services),
            HostCommand.HarnessUpgrade => HarnessCli.Upgrade(args, services),
            HostCommand.ThreeAuthoring => ThreeAuthoringCli.Run(args, services),
            HostCommand.ProjectRoots => AiTools.ListWorkspace(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                GetOptionalOption(args, "--folder"),
                GetIntOption(args, "--max-entries", 500)),
            HostCommand.Doctor => await services.GetRequiredService<DoctorService>()
                .RunAsync(CancellationToken.None).ConfigureAwait(false),
            HostCommand.SceneList => SceneReader.ListScenes(
                services.GetRequiredService<ProjectDescriptor>().AssetsRoot(),
                GetOptionalOption(args, "--query"),
                GetIntOption(args, "--offset", 0),
                GetIntOption(args, "--limit", 100)),
            HostCommand.SceneHierarchy => SceneReader.ReadHierarchy(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--scene")),
                maxObjects: GetIntOption(args, "--max-objects", 500),
                tagLayer: TagLayerResolver.Load(services.GetRequiredService<ProjectDescriptor>().ProjectRoot)),
            HostCommand.PrefabHierarchy => SceneReader.ReadPrefabHierarchy(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--prefab")),
                maxObjects: GetIntOption(args, "--max-objects", 500),
                tagLayer: TagLayerResolver.Load(services.GetRequiredService<ProjectDescriptor>().ProjectRoot)),
            HostCommand.PrefabObjectPatch => await services.GetRequiredService<SceneWriter>().PatchPrefabAsync(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--prefab")),
                GetRequiredOption(args, "--prefab"),
                GetRequiredOption(args, "--object"),
                ParsePatches(GetRequiredOption(args, "--patches")),
                XEngine.Data.Provenance.ChangeProvenance.ForTool(
                    "xengine-cli.prefab-object-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
                GetFlag(args, "--dry-run"),
                GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false),
            HostCommand.PrefabComponentPatch => await PrefabComponentPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.AssetSearch => AssetReader.Search(
                services.GetRequiredService<ProjectDescriptor>().AssetsRoot(),
                GetOptionalOption(args, "--query"),
                GetOptionalOption(args, "--extension"),
                GetIntOption(args, "--limit", 200)),
            HostCommand.AssetInspect => AssetReader.Inspect(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--asset")),
                GetRequiredOption(args, "--asset")),
            HostCommand.AssetSubassets => SubAssetResolver.Resolve(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--asset")),
                GetRequiredOption(args, "--asset")),
            HostCommand.AssetMeshStats => services.GetRequiredService<PlayablePropService>().MeshStats(
                GetRequiredOption(args, "--asset"),
                ParseOptionalInt(GetOptionalOption(args, "--max-triangles"))),
            HostCommand.SceneColliderAdd => await services.GetRequiredService<PlayablePropService>().AddColliderAsync(
                GetRequiredOption(args, "--scene"),
                GetRequiredOption(args, "--object"),
                GetRequiredOption(args, "--model"),
                GetOptionalOption(args, "--shape") ?? "box",
                GetFlag(args, "--convex"),
                GetRequiredOption(args, "--idempotency-key"),
                GetOptionalOption(args, "--intent"),
                GetFlag(args, "--dry-run"),
                GetOptionalOption(args, "--expected-before-hash"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.PrefabCreate => await services.GetRequiredService<PlayablePropService>().CreatePrefabAsync(
                GetRequiredOption(args, "--scene"),
                GetRequiredOption(args, "--object"),
                GetRequiredOption(args, "--destination"),
                GetRequiredOption(args, "--idempotency-key"),
                GetOptionalOption(args, "--intent"),
                GetFlag(args, "--overwrite"),
                GetFlag(args, "--dry-run"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.SpineGenSlice => services.GetRequiredService<SpineGenSliceService>().Slice(
                GetRequiredOption(args, "--image"),
                ParseOptionalInt(GetOptionalOption(args, "--alpha-threshold")),
                ParseOptionalInt(GetOptionalOption(args, "--min-area-pixels")),
                ParseOptionalInt(GetOptionalOption(args, "--crop-padding")),
                ParseOptionalInt(GetOptionalOption(args, "--packing-padding")),
                GetOptionalOption(args, "--split"),
                ParseOptionalInt(GetOptionalOption(args, "--marker-min-depth")),
                ParseOptionalInt(GetOptionalOption(args, "--marker-spacing")),
                GetFlag(args, "--dry-run"), GetOptionalOption(args, "--recipe")),
            HostCommand.SpineGenFill => await services.GetRequiredService<SpineGenFillService>().FillAsync(
                GetRequiredOption(args, "--slice"),
                GetOptionalOption(args, "--provider"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.SpineGenRig => services.GetRequiredService<SpineGenRigService>().Rig(
                GetRequiredOption(args, "--slice")),
            HostCommand.SpineGenSkin => services.GetRequiredService<SpineGenSkinService>().Skin(
                GetRequiredOption(args, "--slice"),
                ParseOptionalInt(GetOptionalOption(args, "--simplify-epsilon-x100")),
                ParseOptionalInt(GetOptionalOption(args, "--max-hull-vertices")),
                ParseOptionalInt(GetOptionalOption(args, "--min-mesh-pixels"))),
            HostCommand.SpineGenAnimate => await services.GetRequiredService<SpineGenAnimateService>().AnimateAsync(
                GetRequiredOption(args, "--slice"),
                GetOptionalOption(args, "--motion"),
                GetOptionalOption(args, "--prompt"),
                GetOptionalOption(args, "--provider"),
                GetOptionalOption(args, "--model"),
                CancellationToken.None, GetOptionalOption(args, "--plan")).ConfigureAwait(false),
            HostCommand.GenCandidates => GenCandidateTools.List(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                GetOptionalOption(args, "--kind"),
                GetOptionalOption(args, "--query"),
                !GetFlag(args, "--all"),
                GetFlag(args, "--include-discarded")),
            HostCommand.GenCandidateChoose => GenCandidateTools.Choose(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                GetRequiredOption(args, "--task")),
            HostCommand.GenCandidateDiscard => GenCandidateTools.Discard(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                GetRequiredOption(args, "--task")),
            HostCommand.StyleAnchorList => StyleAnchorTools.List(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot),
            HostCommand.StyleAnchorUpsert => StyleAnchorTools.Upsert(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                GetRequiredOption(args, "--id"),
                GetOptionalOption(args, "--lora"),
                GetOptionalOption(args, "--palette"),
                GetOptionalOption(args, "--negative"),
                GetOptionalOption(args, "--refs")),
            HostCommand.SpriteSlice => await services.GetRequiredService<SpriteWireService>().SliceAsync(
                GetRequiredOption(args, "--asset"),
                GetIntOption(args, "--columns", 4),
                GetIntOption(args, "--rows", 1),
                GetIntOption(args, "--cell-width", 0),
                GetIntOption(args, "--cell-height", 0),
                ParseOptionalFloat(GetOptionalOption(args, "--pixels-per-unit")) ?? 100f,
                GetRequiredOption(args, "--idempotency-key"),
                GetOptionalOption(args, "--intent"),
                GetFlag(args, "--dry-run"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.SpriteWire => await services.GetRequiredService<SpriteWireService>().WireAsync(
                GetRequiredOption(args, "--scene"),
                GetRequiredOption(args, "--object"),
                GetRequiredOption(args, "--asset"),
                GetIntOption(args, "--columns", 4),
                GetIntOption(args, "--rows", 1),
                GetIntOption(args, "--cell-width", 0),
                GetIntOption(args, "--cell-height", 0),
                ParseOptionalFloat(GetOptionalOption(args, "--pixels-per-unit")) ?? 100f,
                ParseOptionalFloat(GetOptionalOption(args, "--fps")) ?? 12f,
                GetRequiredOption(args, "--idempotency-key"),
                GetOptionalOption(args, "--intent"),
                GetFlag(args, "--dry-run"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.CharacterWire => await services.GetRequiredService<CharacterWireService>().WireAsync(
                GetRequiredOption(args, "--scene"),
                GetRequiredOption(args, "--object"),
                GetRequiredOption(args, "--model"),
                GetOptionalOption(args, "--controller"),
                GetRequiredOption(args, "--idempotency-key"),
                GetOptionalOption(args, "--intent"),
                GetFlag(args, "--dry-run"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.GenEnsureBudget => await services.GetRequiredService<PlayablePropService>().EnsureBudgetAsync(
                GetRequiredOption(args, "--asset"),
                ParseOptionalInt(GetOptionalOption(args, "--max-triangles")) ?? 10000,
                GetOptionalOption(args, "--task"),
                GetRequiredOption(args, "--idempotency-key"),
                GetOptionalOption(args, "--intent"),
                GetFlag(args, "--dry-run"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.GenSubmit => await services.GetRequiredService<GenService>().SubmitAsync(
                GetRequiredOption(args, "--provider"),
                GetRequiredOption(args, "--kind"),
                GetRequiredOption(args, "--prompt"),
                GetOptionalOption(args, "--model"),
                ParseOptionalInt(GetOptionalOption(args, "--seed")),
                GetOptionalOption(args, "--style-anchor"),
                GetOptionalOption(args, "--extra"),
                GetFlag(args, "--dry-run"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.GenStatus => await services.GetRequiredService<GenService>().StatusAsync(
                GetRequiredOption(args, "--task"), CancellationToken.None).ConfigureAwait(false),
            HostCommand.GenFetch => await services.GetRequiredService<GenService>().FetchAsync(
                GetRequiredOption(args, "--task"),
                GetOptionalOption(args, "--destination") ?? "Assets/GenArt",
                GetOptionalOption(args, "--base-name"),
                GetFlag(args, "--overwrite"),
                GetOptionalOption(args, "--intent"),
                GetRequiredOption(args, "--idempotency-key"),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.AssetImport => await AssetImportAsync(args, services).ConfigureAwait(false),
            HostCommand.AssetMove => await AssetMoveAsync(args, services).ConfigureAwait(false),
            HostCommand.AssetDelete => await AssetDeleteAsync(args, services).ConfigureAwait(false),
            HostCommand.MaterialInspect => SceneReader.ReadMaterial(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--material")),
                GetRequiredOption(args, "--material")),
            HostCommand.MaterialPatch => await MaterialPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.AssetDataInspect => SceneReader.ReadAssetData(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--asset")),
                GetRequiredOption(args, "--asset")),
            HostCommand.AssetDataPatch => await AssetDataPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.VolumeInspect => VolumeInspect(args, services),
            HostCommand.VolumePatch => await VolumePatchAsync(args, services).ConfigureAwait(false),
            HostCommand.ShaderGraphInspect => ShaderGraphInspect(args, services),
            HostCommand.ShaderGraphPatch => await ShaderGraphPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.ShaderGraphCompile => ShaderGraphCompile(args, services),
            HostCommand.VfxGraphInspect => VfxGraphInspect(args, services),
            HostCommand.VfxGraphPatch => await VfxGraphPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.VfxGraphCompile => VfxGraphCompile(args, services),
            HostCommand.VfxGraphNodes => AiTools.ListVfxGraphNodes(),
            HostCommand.VfxGraphCreate => VfxGraphCreate(args, services),
            HostCommand.XuiValidate => XuiTools.Validate(
                services, GetXuiPath(args), !GetFlag(args, "--errors-only")),
            HostCommand.XuiInspect => XuiTools.Inspect(
                services, GetXuiPath(args), GetOptionalOption(args, "--screen")),
            HostCommand.XuiPatch => await XuiPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.PxlValidate => PxlTools.Validate(services, GetPxlPath(args)),
            HostCommand.PxlRender => PxlTools.Render(
                services,
                GetPxlPath(args),
                GetOptionalOption(args, "--section"),
                GetIntOption(args, "--scale", 8),
                GetOptionalOption(args, "--out")),
            HostCommand.XuiScaffold => XuiTools.Scaffold(
                services,
                GetXuiPath(args),
                GetRequiredOption(args, "--screen"),
                GetRequiredOption(args, "--out"),
                GetOptionalOption(args, "--namespace"),
                GetFlag(args, "--overwrite-controller"),
                GetFlag(args, "--dry-run")),
            HostCommand.XuiI18nExtract => XuiTools.I18nExtract(
                services,
                GetRequiredOption(args, "--locales").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                GetOptionalOption(args, "--out") ?? "UI/Locales",
                GetOptionalOption(args, "--documents"),
                GetOptionalOption(args, "--scripts"),
                GetFlag(args, "--dry-run")),
            HostCommand.XuiFromPsd => PsdTools.FromPsd(
                services,
                GetRequiredOption(args, "--doc"),
                GetOptionalOption(args, "--screen"),
                GetOptionalOption(args, "--out"),
                GetFlag(args, "--include-hidden"),
                GetFlag(args, "--dry-run")),
            HostCommand.PsdInspect => PsdTools.Inspect(services, GetRequiredOption(args, "--doc")),
            HostCommand.PsdRender => PsdTools.Render(
                services,
                GetRequiredOption(args, "--doc"),
                GetOptionalOption(args, "--layer"),
                GetOptionalOption(args, "--out")),
            HostCommand.SceneSettingsQuery => SceneReader.ReadSceneSettings(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--scene")),
                GetRequiredOption(args, "--scene")),
            HostCommand.SceneSettingsPatch => await SceneSettingsPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.CodeSymbols => await services.GetRequiredService<CodeIndexService>().FindSymbolsAsync(
                services.GetRequiredService<ProjectDescriptor>().SolutionPath
                    ?? throw new FileNotFoundException("No solution file found at the project root."),
                GetRequiredOption(args, "--query"),
                GetIntOption(args, "--offset", 0),
                GetIntOption(args, "--limit", 100),
                CancellationToken.None).ConfigureAwait(false),
            HostCommand.FindInFile => FileSearcher.Search(
                services.GetRequiredService<ProjectDescriptor>().AssetsRoot(),
                GetRequiredOption(args, "--pattern"),
                GetOptionalOption(args, "--path"),
                GetOptionalOption(args, "--glob"),
                GetIntOption(args, "--limit", 100)),
            HostCommand.ScriptCreate => await ScriptCreateAsync(args, services).ConfigureAwait(false),
            HostCommand.ScriptDelete => await services.GetRequiredService<ScriptWriter>().DeleteAsync(
                services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(GetRequiredOption(args, "--path")),
                GetRequiredOption(args, "--path"),
                XEngine.Data.Provenance.ChangeProvenance.ForTool(
                    "xengine-cli.script-delete", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key"))).ConfigureAwait(false),
            HostCommand.ScriptApplyEdits => await ScriptApplyEditsAsync(args, services).ConfigureAwait(false),
            HostCommand.BuildProject => await BuildRunner.BuildAsync(
                GetRequiredOption(args, "--editor"),
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                GetRequiredOption(args, "--output"),
                GetOptionalOption(args, "--build-profile"),
                GetOptionalOption(args, "--platform")).ConfigureAwait(false),
            HostCommand.RunHeadless => await BuildRunner.RunHeadlessAsync(
                GetRequiredOption(args, "--player"),
                GetOptionalOption(args, "--scene"),
                GetIntOption(args, "--max-frames", 60),
                GetIntOption(args, "--max-seconds", 0)).ConfigureAwait(false),
            HostCommand.RunTests => await TestRunner.RunAsync(
                GetOptionalOption(args, "--target"),
                services.GetRequiredService<ProjectDescriptor>().SolutionPath,
                GetOptionalOption(args, "--filter"),
                GetIntOption(args, "--timeout", 300)).ConfigureAwait(false),
            HostCommand.WorkflowList => services.GetRequiredService<WorkflowCatalog>().List(),
            HostCommand.WorkflowRun => await services.GetRequiredService<WorkflowRunner>().RunAsync(
                (services.GetRequiredService<WorkflowCatalog>().Get(GetRequiredOption(args, "--name"))
                    ?? throw new KeyNotFoundException("Workflow not found: " + GetRequiredOption(args, "--name"))),
                JsonDocument.Parse(GetOptionalOption(args, "--inputs") ?? "{}").RootElement.Clone(),
                GetFlag(args, "--dry-run")).ConfigureAwait(false),
            HostCommand.SceneObjectPatch => await ScenePatchAsync(args, services).ConfigureAwait(false),
            HostCommand.SceneObjectCreate => await SceneCreateAsync(args, services).ConfigureAwait(false),
            HostCommand.SceneObjectDelete => await SceneDeleteAsync(args, services).ConfigureAwait(false),
            HostCommand.SceneComponentAdd => await SceneComponentAddAsync(args, services).ConfigureAwait(false),
            HostCommand.AnimatorComponentAdd => await AnimatorComponentAddAsync(args, services).ConfigureAwait(false),
            HostCommand.SceneComponentRemove => await SceneComponentRemoveAsync(args, services).ConfigureAwait(false),
            HostCommand.SceneComponentPatch => await SceneComponentPatchAsync(args, services).ConfigureAwait(false),
            HostCommand.JournalQuery => JournalReader.Query(
                services.GetRequiredService<ProjectDescriptor>().ProjectRoot,
                new JournalReadQuery(
                    GetOptionalOption(args, "--asset"),
                    null, null,
                    GetOptionalOption(args, "--by-author"),
                    GetOptionalOption(args, "--intent-contains"),
                    GetIntOption(args, "--offset", 0),
                    Math.Clamp(GetIntOption(args, "--limit", 100), 1, 200))),
            HostCommand.NavInspect or HostCommand.NavQueryPath or HostCommand.NavExport or HostCommand.NavSettingsPatch
                => await NavCli.Invoke(command, args, services).ConfigureAwait(false),
            HostCommand.AnimatorInspect or HostCommand.AnimatorValidate or HostCommand.AnimatorStateCreate or HostCommand.AnimatorStatePatch
                or HostCommand.AnimatorStateDelete                 or HostCommand.AnimatorTransitionCreate
                or HostCommand.AnimatorTransitionPatch or HostCommand.AnimatorTransitionDelete
                or HostCommand.AnimatorTransitionReorder
                or HostCommand.AnimatorParameterAdd or HostCommand.AnimatorParameterRemove
                or HostCommand.AnimatorLayerAdd or HostCommand.AnimatorLayerPatch
                or HostCommand.AnimatorStateMachineCreate or HostCommand.AnimatorStateMachineDelete
                or HostCommand.AnimatorStateMachineMove
                or HostCommand.ClipInspect or HostCommand.ClipRetarget or HostCommand.ClipPatch or HostCommand.ClipCurvePatch
                or HostCommand.ClipEventAdd or HostCommand.ClipEventPatch or HostCommand.ClipEventRemove
                or HostCommand.BlendTreeInspect or HostCommand.BlendTreePatch
                or HostCommand.OverrideControllerInspect or HostCommand.OverrideControllerPatch
                or HostCommand.AvatarMaskInspect or HostCommand.AvatarMaskPatch
                => await AnimationCli.Invoke(command, args, services).ConfigureAwait(false),
            // Stubs for commands that depend on services not yet wired (B1/B2). Kept explicit so
            // the CLI surfaces a clear "not yet available" rather than a silent default.
            HostCommand.PackageGraph => throw new NotImplementedException(
                "package-graph lands with the B1 system tools."),
            HostCommand.PackageSearch => await PackageRegistryCli.SearchAsync(args, services).ConfigureAwait(false),
            HostCommand.PackageInfo => await PackageRegistryCli.InfoAsync(args, services).ConfigureAwait(false),
            HostCommand.PackageInstall => await PackageRegistryCli.InstallAsync(args, services).ConfigureAwait(false),
            HostCommand.PackageUpdate => await PackageRegistryCli.UpdateAsync(args, services).ConfigureAwait(false),
            HostCommand.PackagePublish => await PackageRegistryCli.PublishAsync(args, services).ConfigureAwait(false),
            HostCommand.OperationGet => throw new NotImplementedException(
                "operation-get lands with the A2 operation/journal store."),
            // OperationCancel removed (P1-1): the Host runs ops inline, so there is nothing to cancel.
            HostCommand.Call => throw new NotImplementedException(
                "call lands with the B2 write tools."),
            _ => throw new InvalidOperationException("The serve command cannot run via the CLI runner."),
        };
    }

    internal static string GetRequiredOption(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count; ++index)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal)) break;
            return args[index + 1];
        }

        throw new ArgumentException($"{name} requires an argument.");
    }

    internal static string? GetOptionalOption(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count; ++index)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Count
                && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    internal static int GetIntOption(IReadOnlyList<string> args, string name, int defaultValue)
    {
        string? value = GetOptionalOption(args, name);
        if (value is null) return defaultValue;
        return int.TryParse(value, out int parsed)
            ? parsed
            : throw new ArgumentException($"{name} requires an integer argument.");
    }

    private static int? ParseOptionalInt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, out int parsed)
            ? parsed
            : throw new ArgumentException($"Expected an integer, got '{value}'.");
    }

    private static float? ParseOptionalFloat(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : throw new ArgumentException($"Expected a number, got '{value}'.");
    }

    internal static bool GetFlag(IReadOnlyList<string> args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    // ---- scene-write CLI adapters (parse args -> SceneWriter call) ----

    private static async Task<SceneWriteResult> ScenePatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--object"),
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-object-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static async Task<SceneWriteResult> SceneCreateAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.CreateAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--name"),
            GetOptionalOption(args, "--parent"),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-object-create", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run")).ConfigureAwait(false);
    }

    private static async Task<SceneWriteResult> SceneDeleteAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.DeleteAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--object"),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-object-delete", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run")).ConfigureAwait(false);
    }

    private static async Task<SceneWriteResult> SceneComponentAddAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        string? patchesJson = GetOptionalOption(args, "--fields");
        List<SceneFieldPatch>? fields = patchesJson is null ? null : ParsePatches(patchesJson);
        return await writer.AddComponentAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--object"),
            GetRequiredOption(args, "--type"),
            fields,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-component-add", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static Task<SceneWriteResult> AnimatorComponentAddAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return AnimatorComponentAdd.RunAsync(
            project, writer,
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--object"),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.animator-component-add", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--create-controller"),
            GetOptionalOption(args, "--clip-guid"),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash"),
            CancellationToken.None);
    }

    private static async Task<SceneWriteResult> SceneComponentRemoveAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string? componentId = GetOptionalOption(args, "--component-id");
        string? componentType = GetOptionalOption(args, "--type");
        if (string.IsNullOrEmpty(componentId) && string.IsNullOrEmpty(componentType))
        {
            throw new ArgumentException(
                "scene-component-remove needs either --component-id (GUID) or --type (short name).");
        }
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.RemoveComponentAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--object"),
            componentId,
            componentType,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-component-remove", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static async Task<SceneWriteResult> SceneComponentPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string? componentId = GetOptionalOption(args, "--component-id");
        string? componentType = GetOptionalOption(args, "--type");
        if (string.IsNullOrEmpty(componentId) && string.IsNullOrEmpty(componentType))
        {
            throw new ArgumentException(
                "scene-component-patch needs either --component-id (GUID) or --type (short name) to locate the component.");
        }
        bool? enable = null;
        if (GetFlag(args, "--enable")) enable = true;
        else if (GetFlag(args, "--disable")) enable = false;
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchComponentAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            GetRequiredOption(args, "--object"),
            componentId,
            componentType,
            ParsePatches(GetRequiredOption(args, "--patches")),
            enable,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-component-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static async Task<SceneWriteResult> PrefabComponentPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string? componentId = GetOptionalOption(args, "--component-id");
        string? componentType = GetOptionalOption(args, "--type");
        if (string.IsNullOrEmpty(componentId) && string.IsNullOrEmpty(componentType))
        {
            throw new ArgumentException(
                "prefab-component-patch needs either --component-id (GUID) or --type (short name).");
        }
        bool? enable = null;
        if (GetFlag(args, "--enable")) enable = true;
        else if (GetFlag(args, "--disable")) enable = false;
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchComponentAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--prefab")),
            GetRequiredOption(args, "--prefab"),
            GetRequiredOption(args, "--object"),
            componentId,
            componentType,
            ParsePatches(GetRequiredOption(args, "--patches")),
            enable,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.prefab-component-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash"),
            recursive: true).ConfigureAwait(false);
    }

    private static Task<XEngine.Ai.Protocol.AssetImportResult> AssetImportAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<AssetWriter>();
        return writer.ImportAsync(
            GetRequiredOption(args, "--source"),
            project.ResolveAssetPath(GetRequiredOption(args, "--destination")),
            GetRequiredOption(args, "--destination"),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.asset-import", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--overwrite"),
            animationType: ParseOptionalInt(GetOptionalOption(args, "--animation-type")));
    }

    private static Task<XEngine.Ai.Protocol.AssetMoveResult> AssetMoveAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<AssetWriter>();
        return writer.MoveAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--source")),
            project.ResolveAssetPath(GetRequiredOption(args, "--destination")),
            GetRequiredOption(args, "--source"),
            GetRequiredOption(args, "--destination"),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.asset-move", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")));
    }

    private static Task<XEngine.Ai.Protocol.AssetDeleteResult> AssetDeleteAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<AssetWriter>();
        return writer.DeleteAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--asset")),
            GetRequiredOption(args, "--asset"),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.asset-delete", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")));
    }

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> MaterialPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchMaterialAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--material")),
            GetRequiredOption(args, "--material"),
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.material-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> AssetDataPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchAssetDataAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--asset")),
            GetRequiredOption(args, "--asset"),
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.asset-data-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static AssetDataDetail VolumeInspect(IReadOnlyList<string> args, IServiceProvider services)
    {
        string volumePath = GetVolumePath(args);
        AiTools.RequireVolumeProfilePath(volumePath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        return SceneReader.ReadAssetData(project.ResolveAssetPath(volumePath), volumePath);
    }

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> VolumePatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string volumePath = GetVolumePath(args);
        AiTools.RequireVolumeProfilePath(volumePath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchAssetDataAsync(
            project.ResolveAssetPath(volumePath),
            volumePath,
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.volume-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static string GetVolumePath(IReadOnlyList<string> args)
        => GetOptionalOption(args, "--volume")
           ?? GetOptionalOption(args, "--asset")
           ?? throw new ArgumentException("--volume is required.");

    private static AssetDataDetail ShaderGraphInspect(IReadOnlyList<string> args, IServiceProvider services)
    {
        string graphPath = GetGraphPath(args);
        AiTools.RequireShaderGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        return SceneReader.ReadAssetData(project.ResolveAssetPath(graphPath), graphPath);
    }

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> ShaderGraphPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string graphPath = GetGraphPath(args);
        AiTools.RequireShaderGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchAssetDataAsync(
            project.ResolveAssetPath(graphPath),
            graphPath,
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.shadergraph-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static ShaderGraphCompileResult ShaderGraphCompile(IReadOnlyList<string> args, IServiceProvider services)
        => AiTools.CompileShaderGraph(services, GetGraphPath(args), GetOptionalOption(args, "--out"));

    private static AssetDataDetail VfxGraphInspect(IReadOnlyList<string> args, IServiceProvider services)
    {
        string graphPath = GetVfxPath(args);
        AiTools.RequireVfxGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        return SceneReader.ReadAssetData(project.ResolveAssetPath(graphPath), graphPath);
    }

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> VfxGraphPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        string graphPath = GetVfxPath(args);
        AiTools.RequireVfxGraphPath(graphPath);
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchAssetDataAsync(
            project.ResolveAssetPath(graphPath),
            graphPath,
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.vfxgraph-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> XuiPatchAsync(
        IReadOnlyList<string> args, IServiceProvider services)
    {
        string? removes = GetOptionalOption(args, "--remove");
        return await XuiTools.Patch(
            services,
            GetXuiPath(args),
            ParsePatches(GetRequiredOption(args, "--patches")).ToArray(),
            GetRequiredOption(args, "--idempotency-key"),
            GetOptionalOption(args, "--intent"),
            removes == null ? null : removes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            GetOptionalOption(args, "--expected-before-hash"),
            GetFlag(args, "--dry-run")).ConfigureAwait(false);
    }

    private static string GetPxlPath(IReadOnlyList<string> args)
        => GetOptionalOption(args, "--doc")
        ?? GetOptionalOption(args, "--document")
        ?? GetRequiredOption(args, "--asset");

    /// <summary>Accepts --doc, with --document and --asset as aliases (the same path under three names
    /// an agent might reasonably reach for).</summary>
    private static string GetXuiPath(IReadOnlyList<string> args)
        => GetOptionalOption(args, "--doc")
        ?? GetOptionalOption(args, "--document")
        ?? GetRequiredOption(args, "--asset");

    private static VfxGraphCompileResult VfxGraphCompile(IReadOnlyList<string> args, IServiceProvider services)
        => AiTools.CompileVfxGraph(services, GetVfxPath(args), GetOptionalOption(args, "--out"));

    private static VfxGraphCreateResult VfxGraphCreate(IReadOnlyList<string> args, IServiceProvider services)
        => AiTools.CreateVfxGraph(
            services,
            GetVfxPath(args),
            GetOptionalOption(args, "--template") ?? "Loop",
            GetFlag(args, "--dry-run"));

    private static string GetVfxPath(IReadOnlyList<string> args)
        => GetOptionalOption(args, "--graph")
           ?? GetOptionalOption(args, "--asset")
           ?? throw new ArgumentException("--graph is required.");

    private static string GetGraphPath(IReadOnlyList<string> args)
        => GetOptionalOption(args, "--graph")
           ?? GetOptionalOption(args, "--asset")
           ?? throw new ArgumentException("--graph is required.");

    private static async Task<XEngine.Ai.Protocol.SceneWriteResult> SceneSettingsPatchAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return await writer.PatchSceneSettingsAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--scene")),
            GetRequiredOption(args, "--scene"),
            ParsePatches(GetRequiredOption(args, "--patches")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.scene-settings-patch", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetFlag(args, "--dry-run"),
            GetOptionalOption(args, "--expected-before-hash")).ConfigureAwait(false);
    }

    private static async Task<XEngine.Ai.Protocol.ScriptEditResult> ScriptCreateAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<ScriptWriter>();
        // --content reads from a file path when prefixed with @, else literal.
        string contentSpec = GetRequiredOption(args, "--content");
        string content = contentSpec.StartsWith('@') && File.Exists(contentSpec[1..])
            ? File.ReadAllText(contentSpec[1..])
            : contentSpec;
        return await writer.CreateAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--path")),
            GetRequiredOption(args, "--path"),
            content,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.script-create", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key"))).ConfigureAwait(false);
    }

    private static async Task<XEngine.Ai.Protocol.ScriptEditResult> ScriptApplyEditsAsync(IReadOnlyList<string> args, IServiceProvider services)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<ScriptWriter>();
        return await writer.ApplyEditsAsync(
            project.ResolveAssetPath(GetRequiredOption(args, "--path")),
            GetRequiredOption(args, "--path"),
            ParseScriptEdits(GetRequiredOption(args, "--edits")),
            XEngine.Data.Provenance.ChangeProvenance.ForTool(
                "xengine-cli.script-apply-edits", GetOptionalOption(args, "--intent"), GetRequiredOption(args, "--idempotency-key")),
            GetOptionalOption(args, "--expected-before-sha")).ConfigureAwait(false);
    }

    /// <summary>Parse the <c>--edits</c> JSON array into <see cref="ScriptTextEdit"/> records.</summary>
    private static List<ScriptTextEdit> ParseScriptEdits(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var edits = new List<ScriptTextEdit>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            edits.Add(new ScriptTextEdit(
                el.GetProperty("oldText").GetString() ?? "",
                el.GetProperty("newText").GetString() ?? "",
                el.TryGetProperty("occurrence", out JsonElement occ) && occ.ValueKind == JsonValueKind.String
                    ? occ.GetString() ?? "first" : "first"));
        }
        return edits;
    }

    /// <summary>Parse the <c>--patches</c> JSON array into <see cref="SceneFieldPatch"/> records.</summary>
    private static List<SceneFieldPatch> ParsePatches(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("--patches must be a JSON array of {path, value}.");
        }

        var patches = new List<SceneFieldPatch>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            string path = el.GetProperty("path").GetString() ?? "";
            string value = el.GetProperty("value").GetString() ?? "";
            patches.Add(new SceneFieldPatch(path, value));
        }

        return patches;
    }
}
