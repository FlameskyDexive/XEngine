// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;

namespace XEngine.PackageRegistry.Testing;

/// <summary>One recorded registry request, for assertions.</summary>
public sealed record FakeNpmRequest(string Method, string Path, string? Authorization);

/// <summary>A stored published version: its (rewritten) metadata plus the raw tarball.</summary>
public sealed record FakeNpmVersion(JsonObject Metadata, byte[] Tarball, string TarballFileName);

/// <summary>
/// In-process npm-protocol registry for tests: serves packuments, version metadata, the search
/// endpoint, tarball downloads, and accepts npm publish PUTs — the same routes
/// <see cref="NpmRegistryClient"/> speaks — over a local <see cref="HttpListener"/>. Deterministic
/// and offline; the editor and CLI integration tests point clients at <see cref="RegistryUrl"/>.
///
/// <para>Failure injection: set <see cref="TamperTarball"/> to corrupt downloads (integrity
/// mismatch), or <see cref="ExpectedToken"/> to require a bearer token on publish (401 otherwise).</para>
/// </summary>
public sealed class FakeNpmServer : IDisposable
{
    private HttpListener _listener;
    private readonly Thread _loop;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly ConcurrentDictionary<string, SortedDictionary<string, FakeNpmVersion>> _packages = new(StringComparer.Ordinal);

    /// <summary>All accepted publish documents in arrival order (parsed JSON).</summary>
    public ConcurrentQueue<JsonObject> PublishedDocuments { get; } = new();

    public ConcurrentQueue<FakeNpmRequest> Requests { get; } = new();

    /// <summary>When set, publish PUTs must carry this bearer token or receive 401.</summary>
    public string? ExpectedToken { get; set; }

    /// <summary>When set, publish PUTs must carry this one-time password (npm-otp header) or 401.</summary>
    public string? ExpectedOtp { get; set; }

    /// <summary>When true, served tarballs are corrupted so integrity verification fails.</summary>
    public bool TamperTarball { get; set; }

    // NOTE: on non-Windows the managed HttpListener only accepts a literal "localhost" prefix —
    // an 127.0.0.1 prefix never matches and every request times out (Windows http.sys accepts
    // both), so the URL must stay host-name based.
    public string RegistryUrl => "http://localhost:" + Port + "/";
    public int Port { get; }

    /// <param name="port">Explicit port to bind, or 0 to probe a free one.</param>
    public FakeNpmServer(int port = 0)
    {
        Port = port == 0 ? ProbeFreePort() : port;
        _listener = new HttpListener();
        _listener.Prefixes.Add(RegistryUrl);
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            if (port != 0) throw; // an explicit port is a contract; a probed port may retry below.
            // The probe released the port moments ago; a race with another process is rare but
            // possible — retry a couple of times before giving up.
            bool started = false;
            for (int attempt = 0; attempt < 3 && !started; attempt++)
            {
                Thread.Sleep(50);
                try
                {
                    _listener.Close();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(RegistryUrl);
                    _listener.Start();
                    started = true;
                }
                catch (HttpListenerException) { }
            }
            if (!started) throw;
        }
        _loop = new Thread(ServeLoop) { IsBackground = true, Name = "FakeNpmServer" };
        _loop.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }
        _shutdown.Dispose();
    }

    private int _disposed;

    /// <summary>Directly seed a version without going through the publish protocol.</summary>
    public void Seed(string npmName, string version, JsonObject metadata, byte[] tarball, string tarballFileName)
    {
        SortedDictionary<string, FakeNpmVersion> versions =
            _packages.GetOrAdd(npmName, static _ => new SortedDictionary<string, FakeNpmVersion>(VersionComparer.Instance));
        lock (versions)
        {
            versions[version] = new FakeNpmVersion(metadata, tarball, tarballFileName);
        }
    }

    /// <summary>All stored package names (wire names), sorted.</summary>
    public IReadOnlyList<string> PackageNames => _packages.Keys.OrderBy(static n => n, StringComparer.Ordinal).ToList();

    private void ServeLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            try
            {
                Handle(context);
            }
            catch (Exception ex)
            {
                TryRespond(context, HttpStatusCode.InternalServerError, ex.ToString());
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        string rawPath = request.RawUrl ?? "/";
        string path = rawPath.Split('?')[0];
        Requests.Enqueue(new FakeNpmRequest(request.HttpMethod, path, request.Headers["Authorization"]));

        // Always drain the request body before responding so keep-alive connections stay usable
        // even on early-reject paths (401/400).
        byte[] body = Array.Empty<byte>();
        if (request.HasEntityBody)
        {
            using var buffer = new MemoryStream();
            request.InputStream.CopyTo(buffer);
            body = buffer.ToArray();
        }

        if (path == "/-/v1/search")
        {
            HandleSearch(context, request.QueryString["text"] ?? "");
            return;
        }

        List<string> segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToList();

        if (request.HttpMethod == "PUT")
        {
            HandlePublish(context, string.Join("/", segments), body);
            return;
        }

        // Tarball: /{name}/-/{filename} (the '-' disambiguates scoped names with real slashes).
        for (int i = segments.Count - 2; i >= 1; i--)
        {
            if (segments[i] == "-")
            {
                string name = string.Join("/", segments.Take(i));
                string fileName = segments[i + 1];
                HandleTarball(context, name, fileName);
                return;
            }
        }

        string package = string.Join("/", segments);
        // /{name}/{version} when the last segment looks like a version; else packument.
        if (segments.Count >= 2 && IsVersionLike(segments[^1]))
        {
            HandleVersionMetadata(context, package[..package.LastIndexOf('/')], segments[^1]);
            return;
        }
        HandlePackument(context, package);
    }

    private void HandlePackument(HttpListenerContext context, string name)
    {
        if (!_packages.TryGetValue(name, out SortedDictionary<string, FakeNpmVersion>? versions))
        {
            TryRespond(context, HttpStatusCode.NotFound, "{\"error\":\"Not found\"}");
            return;
        }
        FakeNpmVersion[] snapshot;
        lock (versions) snapshot = versions.Values.ToArray();
        var document = new JsonObject
        {
            ["_id"] = name,
            ["name"] = name,
            ["dist-tags"] = new JsonObject { ["latest"] = snapshot[^1].Metadata["version"]!.GetValue<string>() },
            ["versions"] = new JsonObject(),
        };
        foreach (FakeNpmVersion version in snapshot)
            document["versions"]![version.Metadata["version"]!.GetValue<string>()] = JsonNode.Parse(version.Metadata.ToJsonString());
        TryRespond(context, HttpStatusCode.OK, document.ToJsonString());
    }

    private void HandleVersionMetadata(HttpListenerContext context, string name, string version)
    {
        if (_packages.TryGetValue(name, out SortedDictionary<string, FakeNpmVersion>? versions))
        {
            lock (versions)
            {
                if (versions.TryGetValue(version, out FakeNpmVersion? stored) && stored is not null)
                {
                    TryRespond(context, HttpStatusCode.OK, stored.Metadata.ToJsonString());
                    return;
                }
            }
        }
        TryRespond(context, HttpStatusCode.NotFound, "{\"error\":\"Not found\"}");
    }

    private void HandleTarball(HttpListenerContext context, string name, string fileName)
    {
        if (_packages.TryGetValue(name, out SortedDictionary<string, FakeNpmVersion>? versions))
        {
            FakeNpmVersion match;
            lock (versions) match = versions.Values.FirstOrDefault(v => v.TarballFileName == fileName)!;
            if (match is not null)
            {
                byte[] bytes = TamperTarball ? Corrupt(match.Tarball) : match.Tarball;
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                // See TryRespond: the managed listener needs an explicit Close to flush.
                context.Response.Close();
                return;
            }
        }
        TryRespond(context, HttpStatusCode.NotFound, "{\"error\":\"Not found\"}");
    }

    private void HandleSearch(HttpListenerContext context, string text)
    {
        var objects = new JsonArray();
        foreach (string name in PackageNames)
        {
            FakeNpmVersion[] snapshot;
            lock (_packages[name]) snapshot = _packages[name].Values.ToArray();
            JsonObject latest = snapshot[^1].Metadata;
            if (!MatchesSearch(name, latest, text)) continue;
            var package = new JsonObject
            {
                ["name"] = name,
                ["version"] = latest["version"]!.GetValue<string>(),
                ["description"] = latest["description"]?.DeepClone(),
                ["keywords"] = latest["keywords"]?.DeepClone(),
            };
            objects.Add(new JsonObject { ["package"] = package });
        }
        var document = new JsonObject { ["objects"] = objects, ["total"] = objects.Count };
        TryRespond(context, HttpStatusCode.OK, document.ToJsonString());
    }

    private static bool MatchesSearch(string name, JsonObject metadata, string text)
    {
        string description = metadata["description"]?.GetValue<string>() ?? "";
        JsonArray? keywords = metadata["keywords"] as JsonArray;
        // Search text may use either name form: the npm scope ("com.xengine" engine prefix or
        // "@inxep" scope) — match against both the wire name and its engine mapping.
        string engineName = NpmPackageName.FromNpmName(name);
        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("scope:", StringComparison.Ordinal))
            {
                string scope = token["scope:".Length..];
                if (!name.StartsWith("@" + scope + "/", StringComparison.Ordinal)) return false;
            }
            else if (token.StartsWith("keywords:", StringComparison.Ordinal))
            {
                string keyword = token["keywords:".Length..];
                bool has = keywords?.Any(k => string.Equals(k?.GetValue<string>(), keyword, StringComparison.OrdinalIgnoreCase)) == true;
                if (!has) return false;
            }
            else if (!name.Contains(token, StringComparison.OrdinalIgnoreCase)
                     && !engineName.Contains(token, StringComparison.OrdinalIgnoreCase)
                     && !description.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private void HandlePublish(HttpListenerContext context, string name, byte[] body)
    {
        if (ExpectedToken is not null)
        {
            string? authorization = context.Request.Headers["Authorization"];
            if (authorization != "Bearer " + ExpectedToken)
            {
                TryRespond(context, HttpStatusCode.Unauthorized, "{\"error\":\"auth required\"}");
                return;
            }
        }
        if (ExpectedOtp is not null && context.Request.Headers["npm-otp"] != ExpectedOtp)
        {
            TryRespond(context, HttpStatusCode.Unauthorized,
                "{\"error\":\"Two-factor authentication or granular access token with bypass 2fa enabled is required to publish packages.\"}");
            return;
        }

        JsonObject? document = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(body)) as JsonObject;
        if (document is null || document["name"]?.GetValue<string>() != name)
        {
            TryRespond(context, HttpStatusCode.BadRequest, "{\"error\":\"document/name mismatch\"}");
            return;
        }

        JsonObject? attachments = document["_attachments"] as JsonObject;
        JsonObject? versionsObject = document["versions"] as JsonObject;
        if (attachments is null || attachments.Count == 0 || versionsObject is null || versionsObject.Count == 0)
        {
            TryRespond(context, HttpStatusCode.BadRequest, "{\"error\":\"missing attachments or versions\"}");
            return;
        }

        // npm append semantics: the publish target is dist-tags.latest. Versions already on the
        // registry that are NOT the target are replayed history (the npm CLI merges the packument
        // into its PUT) and must be skipped; only re-publishing the target itself conflicts.
        string publishTarget = document["dist-tags"]?["latest"]?.GetValue<string>() ?? "";
        var attachment = attachments.First();
        byte[] attachmentTarball = Convert.FromBase64String(attachment.Value!["data"]!.GetValue<string>());

        SortedDictionary<string, FakeNpmVersion> versions =
            _packages.GetOrAdd(name, static _ => new SortedDictionary<string, FakeNpmVersion>(VersionComparer.Instance));
        lock (versions)
        {
            if (publishTarget.Length > 0 && versions.ContainsKey(publishTarget))
            {
                TryRespond(context, HttpStatusCode.Conflict,
                    "{\"error\":\"cannot modify pre-existing version: " + publishTarget + "\"}");
                return;
            }
            foreach (KeyValuePair<string, JsonNode?> versionEntry in versionsObject)
            {
                if (versionEntry.Value is not JsonObject metadata) continue;
                if (versions.ContainsKey(versionEntry.Key)) continue; // replayed history
                versions[versionEntry.Key] = new FakeNpmVersion(metadata, attachmentTarball, attachment.Key);
            }
        }

        PublishedDocuments.Enqueue(document);
        TryRespond(context, HttpStatusCode.Created,
            "{\"ok\":true,\"id\":\"" + name + "@" + publishTarget + "\"}");
    }

    private static void TryRespond(HttpListenerContext context, HttpStatusCode status, string body)
    {
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            // The managed (non-Windows) HttpListener does NOT finalize the response implicitly —
            // without Close() the bytes never flush and the client stalls until its timeout
            // (Windows http.sys finalizes on context disposal, masking this).
            context.Response.Close();
        }
        catch (Exception)
        {
            // Client went away or the response was already completed; nothing to salvage.
        }
    }

    private static byte[] Corrupt(byte[] source)
    {
        byte[] corrupted = (byte[])source.Clone();
        if (corrupted.Length > 0) corrupted[corrupted.Length / 2] ^= 0xFF;
        return corrupted;
    }

    private static bool IsVersionLike(string segment)
        => segment.Length >= 5 && char.IsDigit(segment[0]) && segment.Count(c => c == '.') >= 2;

    private static int ProbeFreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Order versions semver-ishly (major.minor.patch, pre-release ordinal) for dist-tags.</summary>
    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            int[] ParseNumbers(string version)
            {
                string core = version.Split('-', '+')[0];
                return core.Split('.').Select(part => int.TryParse(part, out int n) ? n : 0).ToArray();
            }

            int[] left = ParseNumbers(x), right = ParseNumbers(y);
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                int l = i < left.Length ? left[i] : 0;
                int r = i < right.Length ? right[i] : 0;
                if (l != r) return l.CompareTo(r);
            }
            return string.CompareOrdinal(x, y);
        }
    }
}
