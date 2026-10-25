// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

using XEngine.Runtime;

namespace XEngine.Terrain;

/// <summary>
/// Redirects pre-package Echo <c>$type</c> names to the types in this assembly. Names-only so a
/// collectible package load context is not pinned in the core registry.
/// </summary>
internal static class TerrainTypeRedirects
{
    [ModuleInitializer]
    [OnAssemblyLoad(Order = -1000)]
    internal static void Register()
    {
        Alias("XEngine.Runtime.Terrain.TerrainComponent", typeof(TerrainComponent));
        Alias("XEngine.Runtime.TerrainCollider", typeof(TerrainCollider));
        Alias("XEngine.Runtime.TerrainData", typeof(TerrainData));
        Alias("XEngine.Runtime.TerrainLayer", typeof(TerrainLayer));
        Alias("XEngine.Runtime.DetailPrototype", typeof(DetailPrototype));
        Alias("XEngine.Runtime.TreePrototype", typeof(TreePrototype));
        Alias("XEngine.Runtime.TreeInstance", typeof(TreeInstance));

        // Samples and editor Echo stamp XEngine.Terrain.Runtime. Tests compile package sources
        // into XEngine.Runtime.Test, so the package assembly name must still resolve.
        AliasPackage(typeof(TerrainComponent));
        AliasPackage(typeof(TerrainCollider));
        AliasPackage(typeof(TerrainData));
        AliasPackage(typeof(TerrainLayer));
        AliasPackage(typeof(WindZone));
        AliasPackage(typeof(DetailPrototype));
        AliasPackage(typeof(TreePrototype));
        AliasPackage(typeof(TreeInstance));
    }

    private static void Alias(string legacyFullName, Type target)
    {
        string targetName = target.FullName + ", " + target.Assembly.GetName().Name;
        RuntimeTypeRegistry.RegisterAlias(legacyFullName + ", XEngine.Runtime", targetName);
        RuntimeTypeRegistry.RegisterAlias(legacyFullName, targetName);
    }

    private static void AliasPackage(Type target)
    {
        string packageName = target.FullName + ", XEngine.Terrain.Runtime";
        string targetName = target.FullName + ", " + target.Assembly.GetName().Name;
        RuntimeTypeRegistry.RegisterAlias(packageName, targetName);
        RuntimeTypeRegistry.RegisterAlias(target.FullName!, targetName);
    }
}
