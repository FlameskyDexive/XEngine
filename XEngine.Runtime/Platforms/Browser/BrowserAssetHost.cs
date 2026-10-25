// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Net.Http;

using XEngine.Runtime.AssetStreaming;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Mounts web content at <c>Assets/</c>. Browser uses a fetch-backed
/// <see cref="HttpAssetStreamProvider"/> (same-origin wwwroot, not a CDN for runtime wasm).
/// WeChat / Douyin use <see cref="MiniGamePackageFileProvider"/> filled from the package FS;
/// optional CDN / subpackage bytes still land in that provider after <c>downloadFile</c>.
/// </summary>
public static class BrowserAssetHost
{
    public static AssetVirtualFileSystem? FileSystem { get; private set; }

    public static HttpAssetStreamProvider? Content { get; private set; }

    public static MiniGamePackageFileProvider? Package { get; private set; }

    public static void Install(string baseHref, string contentPrefix = "Content/")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseHref);
        var http = new HttpClient { BaseAddress = new Uri(EnsureTrailingSlash(baseHref), UriKind.Absolute) };
        Install(http, contentPrefix, ownsClient: true);
    }

    public static void Install(HttpClient httpClient, string contentPrefix = "Content/", bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        Shutdown();
        Content = new HttpAssetStreamProvider(httpClient, contentPrefix, catalog: null, ownsClient);
        var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider("Assets", Content, ownsProvider: true);
        FileSystem = vfs;
    }

    /// <summary>
    /// Mini-game in-package mount. Optional <paramref name="cdnClient"/> is the AssetPlayMode.Web
    /// HTTP path for tests / browser CDN; WeChat JS must not fetch runtime wasm through it.
    /// </summary>
    public static void InstallPackage(HttpClient? cdnClient = null, string cdnPrefix = "cdn/", bool ownsCdnClient = false)
    {
        MiniGamePackageFileProvider package = new();
        HttpAssetStreamProvider? cdn = cdnClient == null
            ? null
            : new HttpAssetStreamProvider(cdnClient, cdnPrefix, catalog: null, ownsCdnClient);
        Shutdown();
        Package = package;
        Content = cdn;
        var vfs = new AssetVirtualFileSystem();
        vfs.MountProvider("Assets", package, ownsProvider: true);
        if (cdn != null)
            vfs.MountProvider("Cdn", cdn, ownsProvider: true);
        FileSystem = vfs;
    }

    public static void AddPackageFile(string path, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);
        if (Package == null || FileSystem == null)
            InstallPackage();
        Package!.Add(path, data);
    }

    public static void Shutdown()
    {
        FileSystem?.Dispose();
        FileSystem = null;
        Content = null;
        Package = null;
    }

    private static string EnsureTrailingSlash(string href) =>
        href.EndsWith('/') ? href : href + "/";
}
