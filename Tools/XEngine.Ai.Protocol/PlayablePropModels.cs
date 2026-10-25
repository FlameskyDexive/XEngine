using System.Collections.Generic;

namespace XEngine.Ai.Protocol;

/// <summary>★ Gen-P2.1 — One mesh inside a model, with the offline-derived counts and AABB
/// the editor import will see (same Clay replay as <c>asset_subassets</c>).</summary>
public sealed record MeshStatEntry(
    string Name,
    int Vertices,
    int Triangles,
    float MinX, float MinY, float MinZ,
    float MaxX, float MaxY, float MaxZ,
    float SizeX, float SizeY, float SizeZ,
    float CenterX, float CenterY, float CenterZ,
    /// <summary>Deterministic sub-asset GUID when a .meta exists; empty otherwise.</summary>
    string? Guid);

/// <summary>★ Gen-P2.1 — Combined mesh budget + suggested BoxCollider extents for a model file.</summary>
public sealed record MeshStatsResult(
    string TargetAsset,
    string? ParentGuid,
    int MeshCount,
    int TotalVertices,
    int TotalTriangles,
    float CombinedMinX, float CombinedMinY, float CombinedMinZ,
    float CombinedMaxX, float CombinedMaxY, float CombinedMaxZ,
    float CombinedSizeX, float CombinedSizeY, float CombinedSizeZ,
    float CombinedCenterX, float CombinedCenterY, float CombinedCenterZ,
    /// <summary>BoxCollider size that fully covers the combined AABB (each axis ≥ 0.01).</summary>
    float SuggestedBoxSizeX, float SuggestedBoxSizeY, float SuggestedBoxSizeZ,
    float SuggestedBoxCenterX, float SuggestedBoxCenterY, float SuggestedBoxCenterZ,
    int? MaxTriangles,
    bool OverBudget,
    IReadOnlyList<MeshStatEntry> Meshes,
    IReadOnlyList<string> Warnings);

/// <summary>★ Gen-P2.1 — Result of adding a BoxCollider or MeshCollider derived from a model.</summary>
public sealed record ColliderAddResult(
    string ChangeId,
    string TargetAsset,
    string ObjectIdentifier,
    string ComponentIdentifier,
    string Shape,
    string BeforeHash,
    string AfterHash,
    bool DryRun,
    string Summary,
    MeshStatsResult Stats);

/// <summary>★ Gen-P2.1 — A scene GameObject extracted to a standalone .prefab (GUID minted).</summary>
public sealed record PrefabCreateResult(
    string PrefabPath,
    string Guid,
    string SourceScene,
    string SourceObjectIdentifier,
    string ChangeId,
    bool DryRun,
    bool Overwritten);

/// <summary>★ Gen-P2.1 — Triangle-budget gate: remesh (or mock-decimate) when over the cap.</summary>
public sealed record BudgetEnsureResult(
    string AssetPath,
    string Guid,
    int TotalTriangles,
    int MaxTriangles,
    bool OverBudget,
    bool Remeshed,
    bool WouldRemesh,
    bool DryRun,
    string? RemeshTaskId,
    MeshStatsResult Stats,
    IReadOnlyList<string> Warnings);

/// <summary>★ Gen-P2.2 — One clip wired into a generated AnimatorController.</summary>
public sealed record CharacterClipBinding(string StateName, string ClipGuid, string StableId);

/// <summary>★ Gen-P2.2 — Result of building an Animator + .controller from a model's animation clips.</summary>
public sealed record CharacterWireResult(
    string ScenePath,
    string ObjectIdentifier,
    string AnimatorComponentId,
    string ControllerPath,
    string ControllerGuid,
    IReadOnlyList<CharacterClipBinding> Clips,
    IReadOnlyList<string> Diagnostics,
    bool DryRun,
    IReadOnlyList<string> Warnings);

/// <summary>★ Gen-P2.3 — One grid cell of a spritesheet, with the deterministic Sprite sub-asset GUID.</summary>
public sealed record SpriteSliceFrame(
    string Name,
    string Guid,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>★ Gen-P2.3 — TextureImporter Multiple slice written into a PNG .meta.</summary>
public sealed record SpriteSliceResult(
    string AssetPath,
    string? ParentGuid,
    int TextureWidth,
    int TextureHeight,
    int Columns,
    int Rows,
    int CellWidth,
    int CellHeight,
    IReadOnlyList<SpriteSliceFrame> Frames,
    bool DryRun,
    IReadOnlyList<string> Warnings);

/// <summary>★ Gen-P2.3 — Slice a spritesheet and bind SpriteRenderer + SpriteFlipbook on a GameObject.</summary>
public sealed record SpriteWireResult(
    string ScenePath,
    string ObjectIdentifier,
    string SpriteRendererId,
    string FlipbookId,
    float FramesPerSecond,
    SpriteSliceResult Slice,
    bool DryRun);
