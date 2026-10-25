using System.IO;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class PackageWatcherFilterTests
{
    private static FileSystemEventArgs Evt(WatcherChangeTypes type, string name)
        => new(type, @"C:\proj\Packages", name);

    [Fact]
    public void ArbitraryJsonEdit_IsIgnored()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.Ignore,
            EditorAssetBackend.ClassifyPackageWatcherEvent(Evt(WatcherChangeTypes.Changed, "com.foo/data.json")));
    }

    [Fact]
    public void LockFile_IsIgnored()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.Ignore,
            EditorAssetBackend.ClassifyPackageWatcherEvent(
                Evt(WatcherChangeTypes.Changed, "packages-lock.json")));
    }

    [Fact]
    public void ScriptEdit_RecompilesWithoutRemount()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.RecompileOnly,
            EditorAssetBackend.ClassifyPackageWatcherEvent(
                Evt(WatcherChangeTypes.Changed, "com.foo/Runtime/Foo.cs")));
    }

    [Fact]
    public void ScriptCreate_Remounts()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.Remount,
            EditorAssetBackend.ClassifyPackageWatcherEvent(
                Evt(WatcherChangeTypes.Created, "com.foo/Runtime/Foo.cs")));
    }

    [Fact]
    public void PackageJsonChange_Remounts()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.Remount,
            EditorAssetBackend.ClassifyPackageWatcherEvent(
                Evt(WatcherChangeTypes.Changed, "com.foo/package.json")));
    }

    [Fact]
    public void NewPackageFolder_Remounts()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.Remount,
            EditorAssetBackend.ClassifyPackageWatcherEvent(
                Evt(WatcherChangeTypes.Created, "com.foo")));
    }

    [Fact]
    public void NestedRuntimeFolder_IsIgnored()
    {
        Assert.Equal(
            EditorAssetBackend.PackageWatcherAction.Ignore,
            EditorAssetBackend.ClassifyPackageWatcherEvent(
                Evt(WatcherChangeTypes.Created, @"com.foo\Runtime")));
    }
}
