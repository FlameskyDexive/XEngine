// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

public class TwoDPackageManifestTests
{
    private static readonly string[] BuiltInSubpackages =
    {
        "com.xengine.2d.common",
        "com.xengine.2d.sprite",
    };

    private static readonly string[] OptionalSubpackages =
    {
        "com.xengine.2d.pixel-perfect",
        "com.xengine.2d.tilemap",
        "com.xengine.2d.tilemap.extras",
        "com.xengine.2d.animation",
        "com.xengine.2d.spriteshape",
        "com.xengine.2d.aseprite",
        "com.xengine.2d.psdimporter",
        "com.xengine.2d.tooling",
    };

    private static readonly string[] Subpackages = [.. BuiltInSubpackages, .. OptionalSubpackages];

    [Fact]
    public void TwoDPackages_HaveCompleteMetadataAndDependencyClosure()
    {
        string packagesRoot = Path.Combine(FindRepositoryRoot(), "Packages");
        var knownPackages = new HashSet<string>(Subpackages, StringComparer.Ordinal);
        knownPackages.Add("com.xengine.feature.2d");

        foreach (string packageName in knownPackages)
        {
            string root = Path.Combine(packagesRoot, packageName);
            string jsonPath = Path.Combine(root, "package.json");
            Assert.True(File.Exists(jsonPath), jsonPath);
            PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(jsonPath));
            Assert.Equal(packageName, package.Name);
            Assert.Equal("0.1.0", package.Version);
            Assert.Equal("0.1.0", package.XEngine);
            Assert.False(string.IsNullOrWhiteSpace(package.Category));
            Assert.Equal("Documentation~/index.md", package.DocumentationUrl);
            Assert.Equal("CHANGELOG.md", package.ChangelogUrl);
            Assert.Equal("LICENSE.md", package.LicensesUrl);
            Assert.True(File.Exists(Path.Combine(root, package.DocumentationUrl!)));
            Assert.True(File.Exists(Path.Combine(root, package.ChangelogUrl!)));
            Assert.True(File.Exists(Path.Combine(root, package.LicensesUrl!)));

            foreach (string dependency in package.Dependencies.Keys)
                Assert.Contains(dependency, knownPackages);
        }

        PackageJson feature = Assert.IsType<PackageJson>(PackageJson.Read(
            Path.Combine(packagesRoot, "com.xengine.feature.2d", "package.json")));
        Assert.Equal("feature", feature.PackageType);
        Assert.False(feature.IsInstalledByDefault);
        Assert.Equal(Subpackages.Length, feature.Dependencies.Count);
        foreach (string subpackage in Subpackages)
            Assert.Equal("0.1.0", feature.Dependencies[subpackage]);

        foreach (string packageName in BuiltInSubpackages)
        {
            PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(
                Path.Combine(packagesRoot, packageName, "package.json")));
            Assert.True(package.IsInstalledByDefault);
        }

        foreach (string packageName in OptionalSubpackages)
        {
            PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(
                Path.Combine(packagesRoot, packageName, "package.json")));
            Assert.False(package.IsInstalledByDefault);
        }
    }

    [Fact]
    public void SpriteShapePackage_DocumentsRuntimeApiAndRegistersStarterSample()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.2d.spriteshape");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        PackageSample sample = Assert.Single(package.Samples);
        Assert.Equal("SpriteShape Starter", sample.DisplayName);
        Assert.Equal("Samples~/Starter", sample.Path);
        Assert.True(Directory.Exists(Path.Combine(root, sample.Path.Replace('/', Path.DirectorySeparatorChar))));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("SpriteShapeController", docs, StringComparison.Ordinal);
        Assert.Contains("SpriteShapeProfile", docs, StringComparison.Ordinal);
        Assert.Contains("LibTessDotNet", docs, StringComparison.Ordinal);
        Assert.Contains("PolygonCollider2D", docs, StringComparison.Ordinal);

        string starter = File.ReadAllText(Path.Combine(root, "Samples~", "Starter", "README.md"));
        Assert.Contains("SpriteShapeController", starter, StringComparison.Ordinal);
        Assert.Contains("Bake()", starter, StringComparison.Ordinal);
        Assert.DoesNotContain("will demonstrate", starter, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Feature2dDocs_DescribeReleasedSpriteShapeWorkflow()
    {
        string docs = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Packages", "com.xengine.feature.2d", "Documentation~", "index.md"));
        Assert.Contains("SpriteShapeController", docs, StringComparison.Ordinal);
        Assert.Contains("0.1.0", docs, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDAssemblyDefinitions_HaveUniqueNamesAndValidJson()
    {
        string packagesRoot = Path.Combine(FindRepositoryRoot(), "Packages");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;

        foreach (string packageName in Subpackages)
        {
            string root = Path.Combine(packagesRoot, packageName);
            foreach (string asmdefPath in Directory.EnumerateFiles(root, "*.asmdef", SearchOption.AllDirectories))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(asmdefPath));
                string name = document.RootElement.GetProperty("name").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.True(names.Add(name), $"Duplicate asmdef name: {name}");
                count++;
            }
        }

        Assert.Equal(17, count);
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
