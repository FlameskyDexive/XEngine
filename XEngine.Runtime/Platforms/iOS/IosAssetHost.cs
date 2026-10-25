// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.AssetStreaming;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>
/// Mounts iOS bundle <c>Content/</c> (read-only) and sandbox Documents / Caches / tmp
/// onto <see cref="AssetVirtualFileSystem"/>. Paths are strings so Windows tests do not
/// need UIKit or <c>NSBundle</c>.
/// </summary>
public static class IosAssetHost
{
    public const string AssetsMount = "Assets";
    public const string FilesMount = "Files";
    public const string CacheMount = "Cache";
    public const string TmpMount = "Tmp";

    public static AssetVirtualFileSystem? FileSystem { get; private set; }

    public static IAssetStreamProvider? ContentProvider { get; private set; }

    private static string? s_contentRoot;

    public static void Configure(
        string resourcePath,
        string contentRoot,
        string documentsRoot,
        string cachesRoot,
        string tmpRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(tmpRoot);

        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(documentsRoot);
        Directory.CreateDirectory(cachesRoot);
        Directory.CreateDirectory(tmpRoot);
        string noBackup = Path.Combine(documentsRoot, "NoBackup");
        Directory.CreateDirectory(noBackup);

        Application.ConfigurePlatformPaths(
            resourcePath,
            contentRoot,
            documentsRoot,
            cachesRoot,
            noBackup);

        ConfigureMounts(contentRoot, documentsRoot, cachesRoot, tmpRoot);
    }

    /// <summary>Mounts Content / Documents / Caches / tmp without touching <see cref="Application"/> paths.</summary>
    public static void ConfigureMounts(
        string contentRoot,
        string documentsRoot,
        string cachesRoot,
        string tmpRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(tmpRoot);

        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(documentsRoot);
        Directory.CreateDirectory(cachesRoot);
        Directory.CreateDirectory(tmpRoot);

        ResetMounts();
        s_contentRoot = Path.GetFullPath(contentRoot);
        var content = new ReadOnlyDirectoryAssetStreamProvider(contentRoot);
        var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider(AssetsMount, content, ownsProvider: false);
        vfs.MountProvider(FilesMount, new DirectoryAssetStreamProvider(documentsRoot), ownsProvider: true);
        vfs.MountProvider(CacheMount, new DirectoryAssetStreamProvider(cachesRoot), ownsProvider: true);
        vfs.MountProvider(TmpMount, new DirectoryAssetStreamProvider(tmpRoot), ownsProvider: true);
        ContentProvider = content;
        FileSystem = vfs;
    }

    /// <summary>
    /// Points <see cref="AssetDatabase"/> at packaged Content and loads a MetalLib catalog
    /// when <c>Shaders/catalog.xshadercatalog</c> is present.
    /// </summary>
    public static void BindPlayerIfConfigured(
        AssetPackagingMode packagingMode = AssetPackagingMode.LooseFiles,
        Guid defaultSceneGuid = default)
    {
        if (ContentProvider == null)
            return;

        AssetDatabase.Current = packagingMode switch
        {
            AssetPackagingMode.LooseFiles or AssetPackagingMode.XEnginePak => new PlayerAssetBackend(
                packagingMode,
                ContentProvider,
                basePath: ""),
            AssetPackagingMode.AssetBundle => BundleAssetBackend.CreateFromProvider(
                ContentProvider,
                FindAssetBundleRoots(),
                defaultSceneGuid,
                AssetBundlePlatform.iOS)
                ?? throw new InvalidDataException("iOS AssetBundle payload has no packages."),
            _ => throw new NotSupportedException(
                $"iOS player does not support {packagingMode} asset packaging."),
        };

        string catalogPath = IosApplicationPaths.ShaderCatalogPath(ContentProvider.Description);
        if (File.Exists(catalogPath))
            ShaderArtifactRuntimeLibrary.Configure(ContentProvider, "Shaders");
    }

    internal static void ResetForTests()
    {
        ResetMounts();
        AssetDatabase.Current = null;
    }

    private static void ResetMounts()
    {
        FileSystem?.Dispose();
        FileSystem = null;
        ContentProvider?.Dispose();
        ContentProvider = null;
        s_contentRoot = null;
    }

    private static string[] FindAssetBundleRoots()
    {
        string? contentRoot = s_contentRoot;
        if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
            return [];

        string[] candidates = Directory.GetDirectories(contentRoot, "*", SearchOption.TopDirectoryOnly);
        Array.Sort(candidates, StringComparer.Ordinal);
        var roots = new System.Collections.Generic.List<string>(candidates.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            string packageName = Path.GetFileName(candidates[i]);
            if (File.Exists(Path.Combine(candidates[i], packageName + ".version")))
                roots.Add(packageName);
        }
        return roots.ToArray();
    }
}
