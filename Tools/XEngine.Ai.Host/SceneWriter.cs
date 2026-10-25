using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;
// The Protocol layer also has a ChangeProvenance twin; alias to the data-layer one which
// ProvenancedDelta/ChangeJournal actually consume.
using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B2) — Offline scene mutator. The Host reads a .scene's raw Echo tree, applies an edit
/// (patch/create/delete), generates a provenance-bearing delta via <see cref="ProvenancedDelta"/>,
/// writes the tree back, and appends the change to the project journal — the SAME journal the editor
/// writes (Layer 0 A2). So an AI-driven edit is traceable end-to-end, identical in shape to an editor
/// edit. No engine runtime is loaded.
///
/// <para>Every operation supports <c>dryRun</c> (compute + report the delta without writing) and is
/// idempotent by the supplied idempotency key recorded in provenance.</para>
/// </summary>
public sealed class SceneWriter
{
    private readonly ChangeJournal _journal;

    public SceneWriter(ChangeJournal journal)
    {
        _journal = journal;
    }

    /// <summary>
    /// Apply field patches to a GameObject identified by <paramref name="objectIdentifier"/>. Patches
    /// are JSON-pointer paths into the GO's Echo tree (e.g. "Transform/_localPosition/Y").
    /// </summary>
    public async Task<SceneWriteResult> PatchAsync(
        string absolutePath,
        string targetAsset,
        string objectIdentifier,
        IReadOnlyList<SceneFieldPatch> patches,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, string summary) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                EchoObject? go = EchoFacade.FindGameObject(root, objectIdentifier)
                    ?? throw new KeyNotFoundException(
                        $"GameObject '{objectIdentifier}' not found in {absolutePath}. " +
                        "Call scene_hierarchy first to list valid Identifiers.");
                foreach (SceneFieldPatch p in patches)
                {
                    EchoFacade.SetPathValue(go, p.Path, p.Value);
                }
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (patches produced identical state).");
        }

        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
    }

    /// <summary>★ prefab — Patch a GameObject in a .prefab file by Identifier. Uses recursive search
    /// (prefab GOs nest via Children, unlike the scene's flat array). Same delta+provenance+journal path.</summary>
    public async Task<SceneWriteResult> PatchPrefabAsync(
        string absolutePath,
        string targetAsset,
        string objectIdentifier,
        IReadOnlyList<SceneFieldPatch> patches,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, string summary) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                EchoObject? go = EchoFacade.FindGameObjectRecursive(root, objectIdentifier)
                    ?? throw new KeyNotFoundException(
                        $"GameObject '{objectIdentifier}' not found in prefab {absolutePath}. " +
                        "Call prefab_hierarchy first to list valid Identifiers.");
                foreach (SceneFieldPatch p in patches)
                {
                    EchoFacade.SetPathValue(go, p.Path, p.Value);
                }
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (patches produced identical state).");
        }

        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
    }
    public async Task<SceneWriteResult> CreateAsync(
        string absolutePath,
        string targetAsset,
        string name,
        string? parentIdentifier,
        ChangeProvenance provenance,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        string newIdentifier = Guid.NewGuid().ToString();
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                EchoObject goArray = EchoFacade.GetGameObjectArray(root);
                int nextEchoId = EchoFacade.MaxEchoId(root);
                int goEchoId = ++nextEchoId;
                EchoObject newGo = MakeMinimalGameObject(name, newIdentifier, goEchoId, ++nextEchoId);

                if (string.IsNullOrEmpty(parentIdentifier))
                {
                    goArray.ListAdd(newGo);
                }
                else
                {
                    // ★ Gen-P0.2 — editor save format: the child's BODY lives inline under the
                    // parent's Children array; the flat serializeObj.array lists every GO once, so the
                    // child appears there as a {$id,$type} reference stub pointing at the inline body.
                    EchoObject parent = EchoFacade.FindGameObject(root, parentIdentifier)
                        ?? throw new KeyNotFoundException(
                            $"Parent GameObject '{parentIdentifier}' not found in {absolutePath}. " +
                            "Call scene_hierarchy first to list valid Identifiers.");
                    EchoFacade.GetChildrenList(parent).ListAdd(newGo);

                    EchoObject stub = EchoObject.NewCompound();
                    stub["$id"] = new EchoObject(goEchoId);
                    stub["$type"] = new EchoObject(GameObjectType);
                    goArray.ListAdd(stub);
                }

                // Keep _goIdentifiers/_compIdentifiers/_compIdOffsets aligned — the engine restores
                // stable identities from them and skips the restore on any length mismatch.
                EchoFacade.RebuildIdentifierArrays(root);
            });

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);
        string summary = $"Created GameObject '{name}' (Identifier {newIdentifier})" +
                         (string.IsNullOrEmpty(parentIdentifier) ? " at scene root." : $" under '{parentIdentifier}'.");
        SceneWriteResult committed = await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
        // ★ Gen-P1.5 — structured identifier so workflows can chain create → component_add.
        return committed with { CreatedIdentifier = newIdentifier };
    }

    /// <summary>Delete a GameObject (and its inline subtree) by Identifier — root or nested.</summary>
    public async Task<SceneWriteResult> DeleteAsync(
        string absolutePath,
        string targetAsset,
        string objectIdentifier,
        ChangeProvenance provenance,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                if (!EchoFacade.RemoveGameObjectSubtree(root, objectIdentifier))
                {
                    throw new KeyNotFoundException($"GameObject '{objectIdentifier}' not found in {absolutePath}.");
                }
                EchoFacade.RebuildIdentifierArrays(root);
            });

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);
        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            $"Deleted GameObject '{objectIdentifier}'.", beforeHash, afterHash, cancellationToken);
    }

    /// <summary>★ P0-4 — Add a component to a GameObject in a scene. <paramref name="componentType"/>
    /// is resolved via <see cref="ComponentRegistry"/> (short-name like "Camera" or a full $type for
    /// user scripts). Optional <paramref name="initialFields"/> patches set specific fields on the new
    /// node (same path/literal shape as scene_object_patch). Returns the new component's identifier.</summary>
    public async Task<SceneWriteResult> AddComponentAsync(
        string absolutePath,
        string targetAsset,
        string objectIdentifier,
        string componentType,
        IReadOnlyList<SceneFieldPatch>? initialFields,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        string? fullType = ComponentRegistry.ResolveTypeString(componentType)
            ?? throw new ArgumentException(
                $"Unknown component type '{componentType}'. Use a known short-name " +
                $"({string.Join(", ", ComponentRegistry.KnownShortNames)}) or a full assembly-qualified $type " +
                "for user MonoBehaviour scripts.");

        string newComponentId = Guid.NewGuid().ToString();

        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                EchoObject? go = EchoFacade.FindGameObject(root, objectIdentifier)
                    ?? throw new KeyNotFoundException(
                        $"GameObject '{objectIdentifier}' not found in {absolutePath}. " +
                        "Call scene_hierarchy first to list valid Identifiers.");
                EchoObject comps = EchoFacade.GetComponentsList(go);
                EchoObject node = ComponentRegistry.MakeMinimalNode(fullType, newComponentId);
                if (initialFields is not null)
                {
                    foreach (SceneFieldPatch p in initialFields)
                    {
                        EchoFacade.SetPathValue(node, p.Path, p.Value);
                    }
                }
                comps.ListAdd(node);
                // ★ Gen-P0.2 — component count changed: keep the scene's parallel identity arrays
                // aligned or the engine drops ALL stable identities at next load.
                EchoFacade.RebuildIdentifierArrays(root);
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);
        string summary = $"Added component '{componentType}' (id {newComponentId[..8]}…) to GameObject '{objectIdentifier}'.";
        SceneWriteResult committed = await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
        return committed with { CreatedIdentifier = newComponentId };
    }

    /// <summary>★ P0-4 — Remove a component from a GameObject in a scene. Match by
    /// <paramref name="componentIdentifier"/> (exact _identifier GUID) or, if that is null, by the first
    /// component whose short type name equals <paramref name="componentType"/> (case-insensitive).</summary>
    public async Task<SceneWriteResult> RemoveComponentAsync(
        string absolutePath,
        string targetAsset,
        string objectIdentifier,
        string? componentIdentifier,
        string? componentType,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                EchoObject? go = EchoFacade.FindGameObject(root, objectIdentifier)
                    ?? throw new KeyNotFoundException(
                        $"GameObject '{objectIdentifier}' not found in {absolutePath}.");
                EchoObject comps = EchoFacade.GetComponentsList(go);
                RemoveComponent(comps, componentIdentifier, componentType, absolutePath, objectIdentifier);
                EchoFacade.RebuildIdentifierArrays(root);
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);
        string what = componentIdentifier ?? componentType ?? "";
        string summary = $"Removed component '{what}' from GameObject '{objectIdentifier}'.";
        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
    }

    /// <summary>★ Patch fields on an EXISTING component of a GameObject in a scene. Locates the
    /// component by <paramref name="componentIdentifier"/> (exact _identifier GUID, preferred) or by
    /// <paramref name="componentType"/> (short type name, first match), then applies the patches to
    /// THAT component node. Patch paths are relative to the component root (e.g. "FieldOfView",
    /// "ClearColor/R", "NearClipPlane") — no "Components/0/" prefix needed. This is the stable way to
    /// edit a component's fields: it doesn't depend on array index (which concurrent edits can shift).
    /// Supports <paramref name="enable"/>/​<paramref name="disable"/> toggles for the _enabled flag.</summary>
    public async Task<SceneWriteResult> PatchComponentAsync(
        string absolutePath,
        string targetAsset,
        string objectIdentifier,
        string? componentIdentifier,
        string? componentType,
        IReadOnlyList<SceneFieldPatch> patches,
        bool? enable,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                // ★ recursive=true for prefabs: GOs nest via Children, so search recursively.
                // For scenes (default), the flat serializeObj.array is searched directly.
                EchoObject? go = recursive
                    ? EchoFacade.FindGameObjectRecursive(root, objectIdentifier)
                    : EchoFacade.FindGameObject(root, objectIdentifier);
                if (go is null)
                {
                    throw new KeyNotFoundException(
                        $"GameObject '{objectIdentifier}' not found in {absolutePath}. " +
                        $"Call {(recursive ? "prefab_hierarchy" : "scene_hierarchy")} first to list valid Identifiers.");
                }
                EchoObject comps = EchoFacade.GetComponentsList(go);
                int idx = FindComponentIndex(comps, componentIdentifier, componentType, absolutePath, objectIdentifier);
                EchoObject comp = comps.List[idx];

                // Apply field patches relative to the component root.
                foreach (SceneFieldPatch p in patches)
                {
                    EchoFacade.SetPathValue(comp, p.Path, p.Value);
                }
                // Convenience: enable/disable toggle without a manual patch.
                if (enable.HasValue)
                {
                    comp["_enabled"] = new EchoObject(enable.Value);
                }
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (patches produced identical state).");
        }

        string what = componentIdentifier ?? componentType ?? "";
        string summary = $"Patched component '{what}' on GameObject '{objectIdentifier}' ({patches.Count} field(s)";
        if (enable.HasValue) summary += $", _enabled={enable.Value}";
        summary += ").";
        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
    }

    // ---- helpers ----

    /// <summary>Find a component's index in the Components list by _identifier (GUID) or short type
    /// name (first match). Shared by PatchComponentAsync and RemoveComponent.</summary>
    private static int FindComponentIndex(
        EchoObject components, string? componentIdentifier, string? componentType,
        string path, string objectIdentifier)
    {
        for (int i = 0; i < components.List.Count; i++)
        {
            EchoObject comp = components.List[i];
            if (componentIdentifier is not null)
            {
                // ★ Gen-P0.2 — _identifier is Echo's compact Guid form {$t:"g",$v:"…"} in editor-saved
                // scenes (plain string in legacy offline writes); match both.
                if (EchoFacade.ReadComponentIdentifier(comp) == componentIdentifier)
                {
                    return i;
                }
            }
            else if (componentType is not null)
            {
                string? fullType = comp.TryGet("$type", out EchoObject? t) ? t!.StringValue : null;
                string shortType = ShortComponentName(fullType);
                if (string.Equals(shortType, componentType, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        string what = componentIdentifier ?? componentType ?? "(no match key)";
        throw new KeyNotFoundException(
            $"Component '{what}' not found on GameObject '{objectIdentifier}' in {path}. " +
            "Call scene_hierarchy to list component identifiers and types.");
    }

    private static void RemoveComponent(
        EchoObject components, string? componentIdentifier, string? componentType,
        string path, string objectIdentifier)
    {
        int idx = FindComponentIndex(components, componentIdentifier, componentType, path, objectIdentifier);
        components.ListRemoveAt(idx);
    }

    private static string ShortComponentName(string? fullType)
    {
        if (string.IsNullOrEmpty(fullType)) return "";
        int comma = fullType.IndexOf(',');
        string name = comma > 0 ? fullType[..comma].Trim() : fullType;
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[(lastDot + 1)..] : name;
    }

    // ---- ★ P0-6 material tools ----

    /// <summary>★ P0-6 — Patch a .mat file's shader reference or properties. Patches are slash-path
    /// pointers into the .mat Echo tree, e.g. "_shader/AssetID", "_properties/_floats/_WindStrength",
    /// "_properties/_colors/_MainColor/R", "_properties/_textures/_MainTex/AssetID". Same
    /// delta+provenance+journal path as scene writes; the .mat root is a single Material node.</summary>
    public async Task<SceneWriteResult> PatchMaterialAsync(
        string absolutePath,
        string targetAsset,
        IReadOnlyList<SceneFieldPatch> patches,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, string summary) = ReadCloneAndMutate(
            absolutePath, mat =>
            {
                foreach (SceneFieldPatch p in patches)
                {
                    EchoFacade.SetPathValue(mat, p.Path, p.Value);
                }
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (patches produced identical state).");
        }

        string detail = string.IsNullOrEmpty(summary)
            ? $"Patched material {targetAsset} ({patches.Count} field(s))."
            : summary;
        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            detail, beforeHash, afterHash, cancellationToken);
    }

    /// <summary>★ P2-2 — Patch a scene's top-level settings (Fog/Ambient/Skybox). Patches are slash-paths
    /// into the scene root, e.g. "Fog/Mode", "Ambient/Color/R", "Skybox/SolidColor/G". The scene root
    /// is the node that holds serializeObj AND these settings fields. Same delta+provenance+journal path.</summary>
    public async Task<SceneWriteResult> PatchSceneSettingsAsync(
        string absolutePath,
        string targetAsset,
        IReadOnlyList<SceneFieldPatch> patches,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, sceneRoot =>
            {
                foreach (SceneFieldPatch p in patches)
                {
                    EchoFacade.SetPathValue(sceneRoot, p.Path, p.Value);
                }
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (patches produced identical state).");
        }

        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            $"Patched scene settings {targetAsset} ({patches.Count} field(s)).",
            beforeHash, afterHash, cancellationToken);
    }

    /// <summary>★ C1 — Patch a generic .asset file (ScriptableObject instance). Patches are slash-paths
    /// into the .asset root, e.g. "MaxHealth", "Damage/Fire", "Costs/$values/0". Same mechanism as
    /// material_patch / scene_settings_patch — drift-protected, journaled, idempotent.</summary>
    public async Task<SceneWriteResult> PatchAssetDataAsync(
        string absolutePath,
        string targetAsset,
        IReadOnlyList<SceneFieldPatch> patches,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(
            absolutePath, root =>
            {
                foreach (SceneFieldPatch p in patches)
                {
                    EchoFacade.SetPathValue(root, p.Path, p.Value);
                }
            }, expectedBeforeHash);

        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (patches produced identical state).");
        }

        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            $"Patched asset data {targetAsset} ({patches.Count} field(s)).",
            beforeHash, afterHash, cancellationToken);
    }

    /// <summary>★ M31 — Apply an arbitrary Echo mutation to a data asset (.controller / .anim / .motion)
    /// with the same drift / idempotency / journal path as <see cref="PatchAssetDataAsync"/>.</summary>
    public async Task<SceneWriteResult> MutateAssetDataAsync(
        string absolutePath,
        string targetAsset,
        Action<EchoObject> mutate,
        string summary,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash = null,
        CancellationToken cancellationToken = default)
    {
        (EchoObject before, EchoObject after, _) = ReadCloneAndMutate(absolutePath, mutate, expectedBeforeHash);
        string beforeHash = ProvenancedDelta.ContentHash(before);
        string afterHash = ProvenancedDelta.ContentHash(after);
        if (beforeHash == afterHash)
        {
            return new SceneWriteResult("", targetAsset, beforeHash, afterHash, dryRun,
                "No change (mutation produced identical state).");
        }

        return await CommitAsync(before, after, absolutePath, targetAsset, provenance, dryRun,
            summary, beforeHash, afterHash, cancellationToken);
    }

    private static (EchoObject before, EchoObject after, string summary) ReadCloneAndMutate(
        string absolutePath, Action<EchoObject> mutate, string? expectedBeforeHash = null)
    {
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"File not found: {absolutePath}. Call scene_hierarchy or prefab_hierarchy first to verify the path.");
        }

        string text = File.ReadAllText(absolutePath);
        EchoObject before = EchoObject.ReadFromString(text);

        // ★ P0-2: Drift guard. If the caller supplied an expected hash, verify the current file
        // matches BEFORE applying the mutation. A mismatch means the file was edited since the
        // caller last read it (e.g. a human edited the scene in the editor) — refuse to clobber.
        if (!string.IsNullOrEmpty(expectedBeforeHash))
        {
            string actualHash = ProvenancedDelta.ContentHash(before);
            if (!string.Equals(actualHash, expectedBeforeHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"stale_state: file '{absolutePath}' has changed since you last read it " +
                    $"(expected hash {expectedBeforeHash[..8]}…, got {actualHash[..8]}…). " +
                    "Re-read the file (scene_hierarchy/prefab_hierarchy) and retry with the updated hash.");
            }
        }

        // Clone so the "before" snapshot stays pristine for delta computation.
        EchoObject after = before.Clone();
        mutate(after);
        return (before, after, "");
    }

    private async Task<SceneWriteResult> CommitAsync(
        EchoObject before, EchoObject after, string absolutePath, string targetAsset,
        ChangeProvenance provenance, bool dryRun, string summary,
        string beforeHash, string afterHash, CancellationToken cancellationToken)
    {
        // ★ P0-1: Idempotent replay short-circuit. Check BEFORE writing any file or computing the
        // delta. If a record with this idempotency key + source already exists, return the original
        // result without touching disk — so a retry (network blip / agent double-submit) leaves both
        // the file and the journal pristine. (AppendAsync re-checks as a second line of defense.)
        if (!dryRun
            && !string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is { } existing)
        {
            return new SceneWriteResult(existing.ChangeId, targetAsset, existing.BeforeHash, existing.AfterHash,
                dryRun, $"Idempotent replay of change {existing.ChangeId} (no write).");
        }

        EchoObject delta = ProvenancedDelta.Create(before, after, provenance);
        string changeId = ProvenancedDelta.GetChangeId(delta) ?? Guid.NewGuid().ToString("N");

        if (!dryRun)
        {
            // Write the mutated tree back to disk, then append the provenance-bearing change to the
            // journal — the same journal the editor's ChangeJournalService writes.
            File.WriteAllText(absolutePath, after.WriteToString());
            await _journal.AppendAsync(targetAsset, delta, provenance, beforeHash, afterHash, cancellationToken);
        }

        return new SceneWriteResult(changeId, targetAsset, beforeHash, afterHash, dryRun, summary);
    }

    // ★ Gen-P0.2 — full assembly-qualified type strings matching the editor's save output exactly.
    private const string GameObjectType =
        "XEngine.Runtime.GameObject, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string TransformType =
        "XEngine.Vector.Transform, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string Float3Type =
        "XEngine.Vector.Float3, Vector, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    private const string QuaternionType =
        "XEngine.Vector.Quaternion, Vector, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>★ Gen-P0.2 — Build a GameObject node in the editor's exact save shape. The flag
    /// fields MUST be Byte tags (0B/1B): GameObject.Deserialize reads them via .ByteValue, so the old
    /// Bool tags failed to round-trip through the engine. $ids are minted by the caller (above the
    /// scene's current max) so the node can be referenced by a flat-array stub when parented.</summary>
    private static EchoObject MakeMinimalGameObject(string name, string identifier, int goEchoId, int transformEchoId)
    {
        EchoObject go = EchoObject.NewCompound();
        go["$id"] = new EchoObject(goEchoId);
        go["$type"] = new EchoObject(GameObjectType);
        go["Name"] = new EchoObject(name);
        go["AssetPath"] = new EchoObject("");
        go["Identifier"] = new EchoObject(identifier);
        go["Static"] = new EchoObject((byte)0);
        go["Enabled"] = new EchoObject((byte)1);
        go["EnabledInHierarchy"] = new EchoObject((byte)1);
        go["TagIndex"] = new EchoObject(0);
        go["LayerIndex"] = new EchoObject(0);
        go["HideFlags"] = new EchoObject(0);

        EchoObject transform = EchoObject.NewCompound();
        transform["$id"] = new EchoObject(transformEchoId);
        transform["$type"] = new EchoObject(TransformType);
        transform["_localPosition"] = Vec(0f, 0f, 0f);
        transform["_localScale"] = Vec(1f, 1f, 1f);
        transform["_localRotation"] = Rot(0f, 0f, 0f, 1f);
        go["Transform"] = transform;

        go["Components"] = EchoObject.NewList();
        go["Children"] = EchoObject.NewList();
        return go;
    }

    private static EchoObject Vec(float x, float y, float z)
    {
        EchoObject v = EchoObject.NewCompound();
        v["$type"] = new EchoObject(Float3Type);
        v["X"] = new EchoObject(x);
        v["Y"] = new EchoObject(y);
        v["Z"] = new EchoObject(z);
        return v;
    }

    private static EchoObject Rot(float x, float y, float z, float w)
    {
        EchoObject r = EchoObject.NewCompound();
        r["$type"] = new EchoObject(QuaternionType);
        r["X"] = new EchoObject(x);
        r["Y"] = new EchoObject(y);
        r["Z"] = new EchoObject(z);
        r["W"] = new EchoObject(w);
        return r;
    }
}
