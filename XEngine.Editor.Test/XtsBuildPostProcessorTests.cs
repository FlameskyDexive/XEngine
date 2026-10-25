using XEngine.XTS;
using XEngine.XTS.Editor;
using XEngine.Editor.Projects.Build;
using XEngine.XTS.Editor.Build;
using System.Text.Json;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N7 gate (Windows side): the XTS build post-processor packages script
/// content into the player output, verifies the native library against the
/// package manifest, refuses builds whose scripts require missing bindings,
/// and writes a content receipt.
/// </summary>
public class XtsBuildPostProcessorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "xts-n7-" + Guid.NewGuid().ToString("N")[..8]);

    private string ProjectDir => Path.Combine(_root, "project", "scripts", "demo");
    private string OutputDir => Path.Combine(_root, "output");
    private string PackageRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Packages", "com.xengine.xts");
                if (File.Exists(Path.Combine(candidate, "package.json")))
                    return candidate;
                dir = dir.Parent!;
            }
            throw new InvalidOperationException("package not found");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private XtsToolchainResult BuildProject()
    {
        XtsToolchainService.CreateProject(ProjectDir, "demo");
        return new XtsToolchainService().Build(ProjectDir);
    }

    private void ShipNativeLibrary(string shaOverride = "")
    {
        var nativeDir = Path.Combine(OutputDir, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDir);
        var source = Path.Combine(PackageRoot, "Plugins", "Windows", "x64", "xengine_xts.dll");
        File.Copy(source, Path.Combine(nativeDir, "xengine_xts.dll"), true);
        if (shaOverride.Length > 0)
        {
            // tamper: flip bytes so the hash no longer matches the manifest
            var path = Path.Combine(nativeDir, "xengine_xts.dll");
            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
        }
    }

    [Fact]
    public async Task Process_PackagesContentAndVerifiesNativeLibrary()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var build = BuildProject();
        Assert.True(build.Success, build.Output);
        ShipNativeLibrary();

        var processor = new XtsBuildPostProcessor();
        var errors = processor.Process(new BuildPostContext
        {
            ProjectPath = _root,
            OutputDirectory = OutputDir,
            Platform = "Windows",
        });
        Assert.True(errors.Count == 0, "unexpected errors: " + string.Join("; ", errors));

        var bundled = Path.Combine(OutputDir, "Content", "XTS", "demo", "main.js");
        Assert.True(File.Exists(bundled), "bundled entry missing from player output");
        Assert.True(File.Exists(Path.Combine(OutputDir, "Content", "XTS", "receipt.json")));

        var receipt = File.ReadAllText(Path.Combine(OutputDir, "Content", "XTS", "receipt.json"));
        Assert.Contains("xengine.xts.content.v1", receipt);
        Assert.Contains("demo", receipt);
    }

    [Fact]
    public async Task Process_RejectsTamperedNativeLibrary()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var build = BuildProject();
        Assert.True(build.Success, build.Output);
        ShipNativeLibrary(shaOverride: "tamper");

        var processor = new XtsBuildPostProcessor();
        var errors = processor.Process(new BuildPostContext
        {
            ProjectPath = _root,
            OutputDirectory = OutputDir,
            Platform = "Windows",
        });
        Assert.Contains(errors, e => e.Contains("hash mismatch"));
    }

    [Fact]
    public async Task Process_RefusesBuildWhenRequiredBindingMissing()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var build = BuildProject();
        Assert.True(build.Success, build.Output);

        // the project requires a binding id the host capability set lacks
        var manifestPath = Path.Combine(ProjectDir, "xts.project.json");
        var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
        var updated = System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = "xengine.xts.project.v1",
            name = "demo",
            entry = "dist/main.js",
            requiredBindings = new[] { "engine.missing.binding" },
        });
        File.WriteAllText(manifestPath, updated);

        // host capability manifest ships with the build (without the id)
        var capDir = Path.Combine(OutputDir, "Content", "XTS");
        Directory.CreateDirectory(capDir);
        File.WriteAllText(Path.Combine(capDir, "host-capabilities.json"),
            "{\"schema\":\"xengine.xts.capabilities.v1\",\"hostVersion\":\"0.1.0\",\"bindings\":[{\"id\":\"engine.time.deltaTime\"}]}");

        ShipNativeLibrary();
        var processor = new XtsBuildPostProcessor();
        var errors = processor.Process(new BuildPostContext
        {
            ProjectPath = _root,
            OutputDirectory = OutputDir,
            Platform = "Windows",
        });
        Assert.Contains(errors, e => e.Contains("engine.missing.binding"));
    }
}
