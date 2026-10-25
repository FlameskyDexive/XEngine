using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;
using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>★ M31 — Offline animator / clip / blendtree MCP tools (journaled writes, hashed reads).</summary>
[McpServerToolType]
public static class AnimationTools
{
    [McpServerTool(Name = "animator_inspect", UseStructuredContent = true)]
    [Description("Read a .controller: layers, states, transitions, conditions, parameters, BlendTree summaries, and contentHash for drift-protected writes.")]
    public static AnimatorInspectResult InspectAnimator(
        IServiceProvider services,
        [Description("Project-relative .controller path (e.g. Assets/Hero.controller).")] string controllerPath)
    {
        string abs = Resolve(services, controllerPath);
        return AnimationAssetOps.InspectController(File.ReadAllText(abs), controllerPath);
    }

    [McpServerTool(Name = "animator_validate", UseStructuredContent = true)]
    [Description("Validate a .controller: unreachable states, missing motion, missing parameters, dead transitions, Solo/Mute shadows, zero-weight layers.")]
    public static AnimatorValidateResult ValidateAnimator(
        IServiceProvider services,
        [Description("Project-relative .controller path (e.g. Assets/Hero.controller).")] string controllerPath)
    {
        string abs = Resolve(services, controllerPath);
        return AnimationAssetOps.ValidateController(File.ReadAllText(abs), controllerPath);
    }

    [McpServerTool(Name = "animator_state_create", UseStructuredContent = true)]
    [Description("Create a state on a .controller (idempotent by name). Optional clipGuid sets Motion. Journaled.")]
    public static Task<SceneWriteResult> CreateState(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null, string? layerName = null, string? clipGuid = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-state-create", $"Created state '{name}'.",
            root => AnimationAssetOps.CreateState(root, name, layerName, clipGuid), cancellationToken);

    [McpServerTool(Name = "animator_state_patch", UseStructuredContent = true)]
    [Description("Patch fields on a named state (Speed, Tag, IsLooping, PosX/PosY, Motion/AssetID). Journaled.")]
    public static Task<SceneWriteResult> PatchState(
        IServiceProvider services,
        string controllerPath, string name, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null, string? layerName = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-state-patch", $"Patched state '{name}'.",
            root => AnimationAssetOps.PatchState(root, name, patches, layerName), cancellationToken);

    [McpServerTool(Name = "animator_state_delete", UseStructuredContent = true)]
    [Description("Delete a named state and transitions that target it. Journaled.")]
    public static Task<SceneWriteResult> DeleteState(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null, string? layerName = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-state-delete", $"Deleted state '{name}'.",
            root => AnimationAssetOps.DeleteState(root, name, layerName), cancellationToken);

    [McpServerTool(Name = "animator_transition_create", UseStructuredContent = true)]
    [Description("Create a transition from one state to another. Idempotent by transition name. Journaled.")]
    public static Task<SceneWriteResult> CreateTransition(
        IServiceProvider services,
        string controllerPath, string fromState, string toState, string idempotencyKey,
        string? name = null, string? intent = null, string? layerName = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-transition-create", $"Created transition {fromState} → {toState}.",
            root => AnimationAssetOps.CreateTransition(root, fromState, toState, name, layerName), cancellationToken);

    [McpServerTool(Name = "animator_transition_patch", UseStructuredContent = true)]
    [Description("Patch a named transition (Duration, HasExitTime, ExitTime, Offset, DestinationStateName, Solo, Mute, HasFixedDuration, InterruptionSource, OrderedInterruption, CanTransitionToSelf). Journaled.")]
    public static Task<SceneWriteResult> PatchTransition(
        IServiceProvider services,
        string controllerPath, string name, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null, string? layerName = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-transition-patch", $"Patched transition '{name}'.",
            root => AnimationAssetOps.PatchTransition(root, name, patches, layerName), cancellationToken);

    [McpServerTool(Name = "animator_transition_delete", UseStructuredContent = true)]
    [Description("Delete a named transition. Journaled.")]
    public static Task<SceneWriteResult> DeleteTransition(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null, string? layerName = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-transition-delete", $"Deleted transition '{name}'.",
            root => AnimationAssetOps.DeleteTransition(root, name, layerName), cancellationToken);

    [McpServerTool(Name = "animator_transition_reorder", UseStructuredContent = true)]
    [Description("Replace a state's TransitionNames order (runtime priority). Journaled.")]
    public static Task<SceneWriteResult> ReorderTransitions(
        IServiceProvider services,
        string controllerPath, string stateName, string[] names, string idempotencyKey,
        string? intent = null, string? layerName = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-transition-reorder", $"Reordered transitions on '{stateName}'.",
            root => AnimationAssetOps.ReorderStateTransitions(root, stateName, names, layerName), cancellationToken);

    [McpServerTool(Name = "animator_parameter_add", UseStructuredContent = true)]
    [Description("Add a controller parameter. type is Float, Int, Bool, or Trigger. Idempotent by name. Journaled.")]
    public static Task<SceneWriteResult> AddParameter(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string type = "Float", string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-parameter-add", $"Added parameter '{name}'.",
            root => AnimationAssetOps.AddParameter(root, name, type), cancellationToken);

    [McpServerTool(Name = "animator_parameter_remove", UseStructuredContent = true)]
    [Description("Remove a controller parameter by name. Journaled.")]
    public static Task<SceneWriteResult> RemoveParameter(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-parameter-remove", $"Removed parameter '{name}'.",
            root => AnimationAssetOps.RemoveParameter(root, name), cancellationToken);

    [McpServerTool(Name = "animator_layer_add", UseStructuredContent = true)]
    [Description("Add a mixer layer to a .controller. Idempotent by name. Journaled.")]
    public static Task<SceneWriteResult> AddLayer(
        IServiceProvider services,
        string controllerPath, string name, string idempotencyKey,
        string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-layer-add", $"Added layer '{name}'.",
            root => AnimationAssetOps.AddLayer(root, name), cancellationToken);

    [McpServerTool(Name = "animator_layer_patch", UseStructuredContent = true)]
    [Description("Patch a named layer (Weight, Blending, DefaultStateName). Journaled.")]
    public static Task<SceneWriteResult> PatchLayer(
        IServiceProvider services,
        string controllerPath, string name, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, controllerPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.animator-layer-patch", $"Patched layer '{name}'.",
            root => AnimationAssetOps.PatchLayer(root, name, patches), cancellationToken);

    [McpServerTool(Name = "clip_inspect", UseStructuredContent = true)]
    [Description("Read a .anim: bindings (bone/channel, key count, value range), generic TypeName/Property tracks, sample rate, duration. Optional scenePath+objectIdentifier lists clip GUIDs on that object's Animator/AnimationComponent (the M30 root).")]
    public static ClipInspectResult InspectClip(
        IServiceProvider services,
        [Description("Project-relative .anim path.")] string clipPath,
        [Description("Optional scene path used with objectIdentifier to list clips on a scene root.")] string? scenePath = null,
        [Description("Optional GameObject Identifier (the animation root).")] string? objectIdentifier = null)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        string abs = project.ResolveAssetPath(clipPath);
        System.Collections.Generic.IReadOnlyList<string>? guids = null;
        if (!string.IsNullOrEmpty(scenePath) && !string.IsNullOrEmpty(objectIdentifier))
        {
            string sceneAbs = project.ResolveAssetPath(scenePath);
            guids = AnimationAssetOps.CollectRootClipGuids(File.ReadAllText(sceneAbs), objectIdentifier);
        }
        return AnimationAssetOps.InspectClip(File.ReadAllText(abs), clipPath, guids);
    }

    [McpServerTool(Name = "clip_retarget", UseStructuredContent = true)]
    [Description("Remap .anim bone names onto a humanoid vocabulary (Unity canonical, or names from --like another clip that already plays on the dest rig). Mixamo/VRM aliases resolve. Curves stay local TRS. Journaled.")]
    public static Task<SceneWriteResult> RetargetClip(
        IServiceProvider services,
        [Description("Project-relative .anim path to rewrite.")] string clipPath,
        string idempotencyKey,
        [Description("Optional .anim whose bone names are the destination vocabulary.")] string? likeClipPath = null,
        string? intent = null, string? expectedBeforeHash = null, bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        EchoObject? like = null;
        if (!string.IsNullOrWhiteSpace(likeClipPath))
        {
            string likeAbs = Resolve(services, likeClipPath);
            like = EchoObject.ReadFromString(File.ReadAllText(likeAbs));
        }

        return Mutate(services, clipPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.clip-retarget", "Retargeted humanoid bone names.",
            root => { AnimationAssetOps.RetargetClip(root, like); }, cancellationToken);
    }

    [McpServerTool(Name = "clip_patch", UseStructuredContent = true)]
    [Description("Patch clip fields: TicksPerSecond (alias SampleRate), Duration, Wrap, Name, RootBonePath. Journaled.")]
    public static Task<SceneWriteResult> PatchClip(
        IServiceProvider services,
        string clipPath, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, clipPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.clip-patch", "Patched clip fields.",
            root => AnimationAssetOps.PatchClip(root, patches), cancellationToken);

    [McpServerTool(Name = "clip_curve_patch", UseStructuredContent = true)]
    [Description("Add, set, or remove a keyframe on a clip curve. channel is PosX/Y/Z, RotX/Y/Z/W, or ScaleX/Y/Z. When typeName is set, bone is a GameObject path (empty = root) and channel is the float property. op is add|set|remove. Journaled.")]
    public static Task<SceneWriteResult> PatchCurve(
        IServiceProvider services,
        string clipPath, string bone, string channel, string op, float time, string idempotencyKey,
        float? value = null, string? typeName = null, string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, clipPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.clip-curve-patch",
            string.IsNullOrEmpty(typeName) ? $"{op} key {bone}/{channel} @ {time}." : $"{op} key {bone}/{typeName}.{channel} @ {time}.",
            root => AnimationAssetOps.PatchCurve(root, bone, channel, op, time, value, typeName), cancellationToken);

    [McpServerTool(Name = "clip_event_add", UseStructuredContent = true)]
    [Description("Add an authored animation event (time in seconds + functionName + optional float/int/string/objectGuid params). Journaled.")]
    public static Task<SceneWriteResult> AddClipEvent(
        IServiceProvider services,
        string clipPath, float time, string functionName, string idempotencyKey,
        float floatParam = 0f, int intParam = 0, string? stringParam = null, string? objectGuid = null,
        string? intent = null, string? expectedBeforeHash = null, bool dryRun = false,
        CancellationToken cancellationToken = default)
        => Mutate(services, clipPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.clip-event-add", $"Added event '{functionName}' @ {time}.",
            root => AnimationAssetOps.AddClipEvent(root, time, functionName, floatParam, intParam, stringParam, objectGuid),
            cancellationToken);

    [McpServerTool(Name = "clip_event_patch", UseStructuredContent = true)]
    [Description("Patch an authored clip event by index (Time, FunctionName, FloatParam, IntParam, StringParam, ObjectParam). Journaled.")]
    public static Task<SceneWriteResult> PatchClipEvent(
        IServiceProvider services,
        string clipPath, int index, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null, string? expectedBeforeHash = null, bool dryRun = false,
        CancellationToken cancellationToken = default)
        => Mutate(services, clipPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.clip-event-patch", $"Patched event [{index}].",
            root => AnimationAssetOps.PatchClipEvent(root, index, patches), cancellationToken);

    [McpServerTool(Name = "clip_event_remove", UseStructuredContent = true)]
    [Description("Remove an authored clip event by index. Journaled.")]
    public static Task<SceneWriteResult> RemoveClipEvent(
        IServiceProvider services,
        string clipPath, int index, string idempotencyKey,
        string? intent = null, string? expectedBeforeHash = null, bool dryRun = false,
        CancellationToken cancellationToken = default)
        => Mutate(services, clipPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.clip-event-remove", $"Removed event [{index}].",
            root => AnimationAssetOps.RemoveClipEvent(root, index), cancellationToken);

    [McpServerTool(Name = "blendtree_inspect", UseStructuredContent = true)]
    [Description("Read a .motion BlendTree, or an embedded BlendTree on a .controller state (pass stateName).")]
    public static BlendTreeInspectResult InspectBlendTree(
        IServiceProvider services,
        [Description("Project-relative .motion or .controller path.")] string assetPath,
        [Description("Required when assetPath is a .controller: the state that owns the BlendTree.")] string? stateName = null)
    {
        string abs = Resolve(services, assetPath);
        return AnimationAssetOps.InspectBlendTree(File.ReadAllText(abs), assetPath, stateName);
    }

    [McpServerTool(Name = "blendtree_patch", UseStructuredContent = true)]
    [Description("Patch BlendTree fields (Type, ParameterName, ParameterNameY, UseAutomaticThresholds, Children/i/Threshold|Position|DirectParameterName|Tree). On a .controller, pass stateName. Journaled.")]
    public static Task<SceneWriteResult> PatchBlendTree(
        IServiceProvider services,
        string assetPath, SceneFieldPatch[] patches, string idempotencyKey,
        string? stateName = null, string? intent = null,
        string? expectedBeforeHash = null, bool dryRun = false, CancellationToken cancellationToken = default)
        => Mutate(services, assetPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.blendtree-patch", "Patched BlendTree.",
            root => AnimationAssetOps.PatchBlendTree(root, stateName, patches), cancellationToken);

    [McpServerTool(Name = "overridecontroller_inspect", UseStructuredContent = true)]
    [Description("Read a .overridecontroller: base GUID and From/To clip mapping, plus contentHash.")]
    public static OverrideControllerInspectResult InspectOverrideController(
        IServiceProvider services,
        [Description("Project-relative .overridecontroller path.")] string assetPath)
    {
        string abs = Resolve(services, assetPath);
        return AnimationAssetOps.InspectOverrideController(File.ReadAllText(abs), assetPath);
    }

    [McpServerTool(Name = "overridecontroller_patch", UseStructuredContent = true)]
    [Description("Patch AnimatorOverrideController fields (Base, Overrides/i/From|To). Journaled.")]
    public static Task<SceneWriteResult> PatchOverrideController(
        IServiceProvider services,
        string assetPath, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null, string? expectedBeforeHash = null, bool dryRun = false,
        CancellationToken cancellationToken = default)
        => Mutate(services, assetPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.overridecontroller-patch", "Patched AnimatorOverrideController.",
            root => AnimationAssetOps.PatchOverrideController(root, patches), cancellationToken);

    [McpServerTool(Name = "avatarmask_inspect", UseStructuredContent = true)]
    [Description("Read a .mask: bone entries, humanoid part toggles, and contentHash.")]
    public static AvatarMaskInspectResult InspectAvatarMask(
        IServiceProvider services,
        [Description("Project-relative .mask path.")] string assetPath)
    {
        string abs = Resolve(services, assetPath);
        return AnimationAssetOps.InspectAvatarMask(File.ReadAllText(abs), assetPath);
    }

    [McpServerTool(Name = "avatarmask_patch", UseStructuredContent = true)]
    [Description("Patch AvatarMask fields (Entries/i/Path|Weight, HumanoidParts/i). Journaled.")]
    public static Task<SceneWriteResult> PatchAvatarMask(
        IServiceProvider services,
        string assetPath, SceneFieldPatch[] patches, string idempotencyKey,
        string? intent = null, string? expectedBeforeHash = null, bool dryRun = false,
        CancellationToken cancellationToken = default)
        => Mutate(services, assetPath, idempotencyKey, intent, expectedBeforeHash, dryRun,
            "xengine-cli.avatarmask-patch", "Patched AvatarMask.",
            root => AnimationAssetOps.PatchAvatarMask(root, patches), cancellationToken);

    static string Resolve(IServiceProvider services, string path)
        => services.GetRequiredService<ProjectDescriptor>().ResolveAssetPath(path);

    static Task<SceneWriteResult> Mutate(
        IServiceProvider services, string assetPath, string idempotencyKey, string? intent,
        string? expectedBeforeHash, bool dryRun, string tool, string summary,
        Action<EchoObject> mutate, CancellationToken cancellationToken)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        var writer = services.GetRequiredService<SceneWriter>();
        return writer.MutateAssetDataAsync(
            project.ResolveAssetPath(assetPath), assetPath, mutate, summary,
            XEngine.Data.Provenance.ChangeProvenance.ForTool(tool, intent, idempotencyKey),
            dryRun, expectedBeforeHash, cancellationToken);
    }
}
