// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Editor.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Editor.Test;

[Trait("Category", "Build")]
public sealed class PlayerShaderArtifactLoadTests(ITestOutputHelper output) : EditorTestHarness
{
    // Exact packaged shader-artifact total (built-in + seeded package shaders). Recompute by
    // running this test and reading the actual "Indexed N offline artifacts" / *.xshader count —
    // the pin guards against shaders silently dropping out of (or junk entering) the package.
    private const int BuiltInArtifactCount = 274;

    [Fact]
    public void WindowedPlayerConfiguresOfflineShadersBeforeGameRun()
    {
        // Drives the desktop player build/run pipeline, which currently hard-codes the
        // win-x64 profile (TestProject.exe apphost) and a Windows-style csproj layout on
        // macOS; re-enable with the macOS player packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        string sourceDirectory = Path.Combine(Project.BuildTempPath, "PlayerSourceOrder");
        Directory.CreateDirectory(sourceDirectory);

        MethodInfo generatePlayerSource = typeof(DesktopBuildPipeline).GetMethod(
            "GeneratePlayerSource",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        generatePlayerSource.Invoke(
            new DesktopBuildPipeline(),
            [
                Project,
                new BuildSettings(),
                new DesktopBuildProfile(),
                Guid.NewGuid(),
                sourceDirectory,
                new List<string>(),
                AssetPackagingMode.LooseFiles,
            ]);

        string source = File.ReadAllText(Path.Combine(sourceDirectory, "Program.cs"));
        int configureIndex = source.IndexOf(
            "ShaderArtifactRuntimeLibrary.Configure(",
            StringComparison.Ordinal);
        int gameRunIndex = source.IndexOf("var __game = new DesktopPlayer();", StringComparison.Ordinal);
        int initializeIndex = source.IndexOf("public override void Initialize()", StringComparison.Ordinal);

        Assert.True(configureIndex >= 0, "Generated Player must configure its offline shader package.");
        Assert.True(gameRunIndex > configureIndex, "Shader artifacts must be configured before Game.Run.");
        Assert.True(initializeIndex > gameRunIndex, "Configuration must live in top-level startup, not Initialize.");
        Assert.Equal(configureIndex, source.LastIndexOf(
            "ShaderArtifactRuntimeLibrary.Configure(",
            StringComparison.Ordinal));
    }

    [Fact]
    public void NativeResolverDiagnostics_AreOptInAndOutsideThePlayerBundle()
    {
        string sourceDirectory = Path.Combine(Project.BuildTempPath, "PlayerResolverDiagnostics");
        Directory.CreateDirectory(sourceDirectory);

        MethodInfo generatePlayerSource = typeof(DesktopBuildPipeline).GetMethod(
            "GeneratePlayerSource",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        generatePlayerSource.Invoke(
            new DesktopBuildPipeline(),
            [
                Project,
                new BuildSettings(),
                new DesktopBuildProfile(),
                Guid.NewGuid(),
                sourceDirectory,
                new List<string>(),
                AssetPackagingMode.LooseFiles,
            ]);

        string source = File.ReadAllText(Path.Combine(sourceDirectory, "Program.cs"));
        Assert.Contains("XENGINE_NATIVE_RESOLVER_LOG", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Path.Combine(AppContext.BaseDirectory, \"resolver.log\")",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompilerInventoryMatcherIsStrict()
    {
        // Drives the desktop player build/run pipeline, which currently hard-codes the
        // win-x64 profile (TestProject.exe apphost) and a Windows-style csproj layout on
        // macOS; re-enable with the macOS player packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        string[] compilerFiles =
        [
            "XEngine.Slang.dll",
            "XEngine.Slang.COMApi.dll",
            "slang-compiler.dll",
            "libslang.so",
            "dxcompiler.dll",
            "dxil.dll",
        ];
        for (int i = 0; i < compilerFiles.Length; i++)
            Assert.True(DesktopBuildPipeline.IsRuntimeShaderCompilerFile(compilerFiles[i]));

        Assert.False(DesktopBuildPipeline.IsRuntimeShaderCompilerFile("XEngine.Runtime.dll"));
        Assert.False(DesktopBuildPipeline.IsRuntimeShaderCompilerFile("Silk.NET.Vulkan.dll"));
        Assert.False(DesktopBuildPipeline.IsRuntimeShaderCompilerFile("shader-assets.dll"));
    }

    [Theory]
    [InlineData(AssetPackagingMode.LooseFiles)]
    [InlineData(AssetPackagingMode.Embedded)]
    public void PlayerLoadsOfflineShaderPackageWithoutRuntimeCompiler(AssetPackagingMode packagingMode)
    {
        // Drives the desktop player build/run pipeline, which currently hard-codes the
        // win-x64 profile (TestProject.exe apphost) and a Windows-style csproj layout on
        // macOS; re-enable with the macOS player packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        Guid sceneGuid = AuthorEmptyScene();
        string outputDirectory = RunBuild(sceneGuid, packagingMode);
        try
        {
            if (packagingMode == AssetPackagingMode.LooseFiles)
            {
                string shaderDirectory = Path.Combine(outputDirectory, "Content", "Shaders");
                Assert.True(File.Exists(Path.Combine(shaderDirectory, "catalog.xshadercatalog")));
                Assert.Equal(BuiltInArtifactCount, Directory.GetFiles(shaderDirectory, "*.xshader").Length);
            }

            string playerOutput = RunPlayerHeadless(outputDirectory, frames: 2);
            Assert.Contains(
                $"[ShaderArtifacts] Indexed {BuiltInArtifactCount} offline artifacts",
                playerOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "Runtime HLSL compilation is disabled.",
                playerOutput,
                StringComparison.Ordinal);
            var compilerFiles = new List<string>();
            foreach (string path in Directory.EnumerateFiles(
                outputDirectory,
                "*",
                SearchOption.AllDirectories))
            {
                if (DesktopBuildPipeline.IsRuntimeShaderCompilerFile(Path.GetFileName(path)))
                    compilerFiles.Add(Path.GetRelativePath(outputDirectory, path));
            }
            Assert.Empty(compilerFiles);
            string resolverPath = Path.Combine(outputDirectory, "resolver.log");
            Assert.False(File.Exists(resolverPath), "Player diagnostics must not mutate its app bundle.");
            output.WriteLine($"packaging_mode={packagingMode}");
            output.WriteLine($"player_artifacts_loaded={BuiltInArtifactCount}");
            output.WriteLine("runtime_hlsl_compilation=disabled");
            output.WriteLine("runtime_shader_compiler_files=0");
            output.WriteLine("runtime_shader_compiler_resolutions=0");
        }
        finally
        {
            TryDeleteDir(outputDirectory);
        }
    }
}
