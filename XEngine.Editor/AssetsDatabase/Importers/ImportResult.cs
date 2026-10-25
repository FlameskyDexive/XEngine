using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime;

namespace XEngine.Editor.Importers;

/// <summary>
/// Import context passed to importers. Holds the entry GUID so sub-assets get
/// correct deterministic IDs immediately enabling proper AssetRef serialization.
/// </summary>
public class ImportContext
{
    /// <summary>The parent entry's GUID.</summary>
    public Guid AssetGuid { get; }

    /// <summary>Absolute path to the source file.</summary>
    public string AbsolutePath { get; }

    /// <summary>Importer settings from .meta file.</summary>
    public Echo.EchoObject? Settings { get; }

    /// <summary>The primary imported object.</summary>
    public EngineObject? MainAsset { get; private set; }

    /// <summary>Optional diagnostic when an importer returns false instead of throwing.</summary>
    public string? Error { get; private set; }

    /// <summary>Report an import failure for structured results and the editor Console.</summary>
    public bool Fail(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        Error = error;
        return false;
    }

    /// <summary>All sub-assets.</summary>
    public List<EngineObject> SubAssets { get; } = [];

    /// <summary>Asset GUIDs that this asset depends on.</summary>
    public HashSet<Guid> Dependencies { get; } = [];

    /// <summary>★ A3 — Dependencies WITH role labels (typeof(T).Name of the AssetRef). Empty for
    /// dependencies discovered without a type (e.g. the CollectAssetDependencies prefab-overrides
    /// walk). The dependency graph reads this to distinguish a texture ref from a material ref.</summary>
    public HashSet<DependencySerializationContext.Dependency> DependencyEdges { get; } = [];

    public ImportContext(Guid assetGuid, string absolutePath, Echo.EchoObject? settings)
    {
        AssetGuid = assetGuid;
        AbsolutePath = absolutePath;
        Settings = settings;
    }

    /// <summary>Set the main asset. Auto-sets Name from the file path if it isn't already set.</summary>
    public void SetMainAsset(EngineObject asset)
    {
        asset.AssetID = AssetGuid;
        if (string.IsNullOrEmpty(asset.Name))
            asset.Name = Path.GetFileNameWithoutExtension(AbsolutePath);
        MainAsset = asset;
    }

    // Display names may be duplicated by source formats. Stable ids may not: changing a stable id
    // changes serialized references, so silently suffixing one would hide an importer bug.
    private readonly HashSet<string> _usedNames = [];
    private readonly HashSet<string> _usedStableSourceIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Add a sub-asset with a deterministic GUID derived from the parent GUID + unique name.
    /// The ID is assigned immediately so AssetRef serialization works correctly.
    /// </summary>
    public void AddSubAsset(string name, EngineObject asset)
        => AddSubAsset(name, name, asset);

    /// <summary>
    /// Add a sub-asset whose identity is independent from its display name. Importers should use a
    /// persistent id from the source format (for example a PSD layer id or an Aseprite frame/layer
    /// tuple) as <paramref name="stableSourceId"/>. Renaming the source item then preserves every
    /// serialized reference to the generated asset.
    /// </summary>
    public void AddSubAsset(string stableSourceId, string displayName, EngineObject asset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableSourceId);
        ArgumentNullException.ThrowIfNull(asset);

        if (!_usedStableSourceIds.Add(stableSourceId))
            throw new InvalidOperationException($"Duplicate sub-asset stable source id '{stableSourceId}' in '{AbsolutePath}'.");

        string requestedName = string.IsNullOrWhiteSpace(displayName) ? stableSourceId : displayName;
        string uniqueName = Utils.UniqueNames.MakeUnique(requestedName, n => _usedNames.Contains(n),
            openSeparator: "_", closeSeparator: "", stripExistingSuffix: false);
        _usedNames.Add(uniqueName);

        if (string.IsNullOrEmpty(asset.Name))
            asset.Name = uniqueName;

        asset.AssetID = AssetEntry.DeriveSubAssetGuid(AssetGuid, stableSourceId);
        SubAssets.Add(asset);
    }

    /// <summary>Add a dependency on another asset (role unknown → "References").</summary>
    public void AddDependency(Guid guid)
    {
        Dependencies.Add(guid);
        DependencyEdges.Add(new DependencySerializationContext.Dependency(guid, "References"));
    }

    /// <summary>★ A3 — Add a dependency WITH a role label (the AssetRef's typeof(T).Name).</summary>
    public void AddDependency(Guid guid, string role)
    {
        Dependencies.Add(guid);
        DependencyEdges.Add(new DependencySerializationContext.Dependency(guid, role));
    }
}
