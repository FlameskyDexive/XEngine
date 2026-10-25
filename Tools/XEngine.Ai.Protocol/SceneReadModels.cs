namespace XEngine.Ai.Protocol;

/// <summary>★ Layer 1 (B1) — A scene file entry returned by the scene-list tool.</summary>
public sealed record SceneFile(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

/// <summary>Paged list of scene files in a project (unified pagination: total + offset + limit).
/// ★ P0-3: <c>HasMore</c> explicitly tells the AI whether more pages exist (offset + returned &lt; Total),
/// so it knows to request the next page without recomputing.</summary>
public sealed record SceneFileList(
    string AssetsRoot,
    IReadOnlyList<SceneFile> Scenes,
    int Total = 0,
    int Offset = 0,
    int Limit = 0,
    /// <summary>True when more results exist beyond the current page (call again with a higher offset).</summary>
    bool HasMore = false);

/// <summary>A node in a scene's GameObject hierarchy (read offline from the Echo tree).
/// ★ P2-1: <c>Tag</c>/<c>Layer</c> now carry the resolved names (from ProjectSettings/Tags &amp;
/// Layers.yaml); the raw indices are in <c>TagIndex</c>/<c>LayerIndex</c> so the AI can round-trip.</summary>
public sealed record SceneObjectNode(
    string Identifier,
    string Name,
    /// <summary>The resolved tag name (e.g. "Player"), or "Untagged". Aliases TagName.</summary>
    string? Tag,
    /// <summary>The resolved layer NAME (e.g. "Default"). For the raw index use LayerIndex.</summary>
    int Layer,
    SceneTransform Transform,
    IReadOnlyList<string> ChildIdentifiers,
    IReadOnlyList<SceneComponent> Components,
    /// <summary>★ P2-1 — The raw tag index the scene stores on disk. Use scene_object_patch to set.</summary>
    int TagIndex = 0,
    /// <summary>★ P2-1 — The raw layer index the scene stores on disk (0-31). Use scene_object_patch to set.</summary>
    int LayerIndex = 0,
    /// <summary>★ P2-1 — The resolved layer name (e.g. "Default", "Water"). Empty when the slot is unnamed.</summary>
    string? LayerName = null,
    /// <summary>★ Gen-P0.2 — Child nodes nested up to the read's maxDepth (null when leaf/omitted).
    /// ChildIdentifiers stays authoritative for linkage; this carries the full child data.</summary>
    IReadOnlyList<SceneObjectNode>? Children = null);

/// <summary>Transform data for a node.</summary>
public sealed record SceneTransform(
    float X, float Y, float Z,
    float RotX, float RotY, float RotZ, float RotW,
    float ScaleX, float ScaleY, float ScaleZ);

/// <summary>A component on a node (short type name + its serializable fields as string values).
/// ★ P0-4: <c>Identifier</c> surfaces the component's <c>_identifier</c> GUID at the top level so the
/// AI can target it for <c>scene_component_remove</c> without mining the fields dictionary.</summary>
public sealed record SceneComponent(
    string TypeName,
    IReadOnlyDictionary<string, string> Fields,
    /// <summary>The component's _identifier GUID (empty when absent). Use with scene_component_remove's componentIdentifier.</summary>
    string Identifier = "");

/// <summary>The full hierarchy of a scene read offline. ★ P0-3: large scenes are truncated to
/// <c>RootObjects.Count == MaxObjects</c> with <c>HasMore = true</c>; <c>TotalObjects</c> still
/// reports the true count so the AI knows how much was omitted. To page through a truncated scene,
/// narrow the query (filter by parent Identifier) or raise the maxObjects argument.</summary>
public sealed record SceneHierarchy(
    string AbsolutePath,
    string SceneName,
    IReadOnlyList<SceneObjectNode> RootObjects,
    int TotalObjects,
    string ContentHash,
    /// <summary>Cap applied to RootObjects for this response.</summary>
    int MaxObjects = 500,
    /// <summary>True when RootObjects was truncated (more objects exist on disk than returned).</summary>
    bool HasMore = false);

// ---- P2-2 scene settings (fog/ambient/skybox) ----

/// <summary>★ P2-2 — One top-level scene setting (Fog/Ambient/Skybox) entry: a slash-path pointer +
/// human-readable value, so the AI knows what's set and how to address it in scene_settings_patch.</summary>
public sealed record SceneSettingEntry(
    /// <summary>Slash-path into the scene Echo tree (e.g. "Fog/Mode", "Ambient/Color/R").</summary>
    string Path,
    /// <summary>Human-readable value (e.g. "ExponentialSquared", "0.5", a color tuple).</summary>
    string Value);

/// <summary>★ P2-2 — Structured read of a scene's top-level lighting/skybox settings (the Fog, Ambient,
/// and Skybox fields that live on the Scene root, not on GameObjects). Returns a flat list of path/value
/// entries grouped by category, plus the content hash for drift protection.</summary>
public sealed record SceneSettings(
    string TargetAsset,
    /// <summary>The category each entry belongs to: "fog", "ambient", or "skybox".</summary>
    IReadOnlyDictionary<string, IReadOnlyList<SceneSettingEntry>> Categories,
    string ContentHash);
