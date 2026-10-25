// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text;

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class HarmonyOSVirtualFileSystemTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSVirtualFileSystemTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PackagedRead_CannotEscapeTheContentRoot()
    {
        HarmonyOSVirtualFileSystem vfs = CreateFileSystem();

        Assert.Throws<UnauthorizedAccessException>(() => vfs.OpenRead("../outside"));
    }

    [Fact]
    public void ContentIsReadOnlyWhilePersistentStorageIsWritable()
    {
        HarmonyOSVirtualFileSystem vfs = CreateFileSystem();
        Directory.CreateDirectory(vfs.ContentRoot);
        File.WriteAllText(Path.Combine(vfs.ContentRoot, "scene.asset"), "content", new UTF8Encoding(false));

        using Stream input = vfs.OpenRead("scene.asset");
        using var reader = new StreamReader(input, Encoding.UTF8);
        Assert.Equal("content", reader.ReadToEnd());
        Assert.Throws<UnauthorizedAccessException>(() => vfs.OpenWrite(HarmonyOSVirtualPathRoot.Content, "new.asset"));

        using (Stream output = vfs.OpenWrite(HarmonyOSVirtualPathRoot.Persistent, "save/state.bin"))
            output.WriteByte(7);
        Assert.Equal(1, new FileInfo(Path.Combine(vfs.PersistentRoot, "save", "state.bin")).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private HarmonyOSVirtualFileSystem CreateFileSystem()
    {
        return new HarmonyOSVirtualFileSystem(
            Path.Combine(_testRoot, "Content"),
            Path.Combine(_testRoot, "Persistent"),
            Path.Combine(_testRoot, "Cache"),
            Path.Combine(_testRoot, "Temp"));
    }
}
