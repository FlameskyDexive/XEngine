// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Net;
using System.Text;

using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N2 install core against a local HTTP file stub: download → verify → extract → atomic publish →
/// fingerprint cache reuse (proven with the server down), integrity-tamper rejection, path-traversal
/// rejection, tar.gz round-trip, single-file components, resume from a partial .part, and the
/// sdkmanager pending-channel report.
/// </summary>
public sealed class PlatformToolchainServiceTests : EditorTestHarness
{
    private readonly string _cacheRoot;
    private readonly LocalFileServer _server = new();

    public PlatformToolchainServiceTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "XEnginePlatformToolchainTests", Guid.NewGuid().ToString("N"));
    }

    public override void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_cacheRoot, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>Seed a fake platform module as a project embed package whose toolchain.json points
    /// at the local stub (the real packages' vendor URLs never get touched by tests).</summary>
    private void WriteTestModule(string toolchainJson)
    {
        string dir = Path.Combine(Project.PackagesPath, "com.xengine.platform.harmonyos");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"),
            """{ "name": "com.xengine.platform.harmonyos", "version": "0.0.0-test", "installByDefault": false }""");
        File.WriteAllText(Path.Combine(dir, ToolchainManifest.FileName), toolchainJson);
        PackageManager.Invalidate();
    }

    private static string ZipFile(Dictionary<string, byte[]> entries)
    {
        string path = Path.Combine(Path.GetTempPath(), "xengine-tc-test-" + Guid.NewGuid().ToString("N") + ".zip");
        using FileStream output = File.Create(path);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (KeyValuePair<string, byte[]> entry in entries)
        {
            ZipArchiveEntry created = archive.CreateEntry(entry.Key);
            using Stream stream = created.Open();
            stream.Write(entry.Value);
        }
        return path;
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    // ------------------------------------------------------------------
    //  Tests
    // ------------------------------------------------------------------

    [Fact]
    public void ArchiveComponent_DownloadsExtractsPublishesAndCaches()
    {
        string zipPath = ZipFile(new Dictionary<string, byte[]>
        {
            ["wrap/bin/tool.exe"] = Text("tool-bytes"),
            ["wrap/README.txt"] = Text("readme"),
        });
        byte[] zipBytes = File.ReadAllBytes(zipPath);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)).ToLowerInvariant();
        _server.Serve("artifact.zip", zipBytes);
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "tool", "kind": "archive", "version": "1.0",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.zip", "sha256": "{{sha}}" } ],
                  "layout": "tool/1.0", "stripRootSegments": 1, "verify": "bin/tool.exe" }
              ]
            }
            """);
        var stages = new List<string>();

        PlatformToolchainInstallResult first = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot,
            new Progress<PlatformToolchainProgress>(p => stages.Add(p.Stage))).GetAwaiter().GetResult();

        Assert.True(first.Success, string.Join("; ", first.Components.Select(c => c.Error)));
        PlatformToolchainComponentResult component = Assert.Single(first.Components);
        Assert.Equal(PlatformToolchainComponentStatus.Installed, component.Status);
        string installed = Path.Combine(_cacheRoot, "harmonyos", "tool", "1.0", "bin", "tool.exe");
        Assert.True(File.Exists(installed));
        Assert.Equal(sha, component.Sha256);
        Assert.Contains("download", stages);
        Assert.Contains("extract", stages);
        Assert.Contains("installed", stages);

        // Fingerprint cache: with the server gone the identical install must succeed offline.
        _server.Dispose();
        PlatformToolchainInstallResult second = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();
        Assert.True(second.Success);
        Assert.Equal(PlatformToolchainComponentStatus.Cached, Assert.Single(second.Components).Status);
    }

    [Fact]
    public void PinnedShaMismatch_FailsWithoutPublishing()
    {
        _server.Serve("artifact.zip", Text("this is not the artifact the manifest pinned"));
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "tool", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.zip", "sha256": "{{new string('a', 64)}}" } ],
                  "layout": "tool/1.0", "verify": "bin/tool.exe" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.False(result.Success);
        PlatformToolchainComponentResult component = Assert.Single(result.Components);
        Assert.Equal(PlatformToolchainComponentStatus.Failed, component.Status);
        Assert.Contains("Integrity mismatch", component.Error);
        Assert.False(File.Exists(Path.Combine(_cacheRoot, "harmonyos", "tool", "1.0", "bin", "tool.exe")));
    }

    [Fact]
    public void TraversalZipEntry_IsRejectedAndNothingEscapes()
    {
        string zipPath = ZipFile(new Dictionary<string, byte[]> { ["../evil.txt"] = Text("evil") });
        _server.Serve("artifact.zip", File.ReadAllBytes(zipPath));
        string outside = Path.Combine(_cacheRoot, "evil.txt");
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "tool", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.zip" } ],
                  "layout": "tool/1.0", "verify": "bin/tool.exe" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Contains("escapes", Assert.Single(result.Components).Error, StringComparison.Ordinal);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public void SlashlessDirectoryEntries_ExtractAsDirectories()
    {
        // Regression (found against the real Temurin / cmdline-tools zips): directory markers
        // stored as zero-length entries WITHOUT a trailing slash must extract as directories,
        // not hit File.Create on a directory path.
        string zipPath = ZipFile(new Dictionary<string, byte[]>
        {
            ["wrap/bin"] = Array.Empty<byte>(),
            ["wrap/bin/java.exe"] = Text("jdk"),
            ["wrap/lib"] = Array.Empty<byte>(),
            ["wrap/lib/tools.jar"] = Text("tools"),
        });
        _server.Serve("artifact.zip", File.ReadAllBytes(zipPath));
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "tool", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.zip" } ],
                  "layout": "jdk/17", "stripRootSegments": 1, "verify": "bin/java.exe" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.True(result.Success, string.Join("; ", result.Components.Select(c => c.Error)));
        string root = Path.Combine(_cacheRoot, "harmonyos", "jdk", "17");
        Assert.True(File.Exists(Path.Combine(root, "bin", "java.exe")));
        Assert.True(File.Exists(Path.Combine(root, "lib", "tools.jar")));
    }

    [Fact]
    public void TarGzComponent_RoundTrips()
    {
        string tgz = Path.Combine(Path.GetTempPath(), "xengine-tc-test-" + Guid.NewGuid().ToString("N") + ".tgz");
        using (FileStream output = File.Create(tgz))
        {
            using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            using var writer = new System.Formats.Tar.TarWriter(gzip);
            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "ohos-sdk/native/file.txt")
            {
                DataStream = new MemoryStream(Text("native-bytes")),
            });
        }
        _server.Serve("artifact.tgz", File.ReadAllBytes(tgz));
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "sdk", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.tgz" } ],
                  "layout": "ohos-sdk", "stripRootSegments": 1, "verify": "native/file.txt" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.True(result.Success, string.Join("; ", result.Components.Select(c => c.Error)));
        Assert.Equal("native-bytes",
            File.ReadAllText(Path.Combine(_cacheRoot, "harmonyos", "ohos-sdk", "native", "file.txt")));
    }

    [Fact]
    public void NestedZipsUnderHostFolder_AreUnpackedIntoLayout()
    {
        // Regression (found against the real OpenHarmony combined SDK): the outer tar.gz holds
        // per-component zips under windows/; unpackZipsUnder materializes native/… + toolchains/.
        byte[] innerZip;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipArchiveEntry native = archive.CreateEntry("native/oh-uni-package.json");
                using (Stream stream = native.Open())
                    stream.Write(Text("""{ "api": "13" }"""));
                ZipArchiveEntry hdc = archive.CreateEntry("toolchains/hdc.exe");
                using (Stream hdcStream = hdc.Open())
                    hdcStream.Write(Text("hdc"));
            }
            innerZip = buffer.ToArray();
        }

        string tgz = Path.Combine(Path.GetTempPath(), "xengine-tc-test-" + Guid.NewGuid().ToString("N") + ".tgz");
        using (FileStream output = File.Create(tgz))
        {
            using var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true);
            using var writer = new System.Formats.Tar.TarWriter(gzip);
            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile,
                "ohos-sdk/windows/native-windows-x64.zip") { DataStream = new MemoryStream(innerZip) });
        }
        _server.Serve("artifact.tgz", File.ReadAllBytes(tgz));
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "sdk", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.tgz" } ],
                  "layout": "ohos-sdk", "stripRootSegments": 1, "unpackZipsUnder": "windows",
                  "verify": "native/oh-uni-package.json" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.True(result.Success, string.Join("; ", result.Components.Select(c => c.Error)));
        string root = Path.Combine(_cacheRoot, "harmonyos", "ohos-sdk");
        Assert.True(File.Exists(Path.Combine(root, "native", "oh-uni-package.json")));
        Assert.True(File.Exists(Path.Combine(root, "toolchains", "hdc.exe")));
    }

    [Fact]
    public void FileComponent_CopiesVerbatim()
    {
        byte[] jar = Text("pretend jar bytes");
        _server.Serve("bundletool.jar", jar);
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "jar", "kind": "file",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}bundletool.jar" } ],
                  "layout": "bundletool/bundletool.jar" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.Equal("pretend jar bytes",
            File.ReadAllText(Path.Combine(_cacheRoot, "harmonyos", "bundletool", "bundletool.jar")));
    }

    [Fact]
    public void SdkManagerComponent_ReportsPendingChannel()
    {
        WriteTestModule("""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "sdk", "kind": "sdkmanager", "packages": ["platform-tools"], "layout": "sdk" }
              ]
            }
            """);

        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        // PendingChannel is a plan report, not a failure — the Android node owns the orchestration.
        Assert.True(result.Success);
        Assert.Equal(PlatformToolchainComponentStatus.PendingChannel, Assert.Single(result.Components).Status);
    }

    [Fact]
    public void PartialDownload_ResumesViaRangeRequest()
    {
        byte[] payload = Text(new string('x', 4096) + "-tail");
        _server.Serve("artifact.zip", payload, supportRange: true);
        WriteTestModule($$"""
            {
              "manifestVersion": 1,
              "platform": "harmonyos",
              "components": [
                { "id": "tool", "kind": "archive",
                  "sources": [ { "kind": "vendor", "url": "{{_server.Url}}artifact.zip" } ],
                  "layout": "tool/1.0", "verify": "bin/tool.exe" }
              ]
            }
            """);

        // Seed the .part left behind by an interrupted install: the same deterministic download
        // target the service uses (<installRoot>/.downloads/artifact-<id><ext>), holding the first
        // half of the payload.
        string target = Path.Combine(_cacheRoot, "harmonyos", ".downloads", "artifact-tool.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target + ".part", payload[..(payload.Length / 2)]);
        _server.RequestedRange = -1;

        // The component content is deliberately plain text, not a zip: the resume contract is that
        // the downloader completes the .part into the FULL payload (Range observed, byte length
        // exact) and the service then fails cleanly at extraction instead of corrupting state.
        PlatformToolchainInstallResult result = PlatformToolchainService.InstallAsync(
            Project, PlatformToolchainKind.HarmonyOS, _cacheRoot).GetAwaiter().GetResult();

        Assert.True(_server.RequestedRange >= 0, "the downloader should have resumed with a Range request");
        byte[] completed = File.ReadAllBytes(target);
        Assert.Equal(payload.Length, completed.Length);
        Assert.Equal(payload, completed);
        Assert.False(result.Success);
        Assert.Equal(PlatformToolchainComponentStatus.Failed, Assert.Single(result.Components).Status);
        Assert.Contains("InvalidDataException", Assert.Single(result.Components).Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Minimal local file server with optional Range support (internal: shared with
    //  AndroidToolchainChannelTests).
    // ------------------------------------------------------------------

    internal sealed class LocalFileServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Thread _loop;
        private readonly CancellationTokenSource _shutdown = new();
        private int _disposed;

        public long RequestedRange { get; set; } = -1;

        public string Url => _listener.Prefixes.First();

        public LocalFileServer()
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            // "localhost" — the managed (non-Windows) HttpListener never matches 127.0.0.1 prefixes.
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _loop = new Thread(ServeLoop) { IsBackground = true };
            _loop.Start();
        }

        public void Serve(string name, byte[] content, bool supportRange = false)
        {
            _files[name] = content;
            _supportRange[name] = supportRange;
        }

        private readonly Dictionary<string, bool> _supportRange = new(StringComparer.OrdinalIgnoreCase);

        private void ServeLoop()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (Exception) { return; }
                string name = context.Request.Url!.AbsolutePath.TrimStart('/');
                if (!_files.TryGetValue(name, out byte[]? content))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                long from = 0;
                string? range = context.Request.Headers["Range"];
                if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    long.TryParse(range["bytes=".Length..].Split('-')[0], out from);
                    RequestedRange = from;
                }
                bool rangeServed = from > 0 && _supportRange.GetValueOrDefault(name);
                context.Response.StatusCode = rangeServed ? 206 : 200;
                if (rangeServed)
                {
                    context.Response.Headers["Content-Range"] = $"bytes {from}-{content.Length - 1}/{content.Length}";
                    context.Response.ContentLength64 = content.Length - from;
                }
                else
                {
                    context.Response.ContentLength64 = content.Length;
                }
                byte[] body = rangeServed ? content[(int)from..] : content;
                context.Response.OutputStream.Write(body, 0, body.Length);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            try { _listener.Close(); } catch (ObjectDisposedException) { }
        }
    }
}
