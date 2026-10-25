using System.Net;
using System.Text;
using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Hot-update over HTTP, tested against a real local HTTP server (the "本地
/// 搭建 HTTP 服务" mode): publish signed content v1 (factory), serve v2 over
/// HTTP, download → verify → activate; corrupted transport and wrong-version
/// responses are refused.
/// </summary>
public class XtsContentUpdateClientTests : IDisposable
{
    private static readonly string SigningKey = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("xts-hotupdate-test-key"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "xts-hotupdate-" + Guid.NewGuid().ToString("N")[..8]);
    private HttpListener? _listener;
    private string _prefix = string.Empty;

    public void Dispose()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteContent(string name, string marker, string version)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), $"// {marker}\nglobalThis.__version = '{marker}';\n");
        File.WriteAllText(Path.Combine(dir, "config.json"), $"{{\"marker\":\"{marker}\"}}");
        var service = new XtsUpdateService(_root, SigningKey);
        service.WriteManifest(dir, version);
        return dir;
    }

    /// <summary>Starts a local HTTP server serving files under rootDir.</summary>
    private string StartServer(string rootDir)
    {
        _prefix = $"http://127.0.0.1:{Random.Shared.Next(30000, 45000)}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    var relative = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
                    var file = Path.Combine(rootDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(file))
                    {
                        var bytes = File.ReadAllBytes(file);
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    ctx.Response.Close();
                }
                catch (Exception) when (!_listener.IsListening) { }
            }
        });
        return _prefix;
    }

    [Fact]
    public async Task InstallFrom_HappyPath_DownloadsVerifiesActivates()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(WriteContent("v1", "factory-marker", "0.1.0"), "0.1.0");

        // v2 is served over HTTP (never touched locally before download)
        var servedDir = WriteContent("served-v2", "hotfix-v2", "0.2.0");
        var url = StartServer(servedDir);

        var client = new XtsContentUpdateClient(service);
        var errors = await client.InstallFromAsync(url, "0.2.0");
        Assert.True(errors.Count == 0, string.Join("; ", errors));

        var (root, problems) = service.LoadCurrentWithRollback();
        Assert.Empty(problems);
        Assert.Contains("hotfix-v2", File.ReadAllText(Path.Combine(root, "main.js")));
        // one-generation rollback copy
        Assert.Contains("factory-marker",
            File.ReadAllText(Path.Combine(service.PreviousRoot, "main.js")));
    }

    [Fact]
    public async Task InstallFrom_CorruptTransport_IsRefused()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(WriteContent("v1", "factory-marker", "0.1.0"), "0.1.0");

        var servedDir = WriteContent("served-bad", "hotfix-bad", "0.2.0");
        // corrupt a served file AFTER signing
        File.WriteAllText(Path.Combine(servedDir, "config.json"), "{\"marker\":\"TAMPERED\"}");
        var url = StartServer(servedDir);

        var client = new XtsContentUpdateClient(service);
        var errors = await client.InstallFromAsync(url, "0.2.0");
        Assert.Contains(errors, e => e.Contains("transport hash mismatch") && e.Contains("config.json"));

        // current content is untouched
        Assert.Contains("factory-marker",
            File.ReadAllText(Path.Combine(service.CurrentRoot, "main.js")));
    }

    [Fact]
    public async Task InstallFrom_WrongVersion_IsRefused()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(WriteContent("v1", "factory-marker", "0.1.0"), "0.1.0");

        var servedDir = WriteContent("served-v3", "hotfix-v3", "0.3.0");
        var url = StartServer(servedDir);

        var client = new XtsContentUpdateClient(service);
        var errors = await client.InstallFromAsync(url, "9.9.9");
        Assert.Contains(errors, e => e.Contains("0.3.0") && e.Contains("9.9.9"));
    }

    [Fact]
    public async Task InstallFrom_MissingManifest_FailsCleanly()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var service = new XtsUpdateService(_root, SigningKey);
        service.SeedFactory(WriteContent("v1", "factory-marker", "0.1.0"), "0.1.0");

        var emptyDir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyDir);
        var url = StartServer(emptyDir);

        var client = new XtsContentUpdateClient(service);
        var errors = await client.InstallFromAsync(url, "0.2.0");
        Assert.NotEmpty(errors);
    }
}
