using System.Text.Json;

using Xunit;

namespace XEngine.Hudmesh.Tests;

public sealed class HudmeshLayoutTests
{
    [Fact]
    public void PackageJson_AsmdefsDocsAndVersionMarker()
    {
        string root = PackageRoot();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
        JsonElement package = doc.RootElement;
        Assert.Equal("com.xengine.hudmesh", package.GetProperty("name").GetString());
        Assert.Equal("0.1.0", package.GetProperty("version").GetString());
        Assert.False(package.GetProperty("installByDefault").GetBoolean());
        Assert.Equal("0.1.0", package.GetProperty("xengine").GetString());
        Assert.Equal("package", package.GetProperty("type").GetString());
        Assert.Equal("Official", package.GetProperty("category").GetString());
        Assert.Contains(package.GetProperty("keywords").EnumerateArray(), k => k.GetString() == "xengine");
        Assert.Empty(package.GetProperty("dependencies").EnumerateObject());

        Assert.Equal("com.xengine.hudmesh", HudmeshRuntime.PackageName);
        Assert.Equal("0.1.0", HudmeshRuntime.Version);

        Assert.True(File.Exists(Path.Combine(root, "Documentation~", "index.md")));
        Assert.True(File.Exists(Path.Combine(root, "CHANGELOG.md")));
        Assert.True(File.Exists(Path.Combine(root, "LICENSE.md")));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "HudmeshRuntime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "HudmeshRuntime.cs.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "XEngine.Hudmesh.Runtime.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "XEngine.Hudmesh.Runtime.asmdef.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "HudmeshPackageEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "HudmeshPackageEditor.cs.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "XEngine.Hudmesh.Editor.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "XEngine.Hudmesh.Editor.asmdef.meta")));

        // Every non-meta file under Runtime/ and Editor/ carries a sibling .meta.
        foreach (string dir in new[] { "Runtime", "Editor" })
        {
            foreach (string file in Directory.GetFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                Assert.True(File.Exists(file + ".meta"), $"missing .meta for {file}");
            }
        }

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.Hudmesh.Runtime.asmdef")));
        Assert.Equal("XEngine.Hudmesh.Runtime", runtimeAsm.RootElement.GetProperty("name").GetString());
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        Assert.True(runtimeAsm.RootElement.GetProperty("autoReferenced").GetBoolean());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.Hudmesh.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void EngineTestProjects_DoNotCompileHudmeshSources()
    {
        string repo = RepositoryRoot();
        string runtimeTest = File.ReadAllText(Path.Combine(repo, "XEngine.Runtime.Test", "XEngine.Runtime.Test.csproj"));
        string editorTest = File.ReadAllText(Path.Combine(repo, "XEngine.Editor.Test", "XEngine.Editor.Test.csproj"));
        Assert.DoesNotContain("com.xengine.hudmesh", runtimeTest, StringComparison.Ordinal);
        Assert.DoesNotContain("com.xengine.hudmesh", editorTest, StringComparison.Ordinal);
    }

    private static string PackageRoot() => HudmeshTestPaths.PackageRoot();

    private static string RepositoryRoot() => HudmeshTestPaths.RepositoryRoot();
}
