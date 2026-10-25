// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Text;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Packages.EditorModules;
using XEngine.PackageRegistry;
using XEngine.PackageRegistry.Testing;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// One-click launcher install session: module (npm → editor module store) → Android license gate →
/// SDK channel, all against hermetic fakes (FakeNpmServer for @inxep/platform.android,
/// LocalFileServer for component archives, a fake sdkmanager.bat for sdkmanager delegation).
/// </summary>
public class PlatformModuleInstallSessionTests : EditorTestHarness
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "XEnginePlatformModuleSessionTests", Guid.NewGuid().ToString("N"));
    private readonly FakeNpmServer _npm = new();
    private readonly PlatformToolchainServiceTests.LocalFileServer _server = new();
    private readonly string _cacheRoot;
    private readonly string? _prevBuiltInRoot;
    private readonly string? _prevDisableEnv;

    private const string ModuleName = "com.xengine.platform.android";

    public PlatformModuleInstallSessionTests()
    {
        _cacheRoot = Path.Combine(_root, "sdks");
        _prevBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        // Isolate BOTH editor-level module sources (store via the harness, bundle via this) so the
        // session's module phase must go through npm — hermetic, never the repo copy.
        PackageManager.BuiltInPackagesPathOverride = Path.Combine(_root, "builtin");
        Directory.CreateDirectory(PackageManager.BuiltInPackagesPathOverride);
        _prevDisableEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, null);
    }

    public override void Dispose()
    {
        PackageManager.BuiltInPackagesPathOverride = _prevBuiltInRoot;
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, _prevDisableEnv);
        _npm.Dispose();
        _server.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static byte[] Zip(params (string name, string content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry created = archive.CreateEntry(name);
                using Stream stream = created.Open();
                stream.Write(Encoding.UTF8.GetBytes(content.Replace("\n", "\r\n")));
            }
        }
        return buffer.ToArray();
    }

    /// <summary>A stand-in sdkmanager: parses --sdk_root=&lt;path&gt; from %1, materializes the
    /// sentinel the channel verifies, and echoes success.</summary>
    private const string FakeSdkManager = """
        @echo off
        for /f "tokens=2 delims==" %%b in ("%~1") do set "ROOT=%%b\"
        mkdir "%ROOT%platform-tools" 2>nul
        type nul > "%ROOT%platform-tools\adb.exe"
        echo fake-sdkmanager ok
        """;

    /// <summary>Publish the android module to the fake registry with a fully local toolchain.json
    /// (JDK + cmdline-tools + sdkmanager component + bundletool).</summary>
    private void PublishAndroidModule()
    {
        _server.Serve("jdk.zip", Zip(("wrap/bin/java.exe", "jdk")));
        _server.Serve("cmdline-tools.zip", Zip(("wrap/bin/sdkmanager.bat", FakeSdkManager)));
        _server.Serve("bundletool.jar", "bundletool-bytes"u8.ToArray());

        string dir = Path.Combine(_root, "src-module");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            $$"""{ "name": "{{ModuleName}}", "version": "0.0.0-test", "installByDefault": false }""");
        File.WriteAllText(Path.Combine(dir, ToolchainManifest.FileName), $$"""
            {
              "manifestVersion": 1,
              "platform": "android",
              "components": [
                { "id": "jdk", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}jdk.zip" } ],
                  "layout": "jdk/17", "stripRootSegments": 1, "verify": "bin/java.exe" },
                { "id": "cmdline-tools", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}cmdline-tools.zip" } ],
                  "layout": "sdk/cmdline-tools/latest", "stripRootSegments": 1, "verify": "bin/sdkmanager.bat" },
                { "id": "bundletool", "kind": "file",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}bundletool.jar" } ],
                  "layout": "bin/bundletool.jar" },
                { "id": "sdk", "kind": "sdkmanager",
                  "packages": [ "platform-tools" ],
                  "layout": "sdk", "verify": "platform-tools/adb.exe" }
              ]
            }
            """);
        using var client = new NpmRegistryClient(new NpmRegistryOptions
        {
            RegistryUrl = _npm.RegistryUrl,
            AuthToken = "test-token",
        });
        client.PublishAsync(dir).GetAwaiter().GetResult();
    }

    private PlatformModuleInstallOptions Options(bool consent)
        => new()
        {
            Platform = PlatformToolchainKind.Android,
            Project = null,
            AcceptAndroidLicenses = consent,
            CacheRootOverride = _cacheRoot,
            WriteEditorSettings = false,
            ModuleRegistryUrl = _npm.RegistryUrl,
        };

    // ------------------------------------------------------------------
    //  Tests
    // ------------------------------------------------------------------

    [Fact]
    public void ModuleNotOnRegistry_SessionFailsWithModuleError()
    {
        var session = new PlatformModuleInstallSession();
        session.Start(Options(consent: false)).GetAwaiter().GetResult();

        Assert.Equal(PlatformModuleInstallState.Failed, session.Snapshot.State);
        Assert.NotNull(session.Snapshot.Error);
        Assert.Contains(ModuleName, session.Snapshot.Error, StringComparison.OrdinalIgnoreCase);
        // Nothing leaked into the store.
        Assert.False(EditorModuleStore.IsInstalled(ModuleName));
    }

    [Fact]
    public void FullFlow_ConsentGateThenSdkSucceeds()
    {
        // The flow's final step launches sdkmanager.bat — a Windows-host toolchain contract
        // (cmd.exe batch). Everything up to it (consent, download, extraction, sentinel
        // verification) is covered on every host; the .bat launch itself is Windows-only.
        if (!OperatingSystem.IsWindows())
            return;

        PublishAndroidModule();

        // Round 1 without consent: the module installs, the SDK waits at the license gate.
        var session = new PlatformModuleInstallSession();
        session.Start(Options(consent: false)).GetAwaiter().GetResult();
        Assert.Equal(PlatformModuleInstallState.AwaitingConsent, session.Snapshot.State);
        Assert.True(EditorModuleStore.IsInstalled(ModuleName));
        // The gate fires before any SDK component lands.
        Assert.False(Directory.Exists(_cacheRoot));

        // Round 2 with consent: full SDK install through the fake sdkmanager.
        session = new PlatformModuleInstallSession();
        session.Start(Options(consent: true)).GetAwaiter().GetResult();

        Assert.Equal(PlatformModuleInstallState.Succeeded, session.Snapshot.State);
        Assert.NotNull(session.Snapshot.SdkRoot);
        string sentinel = Path.Combine(_cacheRoot, "android", "sdk", "platform-tools", "adb.exe");
        Assert.True(File.Exists(sentinel), $"sdkmanager sentinel missing at '{sentinel}'");

        // Status detection agrees: every manifest component counted, SDK ready.
        PlatformModuleInstallSession.SdkStatus status =
            PlatformModuleInstallSession.GetSdkStatus(PlatformToolchainKind.Android, _cacheRoot);
        Assert.Equal(4, status.ComponentsTotal);
        Assert.Equal(4, status.ComponentsInstalled);
        Assert.True(status.Ready);
    }

    [Fact]
    public void GetSdkStatus_EmptyRootCountsZero()
    {
        PublishAndroidModule();
        PlatformModuleInstallSession.SdkStatus status =
            PlatformModuleInstallSession.GetSdkStatus(PlatformToolchainKind.Android, _cacheRoot);
        Assert.Equal(0, status.ComponentsInstalled);
        Assert.False(status.Ready);
        Assert.False(status.Partial);
    }

    [Fact]
    public void DefaultCacheRoot_HonorsUserConfiguredLocation()
    {
        // The launcher Settings download location redirects every channel/session/CLI install
        // that does not pass an explicit cache override. (Property only — never Save()d here, and
        // restored so the developer's real setting survives the test.)
        Theming.EditorSettings settings = Theming.EditorSettings.Instance;
        string? previous = settings.PlatformSdkCacheRoot;
        try
        {
            string custom = Path.Combine(_root, "d-drive-sdks");
            Directory.CreateDirectory(custom);

            settings.PlatformSdkCacheRoot = custom;
            Assert.Equal(custom, PlatformToolchainService.GetDefaultCacheRoot());
            Assert.Equal(Path.Combine(custom, "android"),
                PlatformToolchainService.GetInstallRoot(PlatformToolchainKind.Android));

            // Whitespace falls back to the built-in per-user root under %LOCALAPPDATA%.
            settings.PlatformSdkCacheRoot = "   ";
            Assert.Equal(PlatformToolchainService.BuiltinCacheRoot,
                PlatformToolchainService.GetDefaultCacheRoot());
        }
        finally
        {
            settings.PlatformSdkCacheRoot = previous ?? string.Empty;
        }
    }
}
