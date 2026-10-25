namespace XEngine.Ai.Protocol;

/// <summary>★ Layer 1 (B1) — One asset entry surfaced by the asset tools.</summary>
public sealed record AssetInfo(
    string RelativePath,
    string? Guid,
    string? ImporterType,
    int? ImporterVersion,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    /// <summary>True when a .meta sidecar exists.</summary>
    bool HasMeta);

/// <summary>Paged list of assets. ★ P0-3: <c>HasMore</c> tells the AI whether more assets exist beyond
/// the returned page.</summary>
public sealed record AssetList(
    string AssetsRoot,
    int Total,
    IReadOnlyList<AssetInfo> Assets,
    bool HasMore = false);

/// <summary>Asset detail (from .meta) — GUID, importer, importer version, settings keys.</summary>
public sealed record AssetDetail(
    string RelativePath,
    string Guid,
    string ImporterType,
    int ImporterVersion,
    IReadOnlyDictionary<string, string> Settings,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc);

/// <summary>★ P0-5 — Result of an asset import (copy source into Assets/ + mint .meta).</summary>
public sealed record AssetImportResult(
    string TargetAsset,
    string Guid,
    string ImporterType,
    int ImporterVersion,
    /// <summary>True when an existing asset was replaced (GUID preserved).</summary>
    bool Overwritten);

/// <summary>★ P0-5 — Result of an asset move (file + .meta relocated).</summary>
public sealed record AssetMoveResult(string SourceAsset, string DestinationAsset);

/// <summary>★ Gen-P0.5 — One derived sub-asset of a model file. <see cref="Guid"/> is the
/// deterministic id the editor will assign on import: SHA256(parentGuid + StableId)[0..16].
/// Scene/prefab references built against it survive the eventual in-editor import.</summary>
public sealed record SubAssetInfo(
    /// <summary>The importer's stable source id (for models this is also the display name).</summary>
    string StableId,
    string DisplayName,
    /// <summary>Asset kind: Mesh, Material, AnimationClip, Texture2D.</summary>
    string Kind,
    string Guid);

/// <summary>★ Gen-P0.5 — All sub-assets a model file will produce when the editor imports it,
/// derived offline by replaying the import naming pipeline (no editor, no Library/cache).</summary>
public sealed record SubAssetList(
    string TargetAsset,
    string ParentGuid,
    string ImporterType,
    int Count,
    IReadOnlyList<SubAssetInfo> SubAssets,
    /// <summary>Duplicate stable ids (which would fail the real import) and settings the offline
    /// derivation cannot replay (e.g. enabled mesh-feature generators).</summary>
    IReadOnlyList<string> Warnings);

/// <summary>★ P0-5 — Result of an asset delete (file + .meta removed).</summary>
public sealed record AssetDeleteResult(string TargetAsset);

// ---- P0-6 material inspect/patch models ----

/// <summary>★ P0-6 — One material property entry (typed value bucketed by category: color / float /
/// int / vector / texture). Color and vector values are surfaced as a flat string so the AI can read
/// them without knowing the Echo layout; patches set them via material_patch's path/literal patches.</summary>
public sealed record MaterialProperty(
    string Name,
    /// <summary>Bucket: color, float, int, vector2, vector3, vector4, texture, matrix, buffer.</summary>
    string Category,
    /// <summary>Human-readable value (e.g. "1,0.5,0,1" for a color, "0.5" for a float, a GUID for a texture).</summary>
    string Value);

/// <summary>★ P0-6 — Structured read of a .mat file: shader GUID + categorized property list +
/// content hash (for material_patch's expectedBeforeHash drift protection).</summary>
public sealed record MaterialDetail(
    string TargetAsset,
    /// <summary>The shader's AssetID GUID (the material references a shader by GUID).</summary>
    string ShaderGuid,
    string Name,
    IReadOnlyList<MaterialProperty> Properties,
    /// <summary>SHA-256 content hash of the .mat's Echo tree. Pass to material_patch's expectedBeforeHash.</summary>
    string ContentHash);

// ---- asset_data inspect/patch (generic ScriptableObject data) ----

/// <summary>★ One leaf field of a generic .asset file: a slash-path pointer + human-readable value.</summary>
public sealed record AssetDataField(string Path, string Value);

/// <summary>★ Structured read of a generic .asset file (ScriptableObject instance): its $type + a flat
/// path/value list of every leaf scalar field + the content hash for drift protection. Lets the AI
/// inspect + patch any Echo-serialized data asset (game configs, item tables, skill data) without
/// knowing the concrete runtime type.</summary>
public sealed record AssetDataDetail(
    string TargetAsset,
    /// <summary>The $type assembly-qualified name (the ScriptableObject subclass).</summary>
    string TypeName,
    string Name,
    IReadOnlyList<AssetDataField> Fields,
    string ContentHash);

/// <summary>★ M27 — Dual GLSL+Slang Shader Graph compile result. <see cref="Source"/> is the
/// generated Shader DSL; <see cref="OutputPath"/> is set when the CLI wrote a <c>.shader</c> file.</summary>
public sealed record ShaderGraphCompileResult(
    string TargetAsset,
    string ShaderName,
    string Target,
    string Source,
    string? OutputPath,
    bool Written);

/// <summary>M5 — Visual Effect Graph compile result. <see cref="Source"/> is the generated Shader DSL.</summary>
public sealed record VfxGraphCompileResult(
    string TargetAsset,
    string GraphName,
    string Template,
    int Capacity,
    float SpawnRate,
    float BurstCount,
    string Source,
    string? OutputPath,
    bool Written);

/// <summary>M5 — Visual Effect Graph node catalog (contexts, blocks, operators).</summary>
public sealed record VfxNodeCatalog(
    IReadOnlyList<string> Contexts,
    IReadOnlyList<string> Blocks,
    IReadOnlyList<string> Operators);

/// <summary>M5 — Copy a packaged Empty/Loop/Burst/Trail/Mesh/Subgraph template into the project.</summary>
public sealed record VfxGraphCreateResult(
    string TargetAsset,
    string Template,
    bool Written,
    bool DryRun);
