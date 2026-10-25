using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Parsed Host options. <c>--project</c> selects the target XEngine project root; falls back to
/// the <c>XENGINE_PROJECT_ROOT</c> env var, then the current directory (XMcp pattern).
/// <c>--edition</c> pins the tool surface (<c>engine-dev</c> | <c>user</c>); omitted/`auto` means
/// the Program resolves it from the project layout via <see cref="AiEditionDetector.Detect"/>.
/// </summary>
internal sealed record HostOptions(string ProjectRoot, AiEdition? Edition = null)
{
    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        string? projectRoot = null;
        AiEdition? edition = null;

        for (int index = 0; index < args.Count; ++index)
        {
            if (string.Equals(args[index], "--edition", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--edition requires a value (engine-dev, user, or auto).");
                }

                edition = AiEditionNames.TryParse(args[index + 1]);
                ++index;
                continue;
            }

            if (!string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(args[index], "-p", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count)
            {
                throw new ArgumentException("--project requires a project path argument.");
            }

            projectRoot = args[index + 1];
            // Keep scanning: --edition may appear after --project.
            ++index;
        }

        projectRoot ??= Environment.GetEnvironmentVariable("XENGINE_PROJECT_ROOT");
        projectRoot ??= Environment.CurrentDirectory;

        return new HostOptions(Path.GetFullPath(projectRoot), edition);
    }
}

/// <summary>
/// The Host's top-level commands. Mirrors XMcp's enum but trimmed to the XEngine tool surface;
/// new commands are appended here as B1/B2/B3 tools land.
/// </summary>
internal enum HostCommand
{
    Serve,
    Health,
    Inspect,
    Capabilities,
    ProjectRoots,
    Doctor,
    PackageGraph,
    PackageSearch,
    PackageInfo,
    PackageInstall,
    PackageUpdate,
    PackagePublish,
    SceneList,
    SceneHierarchy,
    PrefabHierarchy,
    PrefabObjectPatch,
    PrefabComponentPatch,
    AssetSearch,
    AssetInspect,
    AssetSubassets,
    AssetMeshStats,
    SceneColliderAdd,
    PrefabCreate,
    GenSubmit,
    GenStatus,
    GenFetch,
    GenEnsureBudget,
    CharacterWire,
    SpriteSlice,
    SpriteWire,
    StyleAnchorList,
    StyleAnchorUpsert,
    GenCandidates,
    GenCandidateChoose,
    GenCandidateDiscard,
    AssetImport,
    AssetMove,
    AssetDelete,
    MaterialInspect,
    MaterialPatch,
    AssetDataInspect,
    AssetDataPatch,
    VolumeInspect,
    VolumePatch,
    ShaderGraphInspect,
    ShaderGraphPatch,
    ShaderGraphCompile,
    VfxGraphInspect,
    VfxGraphPatch,
    VfxGraphCompile,
    VfxGraphNodes,
    VfxGraphCreate,
    XuiValidate,
    XuiInspect,
    XuiPatch,
    XuiScaffold,
    XuiI18nExtract,
    PsdInspect,
    PsdRender,
    XuiFromPsd,
    PxlValidate,
    PxlRender,
    SceneSettingsQuery,
    SceneSettingsPatch,
    CodeSymbols,
    FindInFile,
    ScriptCreate,
    ScriptDelete,
    ScriptApplyEdits,
    BuildProject,
    RunHeadless,
    RunTests,
    WorkflowList,
    WorkflowRun,
    SceneObjectPatch,
    SceneObjectCreate,
    SceneObjectDelete,
    SceneComponentAdd,
    AnimatorComponentAdd,
    SceneComponentRemove,
    SceneComponentPatch,
    AnimatorInspect,
    AnimatorValidate,
    AnimatorStateCreate,
    AnimatorStatePatch,
    AnimatorStateDelete,
    AnimatorTransitionCreate,
    AnimatorTransitionPatch,
    AnimatorTransitionDelete,
    AnimatorTransitionReorder,
    AnimatorParameterAdd,
    AnimatorParameterRemove,
    AnimatorLayerAdd,
    AnimatorLayerPatch,
    AnimatorStateMachineCreate,
    AnimatorStateMachineDelete,
    AnimatorStateMachineMove,
    ClipInspect,
    ClipRetarget,
    ClipPatch,
    ClipCurvePatch,
    ClipEventAdd,
    ClipEventPatch,
    ClipEventRemove,
    BlendTreeInspect,
    BlendTreePatch,
    OverrideControllerInspect,
    OverrideControllerPatch,
    AvatarMaskInspect,
    AvatarMaskPatch,
    NavInspect,
    NavQueryPath,
    NavExport,
    NavSettingsPatch,
    JournalQuery,
    OperationGet,
    // ★ P1-1: OperationCancel removed from the CLI surface — it threw NotImplementedException,
    // which misled the AI into thinking it could cancel a running op. Operations run inline in
    // this Host (XEngine has no Domain Reload, so no long-lived ops to cancel). If a future
    // long-running op lands, re-add with a real implementation.
    Call,
    SpineGenSlice,
    SpineGenFill,
    SpineGenRig,
    SpineGenSkin,
    SpineGenAnimate,
    HarnessInstall,
    HarnessStatus,
    HarnessUpgrade,
    ThreeAuthoring,
}

internal static class HostCommandParser
{
    public static HostCommand Parse(IReadOnlyList<string> args)
    {
        for (int index = 0; index < args.Count; ++index)
        {
            // Skip the --project/-p, --edition and --engine-root values (consumed by their
            // respective parsers, never a command).
            if (string.Equals(args[index], "--project", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[index], "-p", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[index], "--edition", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[index], "--engine-root", StringComparison.OrdinalIgnoreCase))
            {
                ++index;
                continue;
            }

            if (args[index].StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            return args[index].ToLowerInvariant() switch
            {
                "serve" => HostCommand.Serve,
                "health" => HostCommand.Health,
                "inspect" => HostCommand.Inspect,
                "capabilities" => HostCommand.Capabilities,
                "project-roots" => HostCommand.ProjectRoots,
                "doctor" => HostCommand.Doctor,
                "package-graph" => HostCommand.PackageGraph,
                "package-search" => HostCommand.PackageSearch,
                "package-info" => HostCommand.PackageInfo,
                "package-install" => HostCommand.PackageInstall,
                "package-update" => HostCommand.PackageUpdate,
                "package-publish" => HostCommand.PackagePublish,
                "scene-list" => HostCommand.SceneList,
                "scene-hierarchy" => HostCommand.SceneHierarchy,
                "prefab-hierarchy" => HostCommand.PrefabHierarchy,
                "prefab-object-patch" => HostCommand.PrefabObjectPatch,
                "prefab-component-patch" => HostCommand.PrefabComponentPatch,
                "asset-search" => HostCommand.AssetSearch,
                "asset-inspect" => HostCommand.AssetInspect,
                "asset-subassets" => HostCommand.AssetSubassets,
                "asset-mesh-stats" => HostCommand.AssetMeshStats,
                "scene-collider-add" => HostCommand.SceneColliderAdd,
                "prefab-create" => HostCommand.PrefabCreate,
                "gen-submit" => HostCommand.GenSubmit,
                "gen-status" => HostCommand.GenStatus,
                "gen-fetch" => HostCommand.GenFetch,
                "gen-ensure-budget" => HostCommand.GenEnsureBudget,
                "character-wire" => HostCommand.CharacterWire,
                "sprite-slice" => HostCommand.SpriteSlice,
                "sprite-wire" => HostCommand.SpriteWire,
                "style-anchor-list" => HostCommand.StyleAnchorList,
                "style-anchor-upsert" => HostCommand.StyleAnchorUpsert,
                "gen-candidates" => HostCommand.GenCandidates,
                "gen-candidate-choose" => HostCommand.GenCandidateChoose,
                "gen-candidate-discard" => HostCommand.GenCandidateDiscard,
                "asset-import" => HostCommand.AssetImport,
                "asset-move" => HostCommand.AssetMove,
                "asset-delete" => HostCommand.AssetDelete,
                "material-inspect" => HostCommand.MaterialInspect,
                "material-patch" => HostCommand.MaterialPatch,
                "asset-data-inspect" => HostCommand.AssetDataInspect,
                "asset-data-patch" => HostCommand.AssetDataPatch,
                "volume-inspect" => HostCommand.VolumeInspect,
                "volume-patch" => HostCommand.VolumePatch,
                "shadergraph-inspect" => HostCommand.ShaderGraphInspect,
                "shadergraph-patch" => HostCommand.ShaderGraphPatch,
                "shadergraph-compile" => HostCommand.ShaderGraphCompile,
                "vfxgraph-inspect" => HostCommand.VfxGraphInspect,
                "vfxgraph-patch" => HostCommand.VfxGraphPatch,
                "vfxgraph-compile" => HostCommand.VfxGraphCompile,
                "vfxgraph-nodes" => HostCommand.VfxGraphNodes,
                "vfxgraph-create" => HostCommand.VfxGraphCreate,
                "xui-validate" => HostCommand.XuiValidate,
                "xui-inspect" => HostCommand.XuiInspect,
                "xui-patch" => HostCommand.XuiPatch,
                "xui-scaffold" => HostCommand.XuiScaffold,
                "xui-i18n-extract" => HostCommand.XuiI18nExtract,
                "psd-inspect" => HostCommand.PsdInspect,
                "psd-render" => HostCommand.PsdRender,
                "xui-from-psd" => HostCommand.XuiFromPsd,
                "pxl-validate" => HostCommand.PxlValidate,
                "pxl-render" => HostCommand.PxlRender,
                "scene-settings-query" => HostCommand.SceneSettingsQuery,
                "scene-settings-patch" => HostCommand.SceneSettingsPatch,
                "code-symbols" => HostCommand.CodeSymbols,
                "find-in-file" => HostCommand.FindInFile,
                "script-create" => HostCommand.ScriptCreate,
                "script-delete" => HostCommand.ScriptDelete,
                "script-apply-edits" => HostCommand.ScriptApplyEdits,
                "build" => HostCommand.BuildProject,
                "run-headless" => HostCommand.RunHeadless,
                "run-tests" => HostCommand.RunTests,
                "workflow-list" => HostCommand.WorkflowList,
                "workflow-run" => HostCommand.WorkflowRun,
                "spinegen-slice" => HostCommand.SpineGenSlice,
                "spinegen-fill" => HostCommand.SpineGenFill,
                "spinegen-rig" => HostCommand.SpineGenRig,
                "spinegen-skin" => HostCommand.SpineGenSkin,
                "spinegen-animate" => HostCommand.SpineGenAnimate,
                "scene-object-patch" => HostCommand.SceneObjectPatch,
                "scene-object-create" => HostCommand.SceneObjectCreate,
                "scene-object-delete" => HostCommand.SceneObjectDelete,
                "scene-component-add" => HostCommand.SceneComponentAdd,
                "animator-component-add" => HostCommand.AnimatorComponentAdd,
                "animator-inspect" => HostCommand.AnimatorInspect,
                "animator-validate" => HostCommand.AnimatorValidate,
                "animator-state-create" => HostCommand.AnimatorStateCreate,
                "animator-state-patch" => HostCommand.AnimatorStatePatch,
                "animator-state-delete" => HostCommand.AnimatorStateDelete,
                "animator-transition-create" => HostCommand.AnimatorTransitionCreate,
                "animator-transition-patch" => HostCommand.AnimatorTransitionPatch,
                "animator-transition-delete" => HostCommand.AnimatorTransitionDelete,
                "animator-transition-reorder" => HostCommand.AnimatorTransitionReorder,
                "animator-parameter-add" => HostCommand.AnimatorParameterAdd,
                "animator-parameter-remove" => HostCommand.AnimatorParameterRemove,
                "animator-layer-add" => HostCommand.AnimatorLayerAdd,
                "animator-layer-patch" => HostCommand.AnimatorLayerPatch,
                "animator-statemachine-create" => HostCommand.AnimatorStateMachineCreate,
                "animator-statemachine-delete" => HostCommand.AnimatorStateMachineDelete,
                "animator-statemachine-move" => HostCommand.AnimatorStateMachineMove,
                "clip-inspect" => HostCommand.ClipInspect,
                "clip-retarget" => HostCommand.ClipRetarget,
                "clip-patch" => HostCommand.ClipPatch,
                "clip-curve-patch" => HostCommand.ClipCurvePatch,
                "clip-event-add" => HostCommand.ClipEventAdd,
                "clip-event-patch" => HostCommand.ClipEventPatch,
                "clip-event-remove" => HostCommand.ClipEventRemove,
                "blendtree-inspect" => HostCommand.BlendTreeInspect,
                "blendtree-patch" => HostCommand.BlendTreePatch,
                "overridecontroller-inspect" => HostCommand.OverrideControllerInspect,
                "overridecontroller-patch" => HostCommand.OverrideControllerPatch,
                "avatarmask-inspect" => HostCommand.AvatarMaskInspect,
                "avatarmask-patch" => HostCommand.AvatarMaskPatch,
                "nav-inspect" => HostCommand.NavInspect,
                "nav-query-path" => HostCommand.NavQueryPath,
                "nav-export" => HostCommand.NavExport,
                "nav-settings-patch" => HostCommand.NavSettingsPatch,
                "scene-component-remove" => HostCommand.SceneComponentRemove,
                "scene-component-patch" => HostCommand.SceneComponentPatch,
                "journal-query" => HostCommand.JournalQuery,
                "operation-get" => HostCommand.OperationGet,
                // operation-cancel intentionally absent (P1-1): not implemented, removed to avoid
                // advertising a capability the Host can't honor.
                "call" => HostCommand.Call,
                "harness-install" => HostCommand.HarnessInstall,
                "harness-status" => HostCommand.HarnessStatus,
                "harness-upgrade" => HostCommand.HarnessUpgrade,
                "three-authoring" => HostCommand.ThreeAuthoring,
                _ => throw new ArgumentException($"Unknown xengine-cli command: {args[index]}"),
            };
        }

        // No command = run as MCP stdio server (so `dotnet xengine-cli.dll --project X` serves).
        return HostCommand.Serve;
    }
}
