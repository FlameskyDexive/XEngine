// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Editor.Build.Ios;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosShaderCatalogContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosShaderCatalogTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequireMetalSelections_RejectsEmptyAndVulkan()
    {
        InvalidOperationException empty = Assert.Throws<InvalidOperationException>(
            () => IosShaderCatalogContract.RequireMetalSelections([]));
        Assert.Equal(IosShaderCatalogContract.MetallibMissingError, empty.Message);

        var vulkan = new ShaderArtifactBuildSelection(
            Guid.NewGuid(),
            "Forward",
            GraphicsBackend.Vulkan,
            string.Empty);
        InvalidOperationException wrong = Assert.Throws<InvalidOperationException>(
            () => IosShaderCatalogContract.RequireMetalSelections([vulkan]));
        Assert.Equal(IosShaderCatalogContract.NonMetalBackendError, wrong.Message);

        IosShaderCatalogContract.RequireMetalSelections(
        [
            new ShaderArtifactBuildSelection(Guid.NewGuid(), "Forward", GraphicsBackend.Metal, string.Empty),
        ]);
    }

    [Fact]
    public void RequireMetalCatalog_AcceptsMetalAndRejectsVulkan()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Parse(new string('A', 64));
        var metal = new ShaderArtifactCatalog(
        [
            new ShaderArtifactCatalogEntry(
                Guid.NewGuid(),
                "Unlit",
                "Forward",
                GraphicsBackend.Metal,
                "metal",
                string.Empty,
                key),
        ]);
        IosShaderCatalogContract.RequireMetalCatalog(metal);

        var vulkan = new ShaderArtifactCatalog(
        [
            new ShaderArtifactCatalogEntry(
                Guid.NewGuid(),
                "Unlit",
                "Forward",
                GraphicsBackend.Vulkan,
                "spirv_1_3",
                string.Empty,
                key),
        ]);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => IosShaderCatalogContract.RequireMetalCatalog(vulkan));
        Assert.Equal(IosShaderCatalogContract.NonMetalBackendError, ex.Message);
    }

    [Fact]
    public void RequirePreparedMetalPackage_FailsWhenCatalogIsMissing()
    {
        Directory.CreateDirectory(_root);
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => IosShaderCatalogContract.RequirePreparedMetalPackage(_root));
        Assert.Equal(IosShaderCatalogContract.MetallibMissingError, ex.Message);
    }

    [Fact]
    public void CopyMetalShaderPackage_DoesNotCopyWhenCatalogIsMissing()
    {
        Directory.CreateDirectory(_root);
        string emptyPackage = Path.Combine(_root, "empty");
        string destination = Path.Combine(_root, "Content", "Shaders");
        Directory.CreateDirectory(emptyPackage);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => IosAssetPackager.CopyMetalShaderPackage(emptyPackage, destination));
        Assert.Equal(IosShaderCatalogContract.MetallibMissingError, ex.Message);
        Assert.False(Directory.Exists(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
