// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N4 HarmonyOS channel against a local stub: ohos-sdk tar.gz installs into the managed root with
/// resolver-compatible layout (native/oh-uni-package.json), hdc is located from toolchains, an
/// unhosted remote bundle yields actionable guidance, and SkipBundle / local-archive paths route
/// through the manifest-overload install.
/// </summary>
public sealed class HarmonyOSToolchainChannelTests : EditorTestHarness
{
    private readonly string _cacheRoot;
    private readonly PlatformToolchainServiceTests.LocalFileServer _server = new();

    public HarmonyOSToolchainChannelTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "XEngineHarmonyOSChannelTests", Guid.NewGuid().ToString("N"));
    }

    public override void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    private static byte[] TarGz(params (string name, string content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            using var writer = new TarWriter(gzip);
            foreach ((string name, string content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                });
            }
        }
        return buffer.ToArray();
    }

    private void WriteHarmonyOSModule(bool withRemoteBundle)
    {
        string bundleJson = withRemoteBundle
            ? ",\n    { \"id\": \"sdk-bundle\", \"kind\": \"archive\", \"sources\": [ { \"kind\": \"vendor\", \"url\": \"" + _server.Url + "missing-bundle.zip\" } ], \"layout\": \"bundle/x\", \"verify\": \"SHA256SUMS\" }"
            : "";
        string dir = Path.Combine(Project.PackagesPath, "com.xengine.platform.harmonyos");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            """{ "name": "com.xengine.platform.harmonyos", "version": "0.0.0-test", "installByDefault": false }""");
        File.WriteAllText(Path.Combine(dir, ToolchainManifest.FileName), $$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "ohos-sdk", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}ohos-sdk.tgz" } ],
                  "layout": "ohos-sdk", "stripRootSegments": 1, "verify": "native/oh-uni-package.json" }
                  {{bundleJson}}
              ]
            }
            """);
        PackageManager.Invalidate();
    }

    private HarmonyOSChannelOptions Options(bool skipBundle = false, string? localBundle = null) => new()
    {
        Project = Project,
        CacheRootOverride = _cacheRoot,
        SkipBundle = skipBundle,
        LocalBundleArchivePath = localBundle,
        WriteEditorSettings = false,
    };

    [Fact]
    public void OhosSdk_Installs_LocatesHdc_AndUnhostedBundleGivesGuidance()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        _server.Serve("ohos-sdk.tgz", TarGz(
            ("ohos-sdk/native/oh-uni-package.json", """{ "api_version": "13" }"""),
            ("ohos-sdk/toolchains/hdc.exe", "hdc-bytes")));
        WriteHarmonyOSModule(withRemoteBundle: true);

        HarmonyOSChannelResult result = HarmonyOSToolchainChannel.InstallAsync(Options())
            .GetAwaiter().GetResult();

        // SDK half succeeded with resolver-compatible semantics; the unhosted bundle explains itself.
        Assert.NotNull(result.OfficialSdkRoot);
        string sdkRoot = Path.Combine(_cacheRoot, "harmonyos", "ohos-sdk");
        Assert.True(File.Exists(Path.Combine(sdkRoot, "native", "oh-uni-package.json")));
        Assert.Equal(Path.Combine(sdkRoot, "toolchains", "hdc.exe"), result.HdcPath);
        Assert.NotNull(result.BundleImportError);
        Assert.Contains("GitHub Releases feed does not host", result.BundleImportError, StringComparison.Ordinal);

        // Rerun is fingerprint-cached (server gone) and still resolves hdc.
        _server.Dispose();
        HarmonyOSChannelResult cached = HarmonyOSToolchainChannel.InstallAsync(Options())
            .GetAwaiter().GetResult();
        Assert.NotNull(cached.HdcPath);
        Assert.Equal(PlatformToolchainComponentStatus.Cached,
            Assert.Single(cached.Core.Components, c => c.Id == "ohos-sdk").Status);
    }

    [Fact]
    public void SkipBundle_InstallsSdkOnlyWithoutBundleError()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        _server.Serve("ohos-sdk.tgz", TarGz(
            ("ohos-sdk/native/oh-uni-package.json", "{}"),
            ("ohos-sdk/toolchains/hdc.exe", "hdc")));
        WriteHarmonyOSModule(withRemoteBundle: true);

        HarmonyOSChannelResult result = HarmonyOSToolchainChannel.InstallAsync(Options(skipBundle: true))
            .GetAwaiter().GetResult();

        Assert.True(result.Success, result.Error ?? result.BundleImportError);
        Assert.Null(result.BundleImportError);
        Assert.Single(result.Core.Components, c => c.Id == "ohos-sdk");
        Assert.DoesNotContain(result.Core.Components, c => c.Id == "sdk-bundle");
    }
}
