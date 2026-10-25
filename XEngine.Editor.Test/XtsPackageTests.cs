using System.Text.Json;
using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N1 gate for the XTS optional package: optional-package layout contract,
/// native dependency manifest validation (missing platform / wrong ABI /
/// hash mismatch are hard rejections), and a live smoke test of the managed
/// runtime over the vendored QuickJS native library.
/// </summary>
public class XtsPackageTests
{
    private static string PackageRoot
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
            throw new InvalidOperationException("com.xengine.xts package not found above the test output");
        }
    }

    private static string RepoRoot => new DirectoryInfo(PackageRoot).Parent!.Parent!.FullName;

    // ------------------------------------------------------------- layout

    [Fact]
    public void PackageLayout_FollowsOptionalPackageContract()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageRoot, "package.json"))).RootElement;
        Assert.Equal("com.xengine.xts", json.GetProperty("name").GetString());
        Assert.Equal(false, json.GetProperty("installByDefault").GetBoolean());
        Assert.Equal("Official", json.GetProperty("category").GetString());
        Assert.Equal("package", json.GetProperty("type").GetString());

        var keywords = json.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()).ToHashSet();
        foreach (var required in new[] { "xengine", "typescript", "quickjs", "hot-update" })
            Assert.Contains(required, keywords);

        var platforms = json.GetProperty("supportedPlatforms").EnumerateArray().Select(k => k.GetString()).ToHashSet();
        Assert.Contains("Windows", platforms);
        Assert.Contains("HarmonyOS", platforms);

        foreach (var file in new[]
                 {
                     "README.md", "CHANGELOG.md", "LICENSE.md", "THIRD-PARTY-NOTICES.md",
                     "Documentation~/index.md",
                     "Runtime/XEngine.XTS.Runtime.asmdef", "Editor/XEngine.XTS.Editor.asmdef",
                     "Native/native-manifest.json", "Native/source-manifest.json",
                     "Native/xengine_xts_abi/xengine_xts_abi.h",
                     "Plugins/Windows/x64/xengine_xts.dll",
                     "Plugins/Windows/x64/xengine_xts.dll.meta",
                 })
            Assert.True(File.Exists(Path.Combine(PackageRoot, file.Replace('/', Path.DirectorySeparatorChar))),
                $"missing package file: {file}");

        // Uninstalled = zero runtime: nothing outside the package may compile
        // XTS sources (the test project opts in explicitly).
        Assert.False(File.Exists(Path.Combine(RepoRoot, "XEngine.Runtime", "XTS")),
            "XTS runtime code must live inside the optional package only");
    }

    // ---------------------------------------------------- native manifest

    [Fact]
    public void NativeManifest_ValidatesShippedWindowsLibrary()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var manifest = XtsNativeManifest.Load(PackageRoot);
        Assert.Equal(XtsNativeManifest.ManifestSchema, manifest.Schema);
        Assert.Equal(1, manifest.AbiVersion);
        Assert.Equal("2025-04-26", manifest.QuickjsVersion);

        var errors = manifest.Validate("windows-x64", PackageRoot);
        Assert.True(errors.Count == 0, "windows-x64 should validate: " + string.Join("; ", errors));
    }

    [Fact]
    public void NativeManifest_RefusesPackagingUndeclaredPlatform()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var manifest = XtsNativeManifest.Load(PackageRoot);
        // harmonyos-x86_64/arm64 are declared since the ohos cross-build node;
        // a platform with no native library entry (e.g. Android) is still refused
        var errors = manifest.Validate("android-arm64", PackageRoot);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("android-arm64") && e.Contains("refused"));

        var harmonyos = manifest.Validate("harmonyos-arm64", PackageRoot);
        Assert.True(harmonyos.Count == 0, "harmonyos-arm64 should validate: " + string.Join("; ", harmonyos));
    }

    [Fact]
    public void NativeManifest_RejectsHashMismatch()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var manifest = XtsNativeManifest.Load(PackageRoot);
        var tampered = new XtsNativeManifest
        {
            Schema = manifest.Schema,
            AbiVersion = manifest.AbiVersion,
            QuickjsVersion = manifest.QuickjsVersion,
            Platforms = new Dictionary<string, XtsNativeManifestPlatform>(manifest.Platforms, StringComparer.Ordinal)
            {
                ["windows-x64"] = new()
                {
                    Library = manifest.Platforms["windows-x64"].Library,
                    Abi = "x64",
                    Sha256 = new string('0', 64),
                    Source = manifest.Platforms["windows-x64"].Source,
                    BuildId = manifest.Platforms["windows-x64"].BuildId,
                },
            },
        };
        var errors = tampered.Validate("windows-x64", PackageRoot);
        Assert.Contains(errors, e => e.Contains("hash mismatch"));
    }

    [Fact]
    public void NativeManifest_RejectsWrongAbiVersionAndMissingBuildId()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        const string json = """
            {
              "schema": "xengine.xts.native.manifest.v1",
              "abiVersion": 0,
              "quickjsVersion": "2025-04-26",
              "platforms": {
                "windows-x64": {
                  "library": "Plugins/Windows/x64/xengine_xts.dll",
                  "abi": "x64",
                  "sha256": "f297d15f3c6a3f4a78bb40fb5aec718140d3922f983608bbe24aa6ee1930cf96",
                  "source": "test"
                }
              }
            }
            """;
        var errors = XtsNativeManifest.Parse(json).ValidateStructure("windows-x64");
        Assert.Contains(errors, e => e.Contains("ABI version"));
        Assert.Contains(errors, e => e.Contains("build id"));
    }

    // ------------------------------------------------------ runtime smoke

    [Fact]
    public void XtsRuntime_NativeLibraryLoadsAndEvaluates()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        Assert.Equal(1, XtsRuntime.NativeAbiVersion);

        using var runtime = XtsRuntime.Create();
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("m.js", "export const v = 6 * 7;"));
        Assert.Equal(XtsStatus.Ok, runtime.EvalModule("m.js"));
        Assert.True(runtime.MemoryUsedBytes() > 0);

        Assert.Equal(XtsStatus.Compile, runtime.EvalModule("missing.js"));
        Assert.Contains("module not registered", runtime.LastError);

        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("bad.js", "function {( definitely not js"));
        Assert.Equal(XtsStatus.Compile, runtime.EvalModule("bad.js"));
        Assert.Contains("SyntaxError", runtime.LastError);
    }

    [Fact]
    public void XtsRuntime_MemoryLimitFailsAndRebuildRecovers()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create(new XtsRuntimeOptions { MemoryLimitBytes = 256 << 10 });
        runtime.SetExecutionDeadlineMs(2000);
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource(
            "alloc.js", "const a = []; for (let i = 0; i < 200000; i++) a.push({ x: i });"));
        Assert.Equal(XtsStatus.Limit, runtime.EvalModule("alloc.js"));
        Assert.Contains("out of memory", runtime.LastError);
        runtime.Dispose();

        // The OOM'd module pins its allocations until the runtime dies; the
        // recovery contract is rebuild-the-environment.
        using var fresh = XtsRuntime.Create();
        Assert.Equal(XtsStatus.Ok, fresh.AddModuleSource("ok.js", "export const two = 2;"));
        Assert.Equal(XtsStatus.Ok, fresh.EvalModule("ok.js"));
    }

    [Fact]
    public void XtsRuntime_ExecutionDeadlineInterruptsInfiniteLoop()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        runtime.SetExecutionDeadlineMs(100);
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("spin.js", "while (true) { }"));
        Assert.Equal(XtsStatus.Limit, runtime.EvalModule("spin.js"));
        Assert.Contains("interrupted", runtime.LastError);

        runtime.SetExecutionDeadlineMs(0);
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("ok.js", "export const two = 2;"));
        Assert.Equal(XtsStatus.Ok, runtime.EvalModule("ok.js"));
    }
}
