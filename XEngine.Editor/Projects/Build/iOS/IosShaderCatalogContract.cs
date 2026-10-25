// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Build;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// iOS shader pack must be MetalLib. Vulkan/SPIR-V catalogs are rejected rather than
/// silently substituted when this host cannot compile metallib.
/// </summary>
internal static class IosShaderCatalogContract
{
    public const string MetallibMissingError =
        "iOS shader pack requires MetalLib artifacts compiled on macOS+Xcode. Refusing an empty catalog and refusing Vulkan/SPIR-V.";

    public const string NonMetalBackendError =
        "iOS shader pack requires GraphicsBackend.Metal (MetalLib). Refusing Vulkan/SPIR-V substitution.";

    public static void RequireMetalSelections(IReadOnlyList<ShaderArtifactBuildSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0)
            throw new InvalidOperationException(MetallibMissingError);

        for (int i = 0; i < selections.Count; i++)
        {
            if (selections[i].Backend != GraphicsBackend.Metal)
                throw new InvalidOperationException(NonMetalBackendError);
        }
    }

    public static void RequireMetalCatalog(ShaderArtifactCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.HasStableEntries)
            throw new InvalidOperationException(MetallibMissingError);

        IReadOnlyList<ShaderArtifactCatalogEntry> entries = catalog.StableEntries;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Backend != GraphicsBackend.Metal)
                throw new InvalidOperationException(NonMetalBackendError);
        }
    }

    public static void RequirePreparedMetalPackage(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        string catalogPath = Path.Combine(packageDirectory, ShaderArtifactBuildPackager.CatalogFileName);
        if (!File.Exists(catalogPath))
            throw new InvalidOperationException(MetallibMissingError);

        using var stream = File.OpenRead(catalogPath);
        RequireMetalCatalog(ShaderArtifactCatalog.Read(stream));
    }
}
