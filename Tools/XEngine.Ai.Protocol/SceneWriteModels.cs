namespace XEngine.Ai.Protocol;

/// <summary>
/// ★ Layer 1 (B2) — One field patch on a scene object. Path is a JSON-pointer-style path into the
/// GameObject's Echo tree (e.g. "Transform/_localPosition/Y" to move the object's Y). The new value
/// is a literal whose Echo type is inferred (number/bool/string). Used by the scene-object-patch tool.
/// </summary>
public sealed record SceneFieldPatch(
    /// <summary>JSON-pointer-ish path relative to the GameObject root (slash-separated tag names).</summary>
    string Path,
    /// <summary>The new value as a literal string (e.g. "4.5", "true", "MainCamera").</summary>
    string Value);

/// <summary>Result of a scene-write operation (patch/create/delete). Carries the change id so the
/// caller can find it via journal-query, plus before/after hashes for drift detection.</summary>
public sealed record SceneWriteResult(
    string ChangeId,
    string TargetAsset,
    string BeforeHash,
    string AfterHash,
    bool DryRun,
    string Summary,
    /// <summary>★ Gen-P1.5 — For scene_object_create: the new GameObject's stable Identifier, so
    /// workflows can chain it into scene_component_add/patch steps (${steps.create.createdIdentifier})
    /// without parsing the human Summary. Null for other write operations.</summary>
    string? CreatedIdentifier = null);
