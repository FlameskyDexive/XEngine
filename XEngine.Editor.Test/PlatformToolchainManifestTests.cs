// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N1 platform-module toolchain manifests: the three repo packages parse and validate clean, and
/// the policy rules (mirror sha256 pinning, traversal-free layouts, locator-only iOS, sdkmanager
/// package lists) reject malformed manifests.
/// </summary>
public sealed class PlatformToolchainManifestTests : EditorTestHarness
{
    private static string RepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    private static ToolchainManifest ReadRepoManifest(string packageName)
    {
        string path = Path.Combine(RepoRoot(), "Packages", packageName, ToolchainManifest.FileName);
        ToolchainManifest? manifest = ToolchainManifest.Read(path);
        Assert.NotNull(manifest);
        return manifest!;
    }

    [Theory]
    [InlineData("com.xengine.platform.android", "android")]
    [InlineData("com.xengine.platform.harmonyos", "harmonyos")]
    [InlineData("com.xengine.platform.ios", "ios")]
    public void RepoManifests_ParseAndValidate(string packageName, string platform)
    {
        ToolchainManifest manifest = ReadRepoManifest(packageName);
        Assert.Equal(ToolchainManifest.CurrentManifestVersion, manifest.ManifestVersion);
        Assert.Equal(platform, manifest.Platform);
        Assert.True(ToolchainManifest.TryMapPlatform(manifest.Platform, out _));
        Assert.Empty(ToolchainManifest.Validate(manifest));
    }

    [Fact]
    public void AndroidManifest_PinsExpectedComponents()
    {
        ToolchainManifest manifest = ReadRepoManifest("com.xengine.platform.android");
        Assert.Contains(manifest.Components, c => c.Id == "jdk" && c.Kind == ToolchainComponentKind.Archive
            && !string.IsNullOrEmpty(c.Verify));
        Assert.Contains(manifest.Components, c => c.Id == "sdk" && c.Kind == ToolchainComponentKind.SdkManager
            && c.Packages.Contains("platform-tools"));
        Assert.Contains(manifest.Components, c => c.Id == "bundletool" && c.Kind == ToolchainComponentKind.File);
        // Vendor-first ordering: every component's first source is a vendor source.
        foreach (ToolchainComponent component in manifest.Components)
        {
            if (component.Kind == ToolchainComponentKind.SdkManager || component.Sources.Count == 0) continue;
            Assert.Equal(ToolchainSourceKind.Vendor, component.Sources[0].Kind);
        }
    }

    [Fact]
    public void IosManifest_IsLocatorOnly()
    {
        ToolchainManifest manifest = ReadRepoManifest("com.xengine.platform.ios");
        Assert.Empty(manifest.Components);
        ToolchainLocator locator = Assert.Single(manifest.Locators);
        Assert.Equal("xcode", locator.Id);
        Assert.Equal("xcode-select", locator.Tool);
        Assert.NotNull(locator.MinVersion);
    }

    [Fact]
    public void ReadForPlatform_ResolvesInstalledModulePackage()
    {
        // The repo Packages tree stands in for the Editor bundle (same convention as the other
        // builtin-package tests); installing the module by manifest pin makes it resolvable.
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(RepoRoot(), "Packages");
            PackageManager.Invalidate();

            var manifestFile = ProjectPackageManifest.Empty();
            manifestFile.Dependencies["com.xengine.platform.android"] = "0.1.0";
            manifestFile.Write(Path.Combine(Project.PackagesPath, "manifest.json"));
            PackageManager.Invalidate();

            ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(Project, PlatformToolchainKind.Android);
            Assert.NotNull(manifest);
            Assert.Equal("android", manifest!.Platform);
            // iOS is not pinned by this project, but the editor-level fallback (Editor bundle)
            // carries it — exactly the launcher scenario the store/bundle chain exists for.
            ToolchainManifest? ios = ToolchainManifest.ReadForPlatform(Project, PlatformToolchainKind.Ios);
            Assert.NotNull(ios);
            Assert.Equal("ios", ios!.Platform);

            // With no project AND no editor-level copy anywhere, resolution is null. The harness
            // already isolates the module store; point the bundle at an empty dir to empty both.
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(RepoRoot(), "DoesNotExist");
            PackageManager.Invalidate();
            Assert.Null(ToolchainManifest.ReadForPlatform(null, PlatformToolchainKind.Android));
            Assert.False(ToolchainManifest.IsEditorModulePresent(PlatformToolchainKind.Android));
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void Validate_RejectsMirrorWithoutSha256()
    {
        var manifest = ValidManifest();
        manifest.Components[0].Sources.Add(new ToolchainSource
        {
            Kind = ToolchainSourceKind.Mirror,
            Url = "https://example.org/mirror.zip",
        });
        List<string> problems = ToolchainManifest.Validate(manifest);
        Assert.Contains(problems, p => p.Contains("must pin a sha256", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsMalformedSha256()
    {
        var manifest = ValidManifest();
        manifest.Components[0].Sources[0].Sha256 = "not-a-hash";
        Assert.Contains(ToolchainManifest.Validate(manifest),
            p => p.Contains("malformed sha256", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsTraversalLayoutAndRootedVerify()
    {
        var manifest = ValidManifest();
        manifest.Components[0].Layout = "../escape";
        // A rooted path must be expressed per host OS: "C:/..." is only rooted on Windows.
        manifest.Components[0].Verify = OperatingSystem.IsWindows() ? "C:/abs/java.exe" : "/abs/java.exe";
        List<string> problems = ToolchainManifest.Validate(manifest);
        Assert.Contains(problems, p => p.Contains("layout", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("verify", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsRelativeUrlAndUnknownKindPolicies()
    {
        var manifest = ValidManifest();
        manifest.Components[0].Sources[0].Url = "ftp://example.org/jdk.zip";
        var sdk = new ToolchainComponent { Id = "sdk", Kind = ToolchainComponentKind.SdkManager, Layout = "sdk" };
        manifest.Components.Add(sdk);
        List<string> problems = ToolchainManifest.Validate(manifest);
        Assert.Contains(problems, p => p.Contains("not an absolute http(s) url", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("lists no packages", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDuplicateIdsAndWrongVersion()
    {
        var manifest = ValidManifest();
        manifest.ManifestVersion = 2;
        manifest.Components.Add(new ToolchainComponent
        {
            Id = "jdk",
            Kind = ToolchainComponentKind.Archive,
            Sources = { new ToolchainSource { Url = "https://example.org/jdk2.zip" } },
            Layout = "jdk2",
        });
        List<string> problems = ToolchainManifest.Validate(manifest);
        Assert.Contains(problems, p => p.Contains("manifestVersion must be", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Duplicate component id", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_LocatorManifestMustNotCarryComponents()
    {
        var manifest = ValidManifest();
        manifest.Platform = "ios";
        manifest.Locators.Add(new ToolchainLocator { Id = "xcode", Tool = "xcode-select" });
        Assert.Contains(ToolchainManifest.Validate(manifest),
            p => p.Contains("must not carry components", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsUnknownPlatform()
    {
        var manifest = ValidManifest();
        manifest.Platform = "ps5";
        Assert.Contains(ToolchainManifest.Validate(manifest),
            p => p.Contains("Unknown platform", StringComparison.Ordinal));
    }

    private static ToolchainManifest ValidManifest() => new()
    {
        ManifestVersion = ToolchainManifest.CurrentManifestVersion,
        Platform = "android",
        Components =
        {
            new ToolchainComponent
            {
                Id = "jdk",
                Kind = ToolchainComponentKind.Archive,
                Sources = { new ToolchainSource { Url = "https://example.org/jdk.zip" } },
                Layout = "jdk/17",
                Verify = "bin/java.exe",
            },
        },
    };
}
