using System.Collections.Generic;

namespace XEngine.Ai.Protocol;

/// <summary>★ M6 — Structured read of a baked <c>.navmesh</c> Echo asset (no engine load).</summary>
public sealed record NavInspectResult(
    string TargetAsset,
    string Name,
    string TypeName,
    int SettingsHash,
    int AgentTypeId,
    int TileSize,
    int TileCountX,
    int TileCountZ,
    NavVec3 BoundsMin,
    NavVec3 BoundsMax,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<NavTileSummary> Tiles,
    int TileDataBytes,
    int TileCacheDataBytes,
    string ContentHash);

public sealed record NavTileSummary(int TileX, int TileZ, int PolyCount, int VertCount);

public sealed record NavVec3(float X, float Y, float Z);

/// <summary>Offline Detour query against the <c>TileData</c> payload in a <c>.navmesh</c>.</summary>
public sealed record NavQueryPathResult(
    string TargetAsset,
    bool Succeeded,
    string Status,
    IReadOnlyList<NavVec3> Corners,
    string? Error);

/// <summary>OBJ / JSON / Detour-bin export of a <c>.navmesh</c>.</summary>
public sealed record NavExportResult(
    string TargetAsset,
    string Format,
    string OutputPath,
    int BytesWritten,
    int VertexCount,
    int TriangleCount);
