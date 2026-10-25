// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Runtime.AssetStreaming;
using XEngine.Runtime.Platforms.iOS;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class IosAssetHostTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosAssetHostTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Configure_MountsReadOnlyContentAndWritableSandbox()
    {
        string resource = Path.Combine(_root, "Bundle");
        string content = IosApplicationPaths.BundleContentPath(resource);
        string documents = Path.Combine(_root, "Documents");
        string caches = Path.Combine(_root, "Library", "Caches");
        string tmp = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(Path.Combine(content, "Shaders"));
        File.WriteAllText(Path.Combine(content, "scene.txt"), "bundle-scene");

        try
        {
            IosAssetHost.ConfigureMounts(content, documents, caches, tmp);

            AssetVirtualFileSystem vfs = Assert.IsType<AssetVirtualFileSystem>(IosAssetHost.FileSystem);
            Assert.False(vfs.IsWritableMount(IosAssetHost.AssetsMount));
            Assert.True(vfs.IsWritableMount(IosAssetHost.FilesMount));
            Assert.True(vfs.IsWritableMount(IosAssetHost.CacheMount));
            Assert.True(vfs.IsWritableMount(IosAssetHost.TmpMount));

            using Stream stream = Assert.IsAssignableFrom<Stream>(vfs.OpenRead("Assets/scene.txt"));
            using var reader = new StreamReader(stream);
            Assert.Equal("bundle-scene", reader.ReadToEnd());

            using (Stream write = vfs.OpenWrite("Files/save.bin"))
                write.WriteByte(7);
            Assert.True(File.Exists(Path.Combine(documents, "save.bin")));
            Assert.Throws<NotSupportedException>(() => vfs.OpenWrite("Assets/scene.txt"));
        }
        finally
        {
            IosAssetHost.ResetForTests();
        }
    }

    [Fact]
    public void BindPlayerIfConfigured_UsesPackagedContentProvider()
    {
        string resource = Path.Combine(_root, "BindBundle");
        string content = IosApplicationPaths.BundleContentPath(resource);
        Directory.CreateDirectory(Path.Combine(content, "Shaders"));
        File.WriteAllBytes(Path.Combine(content, "asset_manifest.bin"), [1, 2, 3]);
        AssetBackendBase? previous = AssetDatabase.Current;

        try
        {
            IosAssetHost.ConfigureMounts(
                content,
                Path.Combine(_root, "Docs"),
                Path.Combine(_root, "Caches"),
                Path.Combine(_root, "Tmp"));
            IosAssetHost.BindPlayerIfConfigured();

            Assert.IsType<PlayerAssetBackend>(AssetDatabase.Current);
            Assert.False(ShaderArtifactRuntimeLibrary.IsConfigured);
        }
        finally
        {
            IosAssetHost.ResetForTests();
            AssetDatabase.Current = previous;
        }
    }

    [Fact]
    public void BindPlayerIfConfigured_RejectsAssetBundleModeWithoutCollectedPackages()
    {
        string content = IosApplicationPaths.BundleContentPath(Path.Combine(_root, "BundleMode"));

        try
        {
            IosAssetHost.ConfigureMounts(
                content,
                Path.Combine(_root, "BundleModeDocs"),
                Path.Combine(_root, "BundleModeCaches"),
                Path.Combine(_root, "BundleModeTmp"));

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => IosAssetHost.BindPlayerIfConfigured(AssetPackagingMode.AssetBundle));
            Assert.Equal("iOS AssetBundle payload has no packages.", exception.Message);
        }
        finally
        {
            IosAssetHost.ResetForTests();
        }
    }

    [Fact]
    public void ApplicationPaths_DocumentsAndCachesAreDistinct()
    {
        string documents = Path.Combine(_root, "DocsA");
        string caches = Path.Combine(_root, "CachesA");
        Assert.Equal(Path.GetFullPath(documents), IosApplicationPaths.PersistentDataPath(documents));
        Assert.Equal(Path.GetFullPath(caches), IosApplicationPaths.TemporaryCachePath(caches));
        Assert.NotEqual(
            IosApplicationPaths.PersistentDataPath(documents),
            IosApplicationPaths.TemporaryCachePath(caches));
        Assert.EndsWith("Content", IosApplicationPaths.BundleContentPath(Path.Combine(_root, "Res")));
    }

    public void Dispose()
    {
        IosAssetHost.ResetForTests();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
