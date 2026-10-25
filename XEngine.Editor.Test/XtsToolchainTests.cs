using XEngine.XTS;
using XEngine.XTS.Editor;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N4 gate for the XTS TypeScript toolchain: project template creation, the
/// install → typecheck → bundle pipeline, offline rebuilds from local
/// node_modules, pure-JS npm dependencies, and located diagnostics for
/// player-illegal Node API dependencies.
/// </summary>
public class XtsToolchainTests : IDisposable
{
    private readonly string _projectDir =
        Path.Combine(Path.GetTempPath(), "xts-n4-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    [Fact]
    public void CreateProject_WritesUsableTemplate()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        XtsToolchainService.CreateProject(_projectDir, "demo-scripts");
        Assert.True(File.Exists(Path.Combine(_projectDir, "package.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, "tsconfig.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, "src", "main.ts")));
        Assert.Contains("demo-scripts", File.ReadAllText(Path.Combine(_projectDir, "package.json")));
        Assert.False(File.Exists(Path.Combine(_projectDir, "dist", "main.js")));
    }

    [Fact]
    public void Build_PureJsDependency_TypeChecksBundlesAndRuns()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        XtsToolchainService.CreateProject(_projectDir, "demo-scripts");

        var service = new XtsToolchainService();
        var result = service.Build(_projectDir);
        Assert.True(result.Success, $"{result.Step} failed:\n{result.Output}");

        // the bundled module is plain ES2020: evaluate it on the runtime and
        // read back the exported global
        using var runtime = XtsRuntime.Create();
        var bundled = File.ReadAllText(Path.Combine(_projectDir, "dist", "main.js"));
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(
            "globalThis.__bootMessage = undefined; undefined", out _));
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(bundled, out _));
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("globalThis.__bootMessage", out var boot));
        Assert.Contains("moved 1.50m", boot.AsString());
        Assert.True(boot.AsString().Contains("boot"));
    }

    [Fact]
    public void Build_ReRunWithUnreachableRegistry_StaysOffline()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        XtsToolchainService.CreateProject(_projectDir, "demo-scripts");
        var service = new XtsToolchainService();
        Assert.True(service.Build(_projectDir).Success, "first build failed");

        // a rebuild must run entirely from local node_modules: point npm at
        // an unreachable registry so any hidden network dependency fails
        var environment = new Dictionary<string, string>
        {
            ["npm_config_registry"] = "http://127.0.0.1:1/",
            ["npm_config_offline"] = "true",
        };
        var result = service.Build(_projectDir, environment);
        Assert.True(result.Success, $"offline rebuild failed:\n{result.Output}");
        Assert.True(File.Exists(Path.Combine(_projectDir, "dist", "main.js")));
    }

    [Fact]
    public void Build_NodeApiDependency_ProducesLocatedDiagnostic()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        XtsToolchainService.CreateProject(_projectDir, "demo-scripts");
        File.WriteAllText(Path.Combine(_projectDir, "src", "naughty.ts"),
            "import * as fs from \"node:fs\";\nexport const sneaky = () => fs.existsSync(\".\");\n");
        File.WriteAllText(Path.Combine(_projectDir, "src", "main.ts"),
            "import { sneaky } from \"./naughty.js\";\nglobalThis.__sneaky = sneaky();\n");

        var service = new XtsToolchainService();
        var result = service.Build(_projectDir);
        Assert.False(result.Success, "a node:fs import must not build");
        Assert.Contains(result.Diagnostics, d => d.Contains("node api dependency") && d.Contains("node:fs"));
        Assert.Contains("node:fs", result.Output);
    }
}
