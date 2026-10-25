// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using XEngine.Editor.Build.Ios;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosManagedDependencyCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosManagedDependencyCollectorTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadRuntimeAssemblyFiles_SelectsTheRequestedRidAndSortsClosure()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "XEngine.Runtime.deps.json");
        await File.WriteAllTextAsync(path, """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0/ios-arm64": {
                  "XEngine.Runtime/1.0.0": { "runtime": { "XEngine.Runtime.dll": {} } },
                  "DeviceOnly/1.0.0": { "runtime": { "lib/netstandard2.0/DeviceOnly.dll": {} } }
                },
                ".NETCoreApp,Version=v10.0/iossimulator-arm64": {
                  "XEngine.Runtime/1.0.0": { "runtime": { "XEngine.Runtime.dll": {} } },
                  "Echo/1.0.0": { "runtime": { "lib/netstandard2.0/Echo.dll": {} } },
                  "Clay/1.0.0": { "runtime": { "lib/netstandard2.0/Clay.dll": {} } }
                }
              }
            }
            """, new UTF8Encoding(false));

        IReadOnlyList<string> files = IosManagedDependencyCollector.ReadRuntimeAssemblyFiles(
            path,
            "iossimulator-arm64");

        Assert.Equal(["Clay.dll", "Echo.dll", "XEngine.Runtime.dll"], files);
    }

    [Fact]
    public async Task ReadRuntimeAssemblyFiles_RejectsMissingRidAndRuntimeRoot()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "XEngine.Runtime.deps.json");
        await File.WriteAllTextAsync(path, """
            { "targets": { ".NETCoreApp,Version=v10.0/ios-arm64": {
              "Echo/1.0.0": { "runtime": { "Echo.dll": {} } }
            } } }
            """, new UTF8Encoding(false));

        InvalidDataException missingRoot = Assert.Throws<InvalidDataException>(
            () => IosManagedDependencyCollector.ReadRuntimeAssemblyFiles(path, "ios-arm64"));
        Assert.Contains("omits XEngine.Runtime.dll", missingRoot.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(
            () => IosManagedDependencyCollector.ReadRuntimeAssemblyFiles(path, "iossimulator-arm64"));
    }

    [Theory]
    [InlineData("XEngine.Editor")]
    [InlineData("XEngine.Slang")]
    [InlineData("Silk.NET.OpenGL")]
    [InlineData("Vortice.DXGI")]
    [InlineData("dxcompiler")]
    public void IsForbidden_RejectsDesktopAndEditorAssemblies(string name)
    {
        Assert.True(IosManagedDependencyCollector.IsForbidden(name));
    }

    [Fact]
    public void FindRuntimeProject_ResolvesFromTheLoadedRuntimeAssembly()
    {
        string runtime = typeof(XEngine.Runtime.EngineObject).Assembly.Location;
        string projectPath = IosRuntimePayloadBuilder.FindRuntimeProject(runtime);

        Assert.EndsWith(Path.Combine("XEngine.Runtime", "XEngine.Runtime.csproj"), projectPath, StringComparison.Ordinal);
        Assert.True(File.Exists(projectPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
