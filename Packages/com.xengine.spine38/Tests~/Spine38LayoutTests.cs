using System.Text.Json;

using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38LayoutTests
{
    [Fact]
    public void PackageJson_ConflictsAsmdefDocsSamplesAndVersionMarker()
    {
        string root = PackageRoot();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
        JsonElement package = doc.RootElement;
        Assert.Equal("com.xengine.spine38", package.GetProperty("name").GetString());
        Assert.Equal("0.1.4", package.GetProperty("version").GetString());
        Assert.False(package.GetProperty("installByDefault").GetBoolean());
        Assert.Equal("0.1.1", package.GetProperty("xengine").GetString());
        Assert.Equal("package", package.GetProperty("type").GetString());
        Assert.Equal("Official", package.GetProperty("category").GetString());
        Assert.Contains(package.GetProperty("keywords").EnumerateArray(), k => k.GetString() == "xengine");
        Assert.Equal("com.xengine.spine", Assert.Single(package.GetProperty("conflicts").EnumerateArray()).GetString());
        Assert.Equal("0.1.0", package.GetProperty("dependencies").GetProperty("com.xengine.xgui").GetString());
        Assert.Equal("Samples~/GettingStarted", package.GetProperty("samples")[0].GetProperty("path").GetString());

        Assert.Equal("com.xengine.spine38", Spine38Runtime.PackageName);
        Assert.Equal("0.1.4", Spine38Runtime.Version);

        Assert.True(File.Exists(Path.Combine(root, "Documentation~", "index.md")));
        Assert.True(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.True(File.Exists(Path.Combine(root, "LICENSE.md")));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Spine38Runtime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "XEngine.Spine38.Runtime.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "XEngine.Spine38.Editor.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Importers", "Spine38SkeletonImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Importers", "Spine38AtlasImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Spine38Menu.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "SpinePreview.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Spine38RuntimeTools.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Rendering", "SkeletonRenderer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Rendering", "SkeletonAnimation.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Rendering", "SpineMeshGenerator.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "UI", "SkeletonGraphic.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "UI", "BoneFollowerGraphic.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Followers", "BoneFollower.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "Skeleton.shader")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "VENDOR.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "SkeletonJson.cs")));
        Assert.False(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "package.json")));
        Assert.False(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "spine-csharp.asmdef")));

        string[] csharp = Directory.GetFiles(Path.Combine(root, "Runtime", "spine-csharp"), "*.cs", SearchOption.AllDirectories);
        Assert.Equal(42, csharp.Length);
        string vendor = File.ReadAllText(Path.Combine(root, "Runtime", "spine-csharp", "VENDOR.md"));
        Assert.Contains("8b4844bd4", vendor, StringComparison.Ordinal);
        Assert.Contains("3.8.75", vendor, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "LICENSE-SPINE-EXAMPLES.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scenes", "1 The Spine GameObject.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scenes", "2 Controlling Animation.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scenes", "3 SkeletonGraphic.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scripts", "Spine38BeginnerTwo.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.atlas")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.png")));
        Assert.True(new FileInfo(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.png")).Length > 1000);

        string scene1 = File.ReadAllText(Path.Combine(root, "Samples~", "GettingStarted", "Scenes", "1 The Spine GameObject.scene"));
        Assert.Contains("XEngine.Spine38.SkeletonAnimation", scene1, StringComparison.Ordinal);
        Assert.Contains("\"AnimationName\": \"walk\"", scene1, StringComparison.Ordinal);
        Assert.Contains("7c3e1a90-4b2d-4f11-9c8a-2e6d5b1a0f33", scene1, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.atlas.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-pma.png.meta")));

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.Spine38.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        Assert.True(runtimeAsm.RootElement.GetProperty("autoReferenced").GetBoolean());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.Spine38.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void EngineTestProjects_DoNotCompileSpine38Sources()
    {
        string repo = RepositoryRoot();
        string runtimeTest = File.ReadAllText(Path.Combine(repo, "XEngine.Runtime.Test", "XEngine.Runtime.Test.csproj"));
        string editorTest = File.ReadAllText(Path.Combine(repo, "XEngine.Editor.Test", "XEngine.Editor.Test.csproj"));
        Assert.DoesNotContain("com.xengine.spine38", runtimeTest, StringComparison.Ordinal);
        Assert.DoesNotContain("com.xengine.spine38", editorTest, StringComparison.Ordinal);
    }

    private static string PackageRoot() => Spine38TestPaths.PackageRoot();

    private static string RepositoryRoot() => Spine38TestPaths.RepositoryRoot();
}
