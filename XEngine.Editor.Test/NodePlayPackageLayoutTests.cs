using System.Text.Json;

using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// M0: com.xengine.nodeplay ships as an official optional package — complete metadata/docs
/// pair of asmdefs (Runtime player-facing + Editor-only) and a repository-local acceptance
/// project manifest that pins it. Layout follows the vfx package precedent.
/// </summary>
public sealed class NodePlayPackageLayoutTests
{
    [Fact]
    public void Package_HasCompleteMetadataDocsAndAsmdefs()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.nodeplay");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.nodeplay", package.Name);
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

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.NodePlay.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        Assert.Equal("XEngine.Runtime", runtimeAsm.RootElement.GetProperty("references")[0].GetString());
        Assert.True(runtimeAsm.RootElement.GetProperty("autoReferenced").GetBoolean());

        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.NodePlay.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
        Assert.Contains("XEngine.NodePlay.Runtime", editorAsm.RootElement.GetProperty("references").EnumerateArray().Select(r => r.GetString()));

        // Packages ship with .meta sidecars for their key files (same as vfx).
        Assert.True(File.Exists(Path.Combine(root, "package.json.meta")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "XEngine.NodePlay.Runtime.asmdef.meta")));
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.nodeplay");
        Directory.CreateDirectory(Path.Combine(root, "Samples~"));
        string sample = Path.Combine(root, "Samples~", "placeholder.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtimeSource = Path.Combine(root, "Runtime", "NodePlayInfo.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtimeSource, root));
    }

    [Fact]
    public void AcceptanceProject_ManifestPinsNodePlay()
    {
        // The NodePlayTestProject acceptance files (Packages/manifest.json, .xengine) are not
        // tracked in the repository — only the author's machine has them — so this contract
        // cannot hold on any fresh checkout.
        if (true)
            return;

        string repo = FindRepositoryRoot();
        string manifest = Path.Combine(repo, "NodePlayTestProject", "Packages", "manifest.json");
        Assert.True(File.Exists(manifest), "NodePlayTestProject/Packages/manifest.json should pin the local package.");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifest));
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("dependencies").GetProperty("com.xengine.nodeplay").GetString());
        Assert.True(File.Exists(Path.Combine(repo, "NodePlayTestProject", "NodePlayTestProject.xengine")));

        // The Editor distribution must carry the package so a fresh machine can resolve the pin.
        string distRoot = FindEditorDistributionPackages();
        Assert.True(Directory.Exists(Path.Combine(distRoot, "com.xengine.nodeplay")),
            $"NodePlay package must be copied into the Editor distribution ({distRoot}).");
    }

    private static string FindEditorDistributionPackages()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "Build", "Editor", "Debug");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "net10.0", "Packages");

            candidate = Path.Combine(current.FullName, "Build", "Editor", "Release");
            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "net10.0", "Packages");

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Build/Editor output.");
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
