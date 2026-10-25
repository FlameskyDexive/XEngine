// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using XEngine.Editor.Build.HarmonyOS;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSManagedDependencyCollectorTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineHarmonyOSManagedDependencyCollectorTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadRuntimeAssemblyFiles_PrefersOpenHarmonyTargetAndSortsClosure()
    {
        Directory.CreateDirectory(_testRoot);
        string path = Path.Combine(_testRoot, "XEngine.Runtime.deps.json");
        await File.WriteAllTextAsync(path, """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "XEngine.Runtime/1.0.0": { "runtime": { "XEngine.Runtime.dll": {} } }
                },
                ".NETCoreApp,Version=v10.0/linux-musl-arm64": {
                  "XEngine.Runtime/1.0.0": { "runtime": { "XEngine.Runtime.dll": {} } },
                  "K4os.Compression.LZ4/1.3.8": { "runtime": { "lib/net6.0/K4os.Compression.LZ4.dll": {} } }
                }
              }
            }
            """, new UTF8Encoding(false));

        IReadOnlyList<string> files = HarmonyOSManagedDependencyCollector.ReadRuntimeAssemblyFiles(path);

        Assert.Equal(["K4os.Compression.LZ4.dll", "XEngine.Runtime.dll"], files);
    }

    [Theory]
    [InlineData("XEngine.Editor")]
    [InlineData("Silk.NET.OpenGL")]
    [InlineData("Mono.Android")]
    [InlineData("Vortice.DXGI")]
    public void IsForbidden_RejectsNonHarmonyRuntimeAssemblies(string name)
    {
        Assert.True(HarmonyOSManagedDependencyCollector.IsForbidden(name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}
