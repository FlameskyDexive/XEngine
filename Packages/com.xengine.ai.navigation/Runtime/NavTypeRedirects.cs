// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine.Runtime;
using XEngine.Runtime.Navigation;

namespace XEngine.AI.Navigation;

/// <summary>
/// Redirects pre-package Echo <c>$type</c> names (assembly <c>XEngine.Runtime</c>) to the types in
/// this assembly. Names-only so a collectible package load context is not pinned in the core registry.
/// </summary>
internal static class NavTypeRedirects
{
    [ModuleInitializer]
    [OnAssemblyLoad(Order = -1000)]
    internal static void Register()
    {
        Alias("XEngine.Runtime.Navigation.NavMeshData", typeof(NavMeshData));
        Alias("XEngine.Runtime.Navigation.NavMeshBuildSettings", typeof(NavMeshBuildSettings));
        Alias("XEngine.Runtime.Navigation.NavMeshAreaInfo", typeof(NavMeshAreaInfo));
        Alias("XEngine.Runtime.Navigation.NavMeshTileSummary", typeof(NavMeshTileSummary));
        Alias("XEngine.Runtime.Navigation.NavMeshAgent", typeof(NavMeshAgent));
        Alias("XEngine.Runtime.Navigation.NavMeshObstacle", typeof(NavMeshObstacle));
        Alias("XEngine.Runtime.Navigation.OffMeshLink", typeof(OffMeshLink));
        Alias("XEngine.Runtime.Navigation.NavMeshCollectGeometry", typeof(NavMeshCollectGeometry));
        Alias("XEngine.Runtime.Navigation.NavMeshObstacleShape", typeof(NavMeshObstacleShape));
        Alias("XEngine.Runtime.Navigation.NavMeshObstacleCarveMode", typeof(NavMeshObstacleCarveMode));

        AliasPackage(typeof(NavMeshData));
        AliasPackage(typeof(NavMeshBuildSettings));
        AliasPackage(typeof(NavMeshAreaInfo));
        AliasPackage(typeof(NavMeshTileSummary));
        AliasPackage(typeof(NavMeshAgent));
        AliasPackage(typeof(NavMeshObstacle));
        AliasPackage(typeof(OffMeshLink));
        AliasPackage(typeof(NavMeshSurface));
        AliasPackage(typeof(NavMeshModifier));
        AliasPackage(typeof(NavMeshModifierVolume));
        AliasPackage(typeof(NavMeshLink));
    }

    private static void Alias(string legacyFullName, Type target)
    {
        string targetName = target.FullName + ", " + target.Assembly.GetName().Name;
        RuntimeTypeRegistry.RegisterAlias(legacyFullName + ", XEngine.Runtime", targetName);
        RuntimeTypeRegistry.RegisterAlias(legacyFullName, targetName);
    }

    private static void AliasPackage(Type target)
    {
        string packageName = target.FullName + ", XEngine.AI.Navigation.Runtime";
        string targetName = target.FullName + ", " + target.Assembly.GetName().Name;
        RuntimeTypeRegistry.RegisterAlias(packageName, targetName);
        RuntimeTypeRegistry.RegisterAlias(target.FullName!, targetName);
    }
}
