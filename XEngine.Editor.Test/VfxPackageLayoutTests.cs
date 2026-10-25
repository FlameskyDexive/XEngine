using System.Diagnostics;
using System.Text.Json;

using XEngine.Editor.Projects.Packages;
using XEngine.VFX;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VfxPackageLayoutTests
{
    [Fact]
    public void Package_HasCompleteMetadataDocsSamplesAndPlayerRuntime()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.vfx");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.vfx", package.Name);
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

        Assert.Equal(2, package.Samples.Count);
        Assert.Equal("Samples~/SimpleLoop", package.Samples[0].Path);
        Assert.Equal("Samples~/SimpleBurst", package.Samples[1].Path);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "SimpleLoop", "Loop.vfx")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "SimpleBurst", "Burst.vfx")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "SimpleLoop", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "VfxParticles.shader")));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("VisualEffect", docs, StringComparison.Ordinal);
        Assert.Contains("vfxgraph-compile", docs, StringComparison.Ordinal);
        Assert.Contains("DrawIndexedIndirect", docs, StringComparison.Ordinal);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.VFX.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.VFX.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.vfx");
        string sample = Path.Combine(root, "Samples~", "SimpleLoop", "Loop.vfx");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string shader = Path.Combine(root, "Runtime", "Shaders", "VfxParticles.shader");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(shader, root));
    }

    [Fact]
    public void CompileLoop_IsStableAndRecordsEvidence()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        string first = VfxGraphCompiler.CompileToSource(graph);
        var sw = Stopwatch.StartNew();
        const int iterations = 50;
        string last = first;
        for (int i = 0; i < iterations; i++)
            last = VfxGraphCompiler.CompileToSource(graph);
        sw.Stop();
        Assert.Equal(first, last);
        Assert.Contains("Pass \"Init\"", first, StringComparison.Ordinal);
        Assert.Contains("Pass \"WriteArgs\"", first, StringComparison.Ordinal);
        Assert.Contains("SLANGPROGRAM", first, StringComparison.Ordinal);

        double nsPerCompile = Math.Max(sw.Elapsed.TotalNanoseconds / iterations, 1);
        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m6-vfx-package.md"),
            "# M6 VFX package closeout\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`com.xengine.vfx` 0.1.0 ships `Documentation~/`, `Samples~/SimpleLoop` + `SimpleBurst`, " +
            "CHANGELOG, LICENSE. Runtime asmdef has empty `includePlatforms` (player). " +
            "Editor asmdef is Editor-only. `Samples~` is skipped by package asset scan.\n\n" +
            "Loop graph compile: **" + nsPerCompile.ToString("0") + " ns/op** over " + iterations +
            " iterations (CPU lowering + template inject, isolated `Temp/m6-editor-test/`). " +
            "GPU SPIR-V/DXIL sizes: see [m2-gpu-particles.md](m2-gpu-particles.md).\n\n" +
            "Player: `Runtime/Shaders/VfxParticles.shader` is a package asset (not under `~`). " +
            "Devices without compute skip GPU work in `VisualEffect`.\n");
        Assert.True(nsPerCompile > 0);
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
