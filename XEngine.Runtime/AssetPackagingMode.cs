namespace XEngine.Runtime;

/// <summary>
/// How assets are packaged in a built player.
/// </summary>
public enum AssetPackagingMode
{
    /// <summary>Individual {guid}.asset files in an Assets/ folder. Debug-friendly.</summary>
    LooseFiles,

    /// <summary>ZipArchive .xenginepak files. Production desktop builds. Streamable, auto-split.</summary>
    XEnginePak,

    /// <summary>
    /// XEngine AssetBundles (the new YooAsset-style pipeline). Each package is a set of *.bundle
    /// files (custom XEBN container, LZ4-compressed, Echo payloads) plus a PackageManifest. Built by
    /// AssetBundleBuilder from an AssetBundleCollectorSetting; loaded at runtime via the
    /// XAssets/AssetPackage handle API. Coexists with the legacy modes above.
    /// </summary>
    AssetBundle,

    /// <summary>Embedded resources in the player assembly. Required for web/WASM builds.</summary>
    Embedded,
}
