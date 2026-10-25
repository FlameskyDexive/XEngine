// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects.Packages;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class BuildShaderPackageScopeTests : EditorTestHarness
{
    public BuildShaderPackageScopeTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    [Fact]
    public void BuildDoesNotCollectDefaultsFromUninstalledPackages()
    {
        Assert.True(BuiltInAssets.IsBuiltIn(TerrainShaderLibrary.TerrainShaderGuid));
        Assert.DoesNotContain(PackageManager.Resolve(Project), package => package.Name == "com.xengine.terrain");

        VulkanShaderVariantCollection collection = MetalShaderVariantCollector.CollectForBuild([], contributors: []);

        Assert.Contains(collection.Descriptors, descriptor => descriptor.ShaderAssetGuid == BuiltInAssets.GuidFor(DefaultShader.Standard));
        Assert.DoesNotContain(collection.Descriptors, descriptor => descriptor.ShaderAssetGuid == TerrainShaderLibrary.TerrainShaderGuid);
        Assert.DoesNotContain(collection.Descriptors, descriptor => descriptor.ShaderAssetGuid == TerrainShaderLibrary.GrassShaderGuid);
    }

    [Fact]
    public void PackageDefaultsFollowInstallAndRemoveWithoutDomainReload()
    {
        SetTerrainInstalled(true);
        Assert.Contains(PackageManager.Resolve(Project), package => package.Name == "com.xengine.terrain");
        VulkanShaderVariantCollection installed = MetalShaderVariantCollector.CollectForBuild([], contributors: []);
        Assert.Contains(installed.Descriptors, descriptor =>
            descriptor.ShaderAssetGuid == TerrainShaderLibrary.TerrainShaderGuid &&
            descriptor.CanonicalKeywordSet.Contains("TERRAIN_8_LAYERS;", StringComparison.Ordinal));
        Assert.Contains(installed.Descriptors, descriptor => descriptor.ShaderAssetGuid == TerrainShaderLibrary.GrassShaderGuid);

        SetTerrainInstalled(false);
        VulkanShaderVariantCollection removed = MetalShaderVariantCollector.CollectForBuild([], contributors: []);
        Assert.DoesNotContain(removed.Descriptors, descriptor => descriptor.ShaderAssetGuid == TerrainShaderLibrary.TerrainShaderGuid);
    }

    [Fact]
    public void ExplicitPackageShaderDependencyStillCollectsAllDeclaredVariants()
    {
        VulkanShaderVariantCollection collection = MetalShaderVariantCollector.CollectForBuild(
            [TerrainShaderLibrary.TerrainShaderGuid], contributors: [], includeBuiltInShaders: false);
        Assert.NotEmpty(collection.Descriptors);
        Assert.All(collection.Descriptors, descriptor => Assert.Equal(TerrainShaderLibrary.TerrainShaderGuid, descriptor.ShaderAssetGuid));
        Assert.Contains(collection.Descriptors, descriptor => descriptor.CanonicalKeywordSet.Contains("TERRAIN_8_LAYERS;", StringComparison.Ordinal));
    }

    [Fact]
    public void CompileAndPackageUseTheSameProjectInventory()
    {
        Assert.True(ShaderImporter.CompileBuiltInOfflineArtifacts(Project));
        Assert.False(File.Exists(ShaderArtifactBuildPackager.GetImportIndexPath(Project, TerrainShaderLibrary.TerrainShaderGuid)));
        ShaderArtifactBuildPackage package = ShaderArtifactBuildPackager.Prepare(
            Project, [], Path.Combine(Project.RootPath, "ShaderPackage"));
        using var stream = File.OpenRead(package.CatalogPath);
        var catalog = XEngine.Runtime.RHI.Shaders.ShaderArtifactCatalog.Read(stream);
        Assert.Contains(BuiltInAssets.GuidFor(DefaultShader.Standard), catalog.Entries.Keys);
        Assert.DoesNotContain(TerrainShaderLibrary.TerrainShaderGuid, catalog.Entries.Keys);
        Assert.DoesNotContain(TerrainShaderLibrary.GrassShaderGuid, catalog.Entries.Keys);
    }

    private void SetTerrainInstalled(bool installed)
    {
        string path = Path.Combine(Project.PackagesPath, "manifest.json");
        ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(path);
        if (installed)
            manifest.Dependencies["com.xengine.terrain"] = "0.1.0";
        else
            manifest.Dependencies.Remove("com.xengine.terrain");
        manifest.Write(path);
        PackageManager.Invalidate();
    }
}
