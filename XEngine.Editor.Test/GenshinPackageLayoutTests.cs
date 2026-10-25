using System.Text.Json;

using XEngine.Editor.Projects.Packages;
using XEngine.Genshin;
using XEngine.Genshin.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class GenshinPackageLayoutTests
{
    [Fact]
    public void Package_HasCompleteMetadataDocsAndSamplePlaceholder()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.genshin");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.genshin", package.Name);
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

        Assert.Single(package.Samples);
        Assert.Equal("Samples~/CombatDemo", package.Samples[0].Path);
        Assert.Equal("Combat Demo", package.Samples[0].DisplayName);
        Assert.Contains("KeQing", package.Samples[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5-hit", package.Samples[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "CombatDemo", "README.md")));

        Assert.True(File.Exists(Path.Combine(root, "Runtime", "GenshinRuntime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "GenshinPackageEditor.cs")));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("Package Manager", docs, StringComparison.Ordinal);
        Assert.Contains("Samples~/CombatDemo", docs, StringComparison.Ordinal);
        Assert.Contains("ToonMaterial", docs, StringComparison.Ordinal);
        Assert.Contains("GenshinToonBody", docs, StringComparison.Ordinal);
        Assert.Contains("internal testing", docs, StringComparison.Ordinal);
        string changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Assert.Contains("package scaffold", changelog, StringComparison.Ordinal);
        Assert.Contains("Samples~/CombatDemo", changelog, StringComparison.Ordinal);
        string license = File.ReadAllText(Path.Combine(root, "LICENSE.md"));
        Assert.Contains("internal testing only", license, StringComparison.Ordinal);

        Assert.Equal(GenshinRuntime.PackageName, package.Name);
        Assert.Equal(GenshinRuntime.Version, package.Version);
        Assert.Equal(GenshinPackageEditor.PackageName, GenshinRuntime.PackageName);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.Genshin.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.Genshin.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.genshin");
        string sample = Path.Combine(root, "Samples~", "CombatDemo", "README.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtime = Path.Combine(root, "Runtime", "GenshinRuntime.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtime, root));
    }

    [Fact]
    public void LayoutEvidence_WritesM0Notes()
    {
        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "genshin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m0-genshin-package.md"),
            "# M0 Genshin package scaffold\n\n" +
            "Date: 2026-08-20. Branch: `main`.\n\n" +
            "`com.xengine.genshin` 0.1.0 ships `Documentation~/index.md`, `Samples~/CombatDemo` " +
            "placeholder, CHANGELOG, LICENSE (sample-assets: internal testing only). Runtime asmdef " +
            "has empty `includePlatforms` (player); Editor asmdef is Editor-only. `Samples~` is " +
            "skipped by the package asset scan. `VerifyBuiltInPackages` in XEngine.Editor.csproj " +
            "fails the editor build if any of the five marker files go missing.\n\n" +
            "`installByDefault` is false: Package Manager lists the package as Available until " +
            "Install copies it into `Library/PackageCache/com.xengine.genshin@0.1.0/`.\n\n" +
            "Plan: `docs/superpowers/specs/2026-08-20-genshin-toon-combat-demo.md`.\n\n" +
            "Screenshots: [m0-optional-package-available.png](m0-optional-package-available.png), " +
            "[m0-optional-package-installed.png](m0-optional-package-installed.png).\n");
        Assert.True(File.Exists(Path.Combine(dir, "m0-genshin-package.md")));
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
