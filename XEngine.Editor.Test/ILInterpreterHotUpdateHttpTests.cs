// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Net;
using System.Text;

using XEngine.ILInterpreter.Update;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N7 HarmonyOS-gate supplement: the hot-update delivery mechanics over a real
/// local HTTP server — publish a signed update package, download it over HTTP
/// (the transport a player uses against a CDN/local service), stage, verify and
/// activate through the production ILUpdateService. Also proves tampered
/// packages are refused after transport (signature check is independent of
/// transport trust).
/// </summary>
[Trait("Category", "Build")]
public sealed class ILInterpreterHotUpdateHttpTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "XEngineILHttp", Guid.NewGuid().ToString("N"));

    private readonly string _publicKey;
    private readonly string _privateKey;

    public ILInterpreterHotUpdateHttpTests()
    {
        Directory.CreateDirectory(_root);
        (_publicKey, _privateKey) = ILUpdateSigner.CreateKeyPair();
    }

    private static readonly HashSet<string> HostBindings = new(StringComparer.Ordinal);

    /// <summary>Serves one directory over HTTP on an ephemeral port; returns the base URL.</summary>
    private static async Task<(HttpListener Listener, string BaseUrl)> ServeAsync(string serveRoot)
    {
        // HttpListener has no local-endpoint query: grab a free port via TcpListener first.
        var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string baseUrl = $"http://127.0.0.1:{port}/";

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    string rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
                    string file = Path.GetFullPath(Path.Combine(serveRoot, rel));
                    if (!file.StartsWith(Path.GetFullPath(serveRoot), StringComparison.Ordinal)
                        || !File.Exists(file))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(file);
                    ctx.Response.ContentType = "application/octet-stream";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch (Exception) when (!listener.IsListening)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
            }
        });

        return (listener, baseUrl);
    }

    private string PublishUpdate(long version, string moduleContent, bool sign = true, bool tamperAfterSign = false)
    {
        string publishDir = Path.Combine(_root, $"publish-{version}");
        Directory.CreateDirectory(publishDir);
        string dll = Path.Combine(publishDir, "Game.Hotfix.dll");
        File.WriteAllText(dll, moduleContent);

        var manifest = new ILUpdateManifest
        {
            Version = version,
            MinimumHostVersion = "0.1.0",
        };
        manifest.Modules["Game.Hotfix.dll"] = ILUpdateManifest.ComputeModuleSha256(File.ReadAllBytes(dll));
        manifest.Signature = sign ? ILUpdateSigner.Sign(manifest, _privateKey) : Convert.ToBase64String(new byte[64]);
        File.WriteAllText(Path.Combine(publishDir, "update.json"), manifest.ToJson());

        if (tamperAfterSign)
        {
            // Server-side compromise: flip a module byte after signing. Transport
            // delivers it faithfully — the client's hash check must catch it.
            File.WriteAllText(dll, moduleContent + "TAMPERED");
        }
        return publishDir;
    }

    /// <summary>Downloads the update package over HTTP into a staging directory.</summary>
    private static async Task DownloadToStagingAsync(string baseUrl, string stagingDir)
    {
        using var http = new HttpClient();
        Directory.CreateDirectory(stagingDir);
        foreach (string name in new[] { "update.json", "Game.Hotfix.dll" })
        {
            byte[] bytes = await http.GetByteArrayAsync(baseUrl + name).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(stagingDir, name), bytes).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task HotUpdate_OverLocalHttp_ActivatesSignedPackage()
    {
        string publish = PublishUpdate(1, "http-hotfix-v1");
        var (listener, baseUrl) = await ServeAsync(publish).ConfigureAwait(false);
        try
        {
            string contentRoot = Path.Combine(_root, "player");
            var service = new ILUpdateService
            {
                ContentRoot = contentRoot,
                PublicKeyXml = _publicKey,
                HostBindingIds = HostBindings,
            };
            Assert.Equal(0, service.ActiveVersion); // baseline

            string staging = Path.Combine(_root, "staging");
            await DownloadToStagingAsync(baseUrl, staging).ConfigureAwait(false);
            Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(staging));

            Assert.Equal(1, service.ActiveVersion);
            string active = service.ResolveActiveModulesDir();
            Assert.Equal("http-hotfix-v1",
                File.ReadAllText(Path.Combine(active, "Game.Hotfix.dll")));
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }
    }

    [Fact]
    public async Task HotUpdate_SequentialVersions_OverHttp_ChainCorrectly()
    {
        var service = new ILUpdateService
        {
            ContentRoot = Path.Combine(_root, "player"),
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };

        foreach (long v in new[] { 5L, 6L, 7L })
        {
            string publish = PublishUpdate(v, $"content-v{v}");
            var (listener, baseUrl) = await ServeAsync(publish).ConfigureAwait(false);
            try
            {
                string staging = Path.Combine(_root, $"staging-{v}");
                await DownloadToStagingAsync(baseUrl, staging).ConfigureAwait(false);
                Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(staging));

                string active = service.ResolveActiveModulesDir();
                Assert.Equal($"content-v{v}",
                    File.ReadAllText(Path.Combine(active, "Game.Hotfix.dll")));
                Assert.Equal(v, service.ActiveVersion);
            }
            finally
            {
                listener.Stop();
                listener.Close();
            }
        }
    }

    [Fact]
    public async Task HotUpdate_TamperedPackage_OverHttp_IsRefusedByHash()
    {
        // v1 activates fine; v2 is tampered server-side after signing.
        var service = new ILUpdateService
        {
            ContentRoot = Path.Combine(_root, "player"),
            PublicKeyXml = _publicKey,
            HostBindingIds = HostBindings,
        };

        string p1 = PublishUpdate(1, "clean-v1");
        var (l1, url1) = await ServeAsync(p1).ConfigureAwait(false);
        try
        {
            string s1 = Path.Combine(_root, "s1");
            await DownloadToStagingAsync(url1, s1).ConfigureAwait(false);
            Assert.Equal(ILUpdateStatus.Success, service.ApplyUpdate(s1));
        }
        finally { l1.Stop(); l1.Close(); }

        string p2 = PublishUpdate(2, "clean-v2", tamperAfterSign: true);
        var (l2, url2) = await ServeAsync(p2).ConfigureAwait(false);
        try
        {
            string s2 = Path.Combine(_root, "s2");
            await DownloadToStagingAsync(url2, s2).ConfigureAwait(false);
            Assert.Equal(ILUpdateStatus.HashMismatch, service.ApplyUpdate(s2));
            Assert.Equal(1, service.ActiveVersion); // v1 still active
        }
        finally { l2.Stop(); l2.Close(); }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
