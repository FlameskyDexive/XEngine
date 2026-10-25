// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class DesktopBuildPipelineRuntimeAssetsTests
{
    [Theory]
    [InlineData(false, "osx-arm64", 3)]
    [InlineData(true, "osx-arm64", 3)]
    [InlineData(false, "osx-x64", 0)]
    public void PublishedEditorRootNatives_AreCopiedOnlyForTheirPublishedRid(bool assemblyInRuntimes, string targetRid, int expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "XEnginePublishedNatives", Guid.NewGuid().ToString("N"));
        string engine = Path.Combine(root, "Editor");
        string output = Path.Combine(root, "Player");
        try
        {
            Directory.CreateDirectory(Path.Combine(engine, "runtimes"));
            File.WriteAllText(Path.Combine(engine, "XEngine.Editor.deps.json"),
                """{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/osx-arm64"}}""");
            File.WriteAllText(Path.Combine(engine, "libglfw.3.dylib"), "published arm64 GLFW");
            File.WriteAllText(Path.Combine(engine, "libopenal.dylib"), "published arm64 OpenAL");
            File.WriteAllText(Path.Combine(engine, "Magick.Native-Q8-arm64.dll.dylib"), "published arm64 Magick");
            File.WriteAllText(Path.Combine(engine, "libslang-compiler.dylib"), "editor compiler");
            int copied = DesktopBuildPipeline.CopyEngineRuntimeNativeLibraries(
                assemblyInRuntimes ? Path.Combine(engine, "runtimes") : engine, output, targetRid);
            Assert.Equal(expected, copied);
            Assert.Equal(expected > 0, File.Exists(Path.Combine(output, "libglfw.3.dylib")));
            Assert.Equal(expected > 0, File.Exists(Path.Combine(output, "Magick.Native-Q8-arm64.dll.dylib")));
            Assert.False(File.Exists(Path.Combine(output, "Magick.Native-Q8-x64.dll.dylib")));
            Assert.False(File.Exists(Path.Combine(output, "libslang-compiler.dylib")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PublishedRootFallback_PrefersExplicitTargetRidNative()
    {
        string root = Path.Combine(Path.GetTempPath(), "XEnginePublishedNativePreference", Guid.NewGuid().ToString("N"));
        string engine = Path.Combine(root, "Editor");
        string output = Path.Combine(root, "Player");
        string native = Path.Combine(engine, "runtimes", "osx-arm64", "native");
        try
        {
            Directory.CreateDirectory(native);
            File.WriteAllText(Path.Combine(engine, "XEngine.Editor.deps.json"),
                """{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/osx-arm64"}}""");
            File.WriteAllText(Path.Combine(engine, "libglfw.3.dylib"), "root fallback");
            File.WriteAllText(Path.Combine(native, "libglfw.3.dylib"), "explicit RID library");
            Assert.Equal(1, DesktopBuildPipeline.CopyEngineRuntimeNativeLibraries(engine, output, "osx-arm64"));
            Assert.Equal("explicit RID library", File.ReadAllText(Path.Combine(output, "runtimes", "osx-arm64", "native", "libglfw.3.dylib")));
            Assert.False(File.Exists(Path.Combine(output, "libglfw.3.dylib")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PublishedEditorRootNative_DoesNotShadowProjectPlugin()
    {
        string root = Path.Combine(Path.GetTempPath(), "XEnginePublishedNativeOverride", Guid.NewGuid().ToString("N"));
        string engine = Path.Combine(root, "Editor");
        string output = Path.Combine(root, "Player");
        string nested = Path.Combine(output, "runtimes", "osx-arm64", "native", "libglfw.3.dylib");
        try
        {
            Directory.CreateDirectory(engine);
            Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
            File.WriteAllText(Path.Combine(engine, "XEngine.Editor.deps.json"),
                """{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/osx-arm64"}}""");
            File.WriteAllText(Path.Combine(engine, "libglfw.3.dylib"), "engine");
            File.WriteAllText(nested, "project plugin");
            Assert.Equal(0, DesktopBuildPipeline.CopyEngineRuntimeNativeLibraries(engine, output, "osx-arm64"));
            Assert.Equal("project plugin", File.ReadAllText(nested));
            Assert.False(File.Exists(Path.Combine(output, "libglfw.3.dylib")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiesOnlyTargetRidNativeLibrariesFromBothEditorLayouts(bool assemblyDirectoryIsRuntimes)
    {
        string root = Path.Combine(Path.GetTempPath(), "XEngineRuntimeAssets", Guid.NewGuid().ToString("N"));
        string engineDirectory = Path.Combine(root, "Editor");
        string engineRuntimesDirectory = Path.Combine(engineDirectory, "runtimes");
        string outputDirectory = Path.Combine(root, "Player");
        string targetNativeDirectory = Path.Combine(engineRuntimesDirectory, "osx-arm64", "native");
        string otherNativeDirectory = Path.Combine(engineRuntimesDirectory, "osx-x64", "native");

        try
        {
            Directory.CreateDirectory(targetNativeDirectory);
            Directory.CreateDirectory(otherNativeDirectory);
            File.WriteAllText(Path.Combine(engineRuntimesDirectory, "MINI_AUDIO_LICENSE"), "license");
            File.WriteAllText(Path.Combine(targetNativeDirectory, "libglfw.3.dylib"), "arm64 glfw");
            File.WriteAllText(Path.Combine(targetNativeDirectory, "libopenal.dylib"), "arm64 openal");
            File.WriteAllText(Path.Combine(otherNativeDirectory, "libglfw.3.dylib"), "x64 glfw");
            File.WriteAllText(Path.Combine(engineRuntimesDirectory, "Silk.NET.Windowing.Glfw.dll"), "managed");

            string assemblyDirectory = assemblyDirectoryIsRuntimes ? engineRuntimesDirectory : engineDirectory;
            int copied = DesktopBuildPipeline.CopyEngineRuntimeNativeLibraries(
                assemblyDirectory,
                outputDirectory,
                "osx-arm64");

            string outputRuntimeDirectory = Path.Combine(outputDirectory, "runtimes");
            Assert.Equal(3, copied);
            Assert.Equal("arm64 glfw", File.ReadAllText(Path.Combine(outputRuntimeDirectory, "osx-arm64", "native", "libglfw.3.dylib")));
            Assert.Equal("arm64 openal", File.ReadAllText(Path.Combine(outputRuntimeDirectory, "osx-arm64", "native", "libopenal.dylib")));
            Assert.False(File.Exists(Path.Combine(outputRuntimeDirectory, "osx-x64", "native", "libglfw.3.dylib")));
            Assert.Equal("license", File.ReadAllText(Path.Combine(outputRuntimeDirectory, "MINI_AUDIO_LICENSE")));
            Assert.False(File.Exists(Path.Combine(outputRuntimeDirectory, "Silk.NET.Windowing.Glfw.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeepsProjectPluginWhenItAlreadyProvidesSameNativeLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), "XEngineRuntimeAssetOverride", Guid.NewGuid().ToString("N"));
        string engineDirectory = Path.Combine(root, "Editor");
        string engineNativeDirectory = Path.Combine(engineDirectory, "runtimes", "osx-arm64", "native");
        string outputNativeDirectory = Path.Combine(root, "Player", "runtimes", "osx-arm64", "native");
        string destination = Path.Combine(outputNativeDirectory, "libglfw.3.dylib");

        try
        {
            Directory.CreateDirectory(engineNativeDirectory);
            Directory.CreateDirectory(outputNativeDirectory);
            File.WriteAllText(Path.Combine(engineNativeDirectory, "libglfw.3.dylib"), "engine copy");
            File.WriteAllText(destination, "project plugin");

            int copied = DesktopBuildPipeline.CopyEngineRuntimeNativeLibraries(
                engineDirectory,
                Path.Combine(root, "Player"),
                "osx-arm64");

            Assert.Equal(0, copied);
            Assert.Equal("project plugin", File.ReadAllText(destination));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
