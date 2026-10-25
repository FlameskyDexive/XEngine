using System.Text.Json;

using Xunit;

namespace XEngine.GpuAnimation.Tests;

public sealed class GpuAnimationLayoutTests
{
    [Fact]
    public void PackageJson_ManifestFieldsConstsDocsSamplesAndAsmdefs()
    {
        string root = PackageRoot();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
        JsonElement package = doc.RootElement;
        Assert.Equal("com.xengine.gpu-animation", package.GetProperty("name").GetString());
        Assert.Equal("0.1.1", package.GetProperty("version").GetString());
        Assert.False(package.GetProperty("installByDefault").GetBoolean());
        Assert.Equal("0.1.1", package.GetProperty("xengine").GetString());
        Assert.Equal("package", package.GetProperty("type").GetString());
        Assert.Equal("Official", package.GetProperty("category").GetString());
        Assert.Contains(package.GetProperty("keywords").EnumerateArray(), k => k.GetString() == "xengine");
        Assert.Contains(package.GetProperty("keywords").EnumerateArray(), k => k.GetString() == "vat");
        Assert.Equal("Samples~/Crowd", package.GetProperty("samples")[0].GetProperty("path").GetString());

        Assert.Equal("com.xengine.gpu-animation", GpuAnimationRuntime.PackageName);
        Assert.Equal("0.1.1", GpuAnimationRuntime.Version);

        Assert.True(File.Exists(Path.Combine(root, "Documentation~", "index.md")));
        Assert.True(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.True(File.Exists(Path.Combine(root, "LICENSE.md")));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "GpuAnimationRuntime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "BakedGpuAnimation.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "GpuAnimMath.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "GpuAnimationBaker.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "GpuAnimatedCrowd.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "GpuAnimationShaderLibrary.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "GpuAnimUnlit.shader")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "GpuAnimLit.shader")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "XEngine.GpuAnimation.Runtime.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "GpuAnimationPackageEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "GpuAnimationAssetImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "GpuAnimationBakerWindow.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "XEngine.GpuAnimation.Editor.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Crowd", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "package.json.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "XEngine.GpuAnimation.Runtime.asmdef.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "XEngine.GpuAnimation.Editor.asmdef.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Crowd", "README.md.meta")));

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.GpuAnimation.Runtime.asmdef")));
        Assert.Equal("XEngine.GpuAnimation.Runtime", runtimeAsm.RootElement.GetProperty("name").GetString());
        Assert.Equal("XEngine.Runtime", runtimeAsm.RootElement.GetProperty("references")[0].GetString());
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        Assert.True(runtimeAsm.RootElement.GetProperty("autoReferenced").GetBoolean());

        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.GpuAnimation.Editor.asmdef")));
        Assert.Equal("XEngine.GpuAnimation.Editor", editorAsm.RootElement.GetProperty("name").GetString());
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
        Assert.Contains(editorAsm.RootElement.GetProperty("references").EnumerateArray(),
            r => r.GetString() == "XEngine.GpuAnimation.Runtime");
    }

    [Fact]
    public void EngineTestProjects_DoNotCompileGpuAnimationSources()
    {
        string repo = RepositoryRoot();
        string runtimeTest = File.ReadAllText(Path.Combine(repo, "XEngine.Runtime.Test", "XEngine.Runtime.Test.csproj"));
        string editorTest = File.ReadAllText(Path.Combine(repo, "XEngine.Editor.Test", "XEngine.Editor.Test.csproj"));
        Assert.DoesNotContain("com.xengine.gpu-animation", runtimeTest, StringComparison.Ordinal);
        Assert.DoesNotContain("com.xengine.gpu-animation", editorTest, StringComparison.Ordinal);
    }

    private static string PackageRoot() => GpuAnimationTestPaths.PackageRoot();

    private static string RepositoryRoot() => GpuAnimationTestPaths.RepositoryRoot();
}
