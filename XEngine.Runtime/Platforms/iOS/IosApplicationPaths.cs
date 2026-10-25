// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>
/// iOS sandbox path helpers. The generated UIKit host resolves
/// <c>NSBundle</c> / Documents / Library/Caches / tmp and passes the strings here so
/// Runtime never references Microsoft.iOS.
/// </summary>
public static class IosApplicationPaths
{
    public const string ContentFolderName = "Content";
    public const string ShaderCatalogFileName = "catalog.xshadercatalog";

    public static string BundleContentPath(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        return Path.Combine(resourcePath, ContentFolderName);
    }

    public static string ShaderCatalogPath(string contentRoot) =>
        Path.Combine(contentRoot, "Shaders", ShaderCatalogFileName);

    public static string PersistentDataPath(string documentsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsDirectory);
        return Path.GetFullPath(documentsDirectory);
    }

    public static string TemporaryCachePath(string cachesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachesDirectory);
        return Path.GetFullPath(cachesDirectory);
    }

    public static string ScratchPath(string temporaryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        return Path.GetFullPath(temporaryDirectory);
    }
}
