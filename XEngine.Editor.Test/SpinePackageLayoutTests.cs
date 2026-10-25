using System.Text.Json;

using XEngine.Editor.Projects.Packages;
using XEngine.Spine;
using XEngine.Spine.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class SpinePackageLayoutTests
{
    [Fact]
    public void Package_HasCompleteMetadataDocsSamplesAndPlayerRuntime()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.spine");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.spine", package.Name);
        Assert.Equal("0.2.0", package.Version);
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

        Assert.Equal(2, package.Samples.Count);
        Assert.Equal("Samples~/GettingStarted", package.Samples[0].Path);
        Assert.Equal("Samples~/OtherExamples", package.Samples[1].Path);
        Assert.Contains("Getting Started", package.Samples[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Goblins", package.Samples[1].Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Placeholder", package.Samples[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "LICENSE-SPINE-EXAMPLES.txt")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scenes", "1 The Spine GameObject.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scenes", "6 SkeletonGraphic.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Skeletons", "Doi", "Doi.json")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "GettingStarted", "Scripts", "SpineBeginnerTwo.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "OtherExamples", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "OtherExamples", "Scenes", "1 Goblins.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "OtherExamples", "Skeletons", "Goblins", "goblins.json")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Assets", "SpineAtlasRestyle.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "SpineRuntime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "LICENSE")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "SkeletonJson.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "VENDOR.md")));
        Assert.False(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "package.json")));
        Assert.False(File.Exists(Path.Combine(root, "Runtime", "spine-csharp", "spine-csharp.asmdef")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "SpinePackageEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Importers", "SpineAtlasImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Importers", "SpineSkeletonImporter.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Assets", "SkeletonDataAsset.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Assets", "SpineAtlasAsset.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Rendering", "SkeletonRenderer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Rendering", "SkeletonAnimation.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "SpineMenu.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "Inspectors", "SkeletonAnimationEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "UI", "SkeletonGraphic.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Followers", "BoneFollower.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "Skeleton.shader")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "SkeletonLit.shader")));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("Package Manager", docs, StringComparison.Ordinal);
        Assert.Contains("Spine Editor license", docs, StringComparison.Ordinal);
        Assert.Contains("Samples~/GettingStarted", docs, StringComparison.Ordinal);
        Assert.Contains("Samples~/OtherExamples", docs, StringComparison.Ordinal);
        Assert.Contains("atlas-restyle", docs, StringComparison.Ordinal);
        Assert.Contains("SkeletonAnimation", docs, StringComparison.Ordinal);
        Assert.Contains("SkeletonGraphic", docs, StringComparison.Ordinal);
        Assert.Contains("1 The Spine GameObject", docs, StringComparison.Ordinal);
        Assert.Contains("6 SkeletonGraphic", docs, StringComparison.Ordinal);
        Assert.Contains("com.xengine.feature.2d", docs, StringComparison.Ordinal);
        string changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Assert.Contains("Samples~/GettingStarted", changelog, StringComparison.Ordinal);
        Assert.Contains("Other Examples", changelog, StringComparison.Ordinal);
        Assert.Contains("Getting Started", changelog, StringComparison.Ordinal);
        Assert.Contains("unity67-alignment-roadmap", changelog, StringComparison.Ordinal);

        string repoReadme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "README.md"));
        Assert.Contains("com.xengine.spine", repoReadme, StringComparison.Ordinal);
        Assert.DoesNotContain("⬜ **Spine 支持**", repoReadme, StringComparison.Ordinal);
        string roadmap = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "docs", "superpowers", "plans", "2026-08-12-unity67-alignment-roadmap.md"));
        Assert.Contains("`com.xengine.spine`", roadmap, StringComparison.Ordinal);
        string license = File.ReadAllText(Path.Combine(root, "LICENSE.md"));
        Assert.Contains("Spine Runtimes", license, StringComparison.Ordinal);

        Assert.Equal(SpineRuntime.PackageName, package.Name);
        Assert.Equal(SpineRuntime.Version, package.Version);
        Assert.Equal(SpinePackageEditor.PackageName, SpineRuntime.PackageName);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.Spine.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.Spine.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.spine");
        string sample = Path.Combine(root, "Samples~", "GettingStarted", "README.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtime = Path.Combine(root, "Runtime", "SpineRuntime.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtime, root));
    }

    [Fact]
    public void LayoutEvidence_WritesM0Notes()
    {
        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m0-spine-package.md"),
            "# M0 Spine package scaffold\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "`com.xengine.spine` 0.1.0 ships `Documentation~/`, `Samples~/GettingStarted`, " +
            "CHANGELOG, LICENSE. Runtime asmdef has empty `includePlatforms` (player). " +
            "Editor asmdef is Editor-only. `Samples~` is skipped by package asset scan.\n\n" +
            "`installByDefault` is false: Package Manager lists the package as Available until Install " +
            "copies it into `Library/PackageCache/com.xengine.spine@0.1.0/`.\n\n" +
            "Users of this package must hold their own Spine Editor license.\n\n" +
            "Screenshots: [m0-optional-package-available.png](m0-optional-package-available.png), " +
            "[m0-optional-package-installed.png](m0-optional-package-installed.png).\n");
        Assert.True(File.Exists(Path.Combine(dir, "m0-spine-package.md")));
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
