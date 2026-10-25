// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Text;

using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N3 Android channel end-to-end with a FAKE sdkmanager.bat shipped through the (local-server)
/// install pipeline and executed by the real process runner: consent gate → license hashes →
/// sdkmanager delegation → sentinel verify → state reuse without re-running sdkmanager, plus the
/// license-hash self-heal retry path.
/// </summary>
public sealed class AndroidToolchainChannelTests : EditorTestHarness
{
    private readonly string _cacheRoot;
    private readonly PlatformToolchainServiceTests.LocalFileServer _server = new();

    public AndroidToolchainChannelTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "XEngineAndroidChannelTests", Guid.NewGuid().ToString("N"));
    }

    public override void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

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

    private void WriteAndroidModule(string sdkManagerScript)
    {
        _server.Serve("jdk.zip", Zip(("wrap/bin/java.exe", "jdk")));
        _server.Serve("cmdline-tools.zip", Zip(("wrap/bin/sdkmanager.bat", sdkManagerScript)));
        string dir = Path.Combine(Project.PackagesPath, "com.xengine.platform.android");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            """{ "name": "com.xengine.platform.android", "version": "0.0.0-test", "installByDefault": false }""");
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
                { "id": "sdk", "kind": "sdkmanager", "packages": ["platform-tools"],
                  "layout": "sdk", "verify": "platform-tools/adb.exe" }
              ]
            }
            """);
        PackageManager.Invalidate();
    }

    private AndroidChannelOptions Options(bool accept) => new()
    {
        Project = Project,
        CacheRootOverride = _cacheRoot,
        AcceptLicenses = accept,
        WriteEditorSettings = false,
    };

    [Fact]
    public void ConsentGate_ThenInstall_ThenCachedRerun()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        WriteAndroidModule(FakeSdkManager);

        // 1. Without consent the channel stops before any sdkmanager run.
        AndroidChannelResult gated = AndroidToolchainChannel.InstallAsync(Options(false))
            .GetAwaiter().GetResult();
        Assert.True(gated.NeedsLicenseConsent);
        Assert.False(AndroidToolchainChannel.LicensesAccepted(
            Path.Combine(_cacheRoot, "android", "sdk", "licenses")));
        Assert.False(File.Exists(Path.Combine(_cacheRoot, "android", "sdk", "platform-tools", "adb.exe")));

        // 2. With consent: license hashes written, sdkmanager ran (sentinel exists), state recorded.
        AndroidChannelResult installed = AndroidToolchainChannel.InstallAsync(Options(true))
            .GetAwaiter().GetResult();
        Assert.True(installed.Success, installed.Error);
        Assert.Equal(new[] { "platform-tools" }, installed.InstalledPackages);
        string licenseFile = Path.Combine(_cacheRoot, "android", "sdk", "licenses", "android-sdk-license");
        Assert.True(File.Exists(licenseFile));
        Assert.Contains("24333f8a63b6825ea9c5514f83c2829b004d1fee", File.ReadAllLines(licenseFile));
        Assert.True(File.Exists(Path.Combine(_cacheRoot, "android", "sdk", "platform-tools", "adb.exe")));
        string state = Path.Combine(_cacheRoot, "android", ".state", "sdk.json");
        Assert.True(File.Exists(state));
        Assert.Contains("\"managedBy\": \"sdkmanager\"", File.ReadAllText(state));

        // 3. Rerun: the sdk component is fingerprint-cached (sentinel + state) — no sdkmanager round.
        AndroidChannelResult cached = AndroidToolchainChannel.InstallAsync(Options(true))
            .GetAwaiter().GetResult();
        Assert.True(cached.Success, cached.Error);
        Assert.Empty(cached.InstalledPackages);
    }

    [Fact]
    public void SdkManagerFailure_SelfHealsLicenseHash_ThenReportsError()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        // Fails forever, but its output names a 40-hex "license hash" the channel must append once
        // before reporting the failure.
        WriteAndroidModule("""
            @echo off
            echo Review and accept: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            exit /b 1
            """);

        AndroidChannelResult failed = AndroidToolchainChannel.InstallAsync(Options(true))
            .GetAwaiter().GetResult();

        Assert.False(failed.Success);
        Assert.Contains("sdkmanager failed", failed.Error, StringComparison.Ordinal);
        string licenseFile = Path.Combine(_cacheRoot, "android", "sdk", "licenses", "android-sdk-license");
        Assert.True(File.Exists(licenseFile));
        Assert.Contains("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", File.ReadAllLines(licenseFile));
    }
}
