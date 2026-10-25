// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System.Text.Json;

using XEngine.Editor.GUI.Registries;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects.Packages;
using XEngine.PostProcessing;
using XEngine.PostProcessing.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class PostProcessingPackageLayoutTests
{
    [Fact]
    public void Package_HasOptionalOfficialMetadataRuntimeExtractAndDocs()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.postprocessing");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.postprocessing", package.Name);
        Assert.Equal("0.1.0", package.Version);
        Assert.False(package.IsInstalledByDefault);
        Assert.Equal("0.1.0", package.XEngine);
        Assert.Equal("package", package.PackageType);
        Assert.Equal("Official", package.Category);
        Assert.Equal("Documentation~/index.md", package.DocumentationUrl);
        Assert.Equal("CHANGELOG.md", package.ChangelogUrl);
        Assert.Equal("LICENSE.md", package.LicensesUrl);
        Assert.True(File.Exists(Path.Combine(root, package.DocumentationUrl!)));
        Assert.True(File.Exists(Path.Combine(root, package.ChangelogUrl!)));
        Assert.True(File.Exists(Path.Combine(root, package.LicensesUrl!)));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));

        Assert.Single(package.Samples);
        Assert.Equal("Samples~/QuickStart", package.Samples[0].Path);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "QuickStart", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "PostProcessLayer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "PostProcessVolume.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "PostProcessProfile.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "PostProcessMenu.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "PostProcessProfileEditor.cs")));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("Package Manager", docs, StringComparison.Ordinal);
        Assert.Contains("PostProcessLayer", docs, StringComparison.Ordinal);
        Assert.Contains("PostProcessVolume", docs, StringComparison.Ordinal);
        Assert.Contains(".postprofile", docs, StringComparison.Ordinal);
        string changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Assert.Contains("installByDefault", changelog, StringComparison.Ordinal);

        Assert.Equal(PostProcessRuntime.PackageName, package.Name);
        Assert.Equal(PostProcessRuntime.Version, package.Version);
        Assert.Equal(PostProcessPackageEditor.PackageName, PostProcessRuntime.PackageName);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.PostProcessing.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.PostProcessing.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.postprocessing");
        string sample = Path.Combine(root, "Samples~", "QuickStart", "README.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtime = Path.Combine(root, "Runtime", "PostProcessLayer.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtime, root));
    }

    [Fact]
    public void ScriptableObjectImporter_RegistersPostProfileExtension()
    {
        var attribute = typeof(ScriptableObjectImporter).GetCustomAttribute<ImporterForAttribute>();
        Assert.NotNull(attribute);
        Assert.Contains(".postprofile", attribute!.Extensions);
        AssetTypeStyle style = AssetTypeStyles.For(".postprofile");
        Assert.NotNull(style.Icon);
    }

    [Fact]
    public void RepoReadme_MentionsOptionalPostProcessingPackage()
    {
        string repo = FindRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(repo, "README.md"));
        Assert.Contains("com.xengine.postprocessing", readme, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
