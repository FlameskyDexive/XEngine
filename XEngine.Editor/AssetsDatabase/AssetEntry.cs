using System;
using System.Security.Cryptography;
using System.Text;

using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Editor;

/// <summary>
/// Represents a sub-asset inside a parent asset (e.g. a Mesh inside a Model file).
/// </summary>
[Serializable]
public class SubAssetEntry
{
    [SerializeField] public Guid Guid;
    [SerializeField] public string Name = "";
    [SerializeField] public string TypeName = "";  // Assembly-qualified type name
    [SerializeField] public Guid[] Dependencies = Array.Empty<Guid>();

    public Type? Type
    {
        get => !string.IsNullOrEmpty(TypeName) ? RuntimeUtils.ResolveType(TypeName) : null;
        set => TypeName = value?.AssemblyQualifiedName ?? "";
    }
}

/// <summary>
/// In-memory representation of a tracked asset in the database.
/// Stored in the index and serialized to metadata.db for fast startup.
/// </summary>
[Serializable]
public class AssetEntry
{
    [SerializeField] public Guid Guid;
    [SerializeField] public string Path = "";           // Relative to Assets/, e.g. "Textures/Grass.png"
    [SerializeField] public string ImporterType = "";   // e.g. "TextureImporter"
    [SerializeField] public int ImporterVersion;
    [SerializeField] public long LastModifiedTicks;     // File.GetLastWriteTimeUtc().Ticks
    /// <summary>
    /// Fingerprint of the companion .meta file at the last successful import. The source mtime
    /// alone cannot detect package/import-setting edits, because those edits only touch the .meta.
    /// Empty means the entry predates meta-fingerprint persistence; startup may baseline it only
    /// when the existing cache is newer than both source and .meta, otherwise it is reimported.
    /// </summary>
    [SerializeField] public string MetaFingerprint = "";
    [SerializeField] public string? MainAssetTypeName;  // Assembly-qualified type name of main asset
    [SerializeField] public Guid[] Dependencies = Array.Empty<Guid>();
    [SerializeField] public string[] Labels = Array.Empty<string>();
    [SerializeField] public SubAssetEntry[] SubAssets = Array.Empty<SubAssetEntry>();

    /// <summary>
    /// ★ Layer 0 (A1) — Structured edit provenance for the asset. Replaces nothing (kept alongside
    /// the existing mtime/Labels): records who last imported/edited, why, and the journal operation
    /// id that did it, so an AI can trace an asset's history. Defaults to empty and is absent in old
    /// metadata.db rows (Echo deserializes missing compounds to default), so the existing index loads
    /// unchanged.
    /// </summary>
    [SerializeField] public AssetProvenance Provenance = new();

    [SerializeIgnore] public bool NeedsReimport;

    public Type? MainAssetType
    {
        get => MainAssetTypeName != null ? RuntimeUtils.ResolveType(MainAssetTypeName) : null;
        set => MainAssetTypeName = value?.AssemblyQualifiedName;
    }

    /// <summary>
    /// Generate a deterministic GUID for a sub-asset based on parent GUID + name.
    /// Stable across reimports as long as parent GUID and sub-asset name don't change.
    /// </summary>
    public static Guid DeriveSubAssetGuid(Guid parentGuid, string subAssetName)
    {
        byte[] parentBytes = parentGuid.ToByteArray();
        byte[] nameBytes = Encoding.UTF8.GetBytes(subAssetName);
        byte[] combined = new byte[parentBytes.Length + nameBytes.Length];
        parentBytes.CopyTo(combined, 0);
        nameBytes.CopyTo(combined, parentBytes.Length);
        byte[] hash = SHA256.HashData(combined);
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// ★ Layer 0 (A1) — Structured edit provenance for an <see cref="AssetEntry"/>. Carries the
/// "why" and "who" of the last import/edit and a pointer back to the operation journal entry (A2)
/// that produced it, so an AI agent inspecting an asset can read its history rather than just its
/// current state. Plain serializable class (mirrors <see cref="SubAssetEntry"/>); absent fields
/// deserialize to defaults for backward compatibility with existing metadata.db rows.
/// </summary>
[Serializable]
public class AssetProvenance
{
    [SerializeField] public string EditedBy = "";        // username, "editor", or AI tool name
    [SerializeField] public string EditReason = "";      // natural-language intent of last edit
    [SerializeField] public long EditedAtUtcTicks = 0L;  // 0 when pre-provenance
    /// <summary>Journal change-id (see XEngine.Data.Provenance) of the last edit, if recorded.</summary>
    [SerializeField] public string? LastEditOperationId;
}
