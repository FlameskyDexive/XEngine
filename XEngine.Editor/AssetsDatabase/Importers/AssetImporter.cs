using System;

using XEngine.Echo;

namespace XEngine.Editor.Importers;

/// <summary>
/// Attribute to register an importer for specific file extensions.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ImporterForAttribute : Attribute
{
    public string[] Extensions { get; }

    /// <summary>
    /// Importer selection priority for extensions handled by more than one importer. Higher values
    /// win. Equal priorities are reported as registration conflicts so discovery order can never
    /// silently decide which importer owns an asset type.
    /// </summary>
    public int Priority { get; set; }

    public ImporterForAttribute(params string[] extensions) => Extensions = extensions;
}

/// <summary>
/// Base class for all asset importers. Each importer handles specific file types
/// and converts raw files into runtime EngineObjects via the ImportContext.
/// </summary>
public abstract class AssetImporter
{
    /// <summary>Version number for cache invalidation. Bump when import logic changes.</summary>
    public abstract int Version { get; }

    /// <summary>
    /// Import a raw asset file. Push results into ctx via SetMainAsset/AddSubAsset.
    /// </summary>
    /// <returns>True if import succeeded.</returns>
    public abstract bool Import(ImportContext ctx);

    /// <summary>
    /// When an importer shares a common extension (for example <c>.json</c>), return false so
    /// discovery falls back to <see cref="DefaultImporter"/>.
    /// </summary>
    public virtual bool CanImport(string absolutePath) => true;

    /// <summary>
    /// Returns default settings for this importer type. Override to provide defaults.
    /// </summary>
    public virtual EchoObject? DefaultSettings() => null;

    /// <summary>
    /// When true, files handled by this importer are not real runtime assets and are excluded from
    /// build packaging (e.g. scripts, plugins, assembly definitions). Plugins are still shipped, but
    /// through the dedicated plugin pipeline rather than the asset manifest.
    /// </summary>
    /// <summary>
    /// Thread affinity for bulk imports (project open / watcher batches). When false (default),
    /// <see cref="Import"/> may run on the background import worker: it must only do file IO,
    /// CPU parsing/decoding, and self-contained Graphics.Submit GPU uploads - never touch editor
    /// single-threaded state (EditorAssetBackend dictionaries, ThumbnailGenerator, package mounts)
    /// or run user script code. When true the import always runs inline on the main thread
    /// (importers that read main-only state or execute user callbacks).
    /// </summary>
    public virtual bool RequiresMainThread => false;

    public virtual bool IsEditorOnlyAsset => false;
}
