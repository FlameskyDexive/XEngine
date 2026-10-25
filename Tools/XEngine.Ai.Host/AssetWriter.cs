using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;
// The Protocol layer also has a ChangeProvenance twin; alias to the data-layer one which
// ProvenancedDelta/ChangeJournal actually consume.
using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (P0-5) — Offline asset file operations: import (copy source file into Assets/ + mint a
/// .meta with a fresh GUID and the inferred importer), move, delete. Each writes a provenance-bearing
/// record to the journal so asset lifecycle is traceable end-to-end.
///
/// <para>The importer name is inferred from the file extension using the same map the editor's
/// <c>EditorRegistries</c> builds from <c>[ImporterFor]</c> attributes (mirrored here so the Host
/// stays engine-agnostic). Unknown extensions fall back to <c>DefaultImporter</c>, matching the
/// editor. Importer version defaults to 1 for the common types; the two that aggregate feature
/// versions (Model/Mesh) are stamped with their base version — the editor's next import re-stamps
/// the authoritative version if needed.</para>
/// </summary>
public sealed class AssetWriter
{
    private readonly ChangeJournal _journal;

    public AssetWriter(ChangeJournal journal)
    {
        _journal = journal;
    }

    /// <summary>Import (copy) a source file into the project's Assets/ tree and mint a .meta for it.
    /// If <paramref name="destinationPath"/> already exists, throws (use overwrite=true to replace;
    /// overwrite preserves the existing GUID so references survive). Records a provenance-bearing
    /// change to the journal.</summary>
    public async Task<AssetImportResult> ImportAsync(
        string sourceAbsolutePath,
        string destinationAbsolutePath,
        string targetAsset,
        ChangeProvenance provenance,
        bool overwrite = false,
        int? animationType = null,
        CancellationToken cancellationToken = default)
    {
        // ★ P0-1: idempotent replay short-circuit BEFORE any file mutation. An AI retry must not
        // clobber a file the journal already recorded. Re-derive the result from the on-disk state
        // (the file is already what the original import produced).
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is not null
            && File.Exists(destinationAbsolutePath))
        {
            return ReReadImportResult(destinationAbsolutePath, targetAsset);
        }

        if (!File.Exists(sourceAbsolutePath))
        {
            throw new FileNotFoundException($"Source file not found: {sourceAbsolutePath}");
        }

        bool exists = File.Exists(destinationAbsolutePath);
        if (exists && !overwrite)
        {
            throw new InvalidOperationException(
                $"asset_exists: '{targetAsset}' already exists. Pass overwrite=true to replace " +
                "(the existing GUID is preserved so references survive), or choose a different destination.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationAbsolutePath)!);

        // Preserve the GUID when overwriting (so scene/prefab references don't break); mint a fresh
        // one on first import.
        string guid;
        string metaPath = destinationAbsolutePath + ".meta";
        if (exists && overwrite && File.Exists(metaPath))
        {
            guid = ReadExistingGuid(metaPath) ?? Guid.NewGuid().ToString("N");
        }
        else
        {
            guid = Guid.NewGuid().ToString("N");
        }

        string importer = InferImporter(destinationAbsolutePath);
        int importerVersion = InferImporterVersion(importer);

        // Copy the source file (overwrite if requested).
        File.Copy(sourceAbsolutePath, destinationAbsolutePath, overwrite: overwrite);

        // Write the .meta (atomic temp+rename, matching MetaFile.Write).
        WriteMeta(metaPath, guid, importer, importerVersion, animationType);

        var result = new AssetImportResult(targetAsset, guid, importer, importerVersion, exists && overwrite);
        await RecordAsync(targetAsset, exists && overwrite ? "asset_overwrite" : "asset_import",
            provenance, $"Imported {targetAsset} ({importer}, GUID {guid[..8]}…).", cancellationToken);
        return result;
    }

    /// <summary>Move (rename/relocate) an asset file and its .meta sidecar. Refuses to clobber an
    /// existing destination.</summary>
    public async Task<AssetMoveResult> MoveAsync(
        string sourceAbsolutePath,
        string destinationAbsolutePath,
        string sourceAsset,
        string destinationAsset,
        ChangeProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        // ★ P0-1: idempotent replay — the move already happened (source gone, dest exists).
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is not null)
        {
            return new AssetMoveResult(sourceAsset, destinationAsset);
        }

        if (!File.Exists(sourceAbsolutePath))
        {
            throw new FileNotFoundException($"Source asset not found: {sourceAsset}");
        }
        if (File.Exists(destinationAbsolutePath))
        {
            throw new InvalidOperationException(
                $"asset_exists: destination '{destinationAsset}' already exists. Move refused to clobber.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationAbsolutePath)!);
        File.Move(sourceAbsolutePath, destinationAbsolutePath);

        // Move the .meta sidecar too (preserves the GUID + importer settings).
        string sourceMeta = sourceAbsolutePath + ".meta";
        if (File.Exists(sourceMeta))
        {
            File.Move(sourceMeta, destinationAbsolutePath + ".meta");
        }

        await RecordAsync(sourceAsset, "asset_move", provenance,
            $"Moved {sourceAsset} -> {destinationAsset}.", cancellationToken);
        return new AssetMoveResult(sourceAsset, destinationAsset);
    }

    /// <summary>Delete an asset file and its .meta sidecar. Refuses if the asset doesn't exist.</summary>
    public async Task<AssetDeleteResult> DeleteAsync(
        string absolutePath,
        string targetAsset,
        ChangeProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        // ★ P0-1: idempotent replay — the delete already happened (file gone).
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is not null)
        {
            return new AssetDeleteResult(targetAsset);
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Asset not found: {targetAsset}");
        }

        File.Delete(absolutePath);
        string metaPath = absolutePath + ".meta";
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }

        await RecordAsync(targetAsset, "asset_delete", provenance,
            $"Deleted {targetAsset}.", cancellationToken);
        return new AssetDeleteResult(targetAsset);
    }

    // ---- helpers ----

    /// <summary>Re-derive an <see cref="AssetImportResult"/> from the on-disk file + .meta. Used by
    /// the idempotent-replay short-circuit so a retry returns a truthful result without re-copying.</summary>
    private static AssetImportResult ReReadImportResult(string destinationAbsolutePath, string targetAsset)
    {
        string guid = ReadExistingGuid(destinationAbsolutePath + ".meta") ?? "";
        string importer = InferImporter(destinationAbsolutePath);
        int importerVersion = InferImporterVersion(importer);
        return new AssetImportResult(targetAsset, guid, importer, importerVersion, Overwritten: true);
    }

    private async Task RecordAsync(
        string targetAsset, string operation, ChangeProvenance provenance, string summary,
        CancellationToken cancellationToken)
    {
        // ★ P0-1: dedup by idempotency key before doing anything. Asset ops are file mutations so the
        // short-circuit must happen here (no separate "commit" step like SceneWriter).
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is { } existing)
        {
            return;
        }

        // Build a minimal provenance-bearing delta (no Echo tree diff for asset file ops — the
        // payload is the operation + summary, recorded for traceability).
        EchoObject delta = EchoObject.NewCompound();
        delta["operation"] = new EchoObject(operation);
        delta["summary"] = new EchoObject(summary);
        EchoObject enriched = ProvenancedDelta.WithProvenance(delta, provenance);

        string changeId = ProvenancedDelta.GetChangeId(enriched) ?? Guid.NewGuid().ToString("N");
        // before/after hashes don't apply to whole-file asset ops; record empty strings.
        await _journal.AppendAsync(targetAsset, enriched, provenance, "", "", cancellationToken);
    }

    internal static string InferImporter(string assetPath)
    {
        string ext = Path.GetExtension(assetPath).ToLowerInvariant();
        return ImporterByExt.TryGetValue(ext, out string? importer) ? importer : "DefaultImporter";
    }

    internal static int InferImporterVersion(string importerName) =>
        ImporterVersionByName.TryGetValue(importerName, out int v) ? v : 1;

    private static string? ReadExistingGuid(string metaPath)
    {
        try
        {
            EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
            return meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String
                ? g.StringValue : null;
        }
        catch { return null; }
    }

    private static void WriteMeta(string metaPath, string guid, string importer, int importerVersion,
        int? animationType = null)
    {
        EchoObject meta = EchoObject.NewCompound();
        meta["guid"] = new EchoObject(guid);
        meta["importer"] = new EchoObject(importer);
        meta["importerVersion"] = new EchoObject(importerVersion);
        if (importer == "EditorModelImporter")
        {
            EchoObject settings = EchoObject.NewCompound();
            settings["animationType"] = new EchoObject(animationType ?? 0);
            settings["avatarDefinition"] = new EchoObject(2); // No Avatar
            settings["importAnimation"] = new EchoObject(true);
            meta["settings"] = settings;
        }
        // Atomic temp+rename (matches MetaFile.Write) so a crash can't leave a truncated .meta that
        // would otherwise get a regenerated GUID on next EnsureMeta — breaking all references.
        string tempPath = metaPath + ".tmp";
        File.WriteAllText(tempPath, meta.WriteToString());
        File.Move(tempPath, metaPath, overwrite: true);
    }

    /// <summary>Write a sparse Echo <c>.controller</c> next to a scene and mint its .meta. Used by
    /// <c>animator_component_add</c> when createController is set.</summary>
    public static string WriteAnimatorController(string absolutePath, string name)
    {
        EchoObject root = EchoObject.NewCompound();
        root["$type"] = new EchoObject(
            "XEngine.Animation.AnimatorController, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");
        root["Name"] = new EchoObject(name);
        root["DefaultStateName"] = new EchoObject("New State");
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, root.WriteToString());
        string guid = Guid.NewGuid().ToString("N");
        WriteMeta(absolutePath + ".meta", guid, "ScriptableObjectImporter", 1);
        return guid;
    }

    /// <summary>Mirror of the editor's [ImporterFor] registration. Keep in sync when a new importer
    /// is added to the engine.</summary>
    private static readonly Dictionary<string, string> ImporterByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "TextureImporter",
        [".jpg"] = "TextureImporter",
        [".jpeg"] = "TextureImporter",
        [".bmp"] = "TextureImporter",
        [".tga"] = "TextureImporter",
        [".psd"] = "TextureImporter",
        [".hdr"] = "TextureImporter",
        [".dds"] = "TextureImporter",
        [".exr"] = "TextureImporter",
        [".wav"] = "AudioImporter",
        [".mp3"] = "AudioImporter",
        [".ogg"] = "AudioImporter",
        [".flac"] = "AudioImporter",
        [".fbx"] = "EditorModelImporter",
        [".gltf"] = "EditorModelImporter",
        [".glb"] = "EditorModelImporter",
        [".obj"] = "EditorModelImporter",
        [".mesh"] = "MeshImporter",
        [".mat"] = "MaterialImporter",
        [".shader"] = "ShaderImporter",
        [".scene"] = "SceneImporter",
        [".prefab"] = "PrefabImporter",
        [".cs"] = "ScriptImporter",
        [".asset"] = "ScriptableObjectImporter",
        [".controller"] = "ScriptableObjectImporter",
        [".anim"] = "ScriptableObjectImporter",
        [".ttf"] = "FontImporter",
        [".otf"] = "FontImporter",
        [".terraindata"] = "TerrainDataImporter",
        [".inputactions"] = "InputActionMapImporter",
        [".dll"] = "PluginImporter",
        [".so"] = "PluginImporter",
        [".dylib"] = "PluginImporter",
        // AssemblyDefinitionDatabase.Extension = ".asmdef"
        [".asmdef"] = "AssemblyDefinitionImporter",
    };

    /// <summary>Base importer versions (from the engine's AssetImporter.Version). Model/Mesh aggregate
    /// feature versions at runtime; we stamp their base and let the editor re-stamp on next import.</summary>
    private static readonly Dictionary<string, int> ImporterVersionByName = new(StringComparer.Ordinal)
    {
        ["SceneImporter"] = 3,
        ["ShaderImporter"] = 4,
        ["TextureImporter"] = 3,
        // EditorModelImporter base = 6 (M29 rig animationType), MeshImporter base = 1 (both + MeshFeatureRegistry.AggregateVersion at runtime)
        ["EditorModelImporter"] = 6,
        ["MeshImporter"] = 1,
    };
}
