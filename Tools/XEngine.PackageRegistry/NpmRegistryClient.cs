// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.PackageRegistry;

/// <summary>Structured failure for a registry operation; network noise is folded into the message.</summary>
public sealed class NpmRegistryException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public NpmRegistryException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// npm-protocol registry client: packument fetch, search, verified tarball download, and publish.
/// Speaks the same HTTP API as the npm CLI (registry.npmjs.org or any compatible registry such as
/// Verdaccio): <c>GET /{name}</c>, <c>GET /-/v1/search</c>, tarball URLs from <c>dist</c>, and
/// <c>PUT /{name}</c> with base64 <c>_attachments</c> for publish. Engine names are translated to
/// wire names at this boundary (<see cref="NpmPackageName"/>).
///
/// <para>Test seam: pass a fake <see cref="HttpMessageHandler"/> (same pattern as the AI-gen
/// providers). All methods are async; editor-side synchronous resolution blocks on them at the
/// <c>RegistryPackageSource</c> boundary only.</para>
/// </summary>
public sealed class NpmRegistryClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly NpmRegistryOptions _options;
    private readonly bool _ownsHttpClient;

    public NpmRegistryOptions Options => _options;

    public NpmRegistryClient(NpmRegistryOptions? options = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? new NpmRegistryOptions();
        _ownsHttpClient = handler is null;
        if (handler is null && UsesLoopbackRegistry(_options.RegistryUrl))
        {
            // Loopback registries (local test servers, on-LAN Verdaccio via 127.0.0.1) must not
            // go through the system proxy: developer machines commonly run a local HTTP proxy,
            // and proxying "localhost" dead-ends or hangs. Same behavior as npm/curl.
            handler = new HttpClientHandler { UseProxy = false };
            _ownsHttpClient = true;
        }
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = _options.Timeout;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("xengine-package-client/1");
    }

    private static bool UsesLoopbackRegistry(string registryUrl)
    {
        try
        {
            return Uri.TryCreate(registryUrl, UriKind.Absolute, out Uri? uri)
                && uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // Read APIs
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Fetch the packument for an engine package name (translated to its npm wire name).
    /// Returns null when the registry answers 404 (not published here) — that is a normal
    /// resolution outcome, not an error.
    /// </summary>
    public async Task<NpmPackument?> GetPackumentAsync(string engineName, CancellationToken cancellationToken = default)
    {
        string? json = await GetPackumentRawAsync(engineName, cancellationToken).ConfigureAwait(false);
        if (json == null) return null;
        NpmPackument? packument = NpmPackument.Parse(json);
        if (packument is null)
            throw new NpmRegistryException($"Registry returned an unusable packument for '{NpmPackageName.ToNpmName(engineName)}'.");
        return packument;
    }

    /// <summary>The raw packument JSON (null on 404). Publish merges the existing versions and
    /// dist-tags into its PUT document — npmjs.com silently drops an append that omits them.</summary>
    public async Task<string?> GetPackumentRawAsync(string engineName, CancellationToken cancellationToken = default)
    {
        string wire = NpmPackageName.ToNpmName(engineName);
        string url = BaseUrl + NpmPackageName.ToUrlSegment(wire);
        using HttpResponseMessage response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadSuccessResponse(response, $"fetch packument for '{wire}'", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Search the registry. <paramref name="text"/> supports npm qualifiers
    /// (<c>scope:inxep</c>, <c>keywords:xengine</c>) verbatim.</summary>
    public async Task<IReadOnlyList<NpmSearchResult>> SearchAsync(
        string text, int size = 250, CancellationToken cancellationToken = default)
    {
        string url = BaseUrl + "-/v1/search?text=" + Uri.EscapeDataString(text) + "&size=" + size;
        using HttpResponseMessage response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        string json = await ReadSuccessResponse(response, $"search '{text}'", cancellationToken).ConfigureAwait(false);
        return NpmSearchResult.Parse(json);
    }

    /// <summary>
    /// Download the tarball for a version into <paramref name="destinationFile"/> (a temp path),
    /// verifying its SRI integrity first when the metadata carries one. Returns the tarball bytes'
    /// SRI string (<c>sha512-…</c>) so callers can store it as the lock content hash.
    /// </summary>
    public async Task<string> DownloadTarballAsync(
        NpmVersionInfo version, string destinationFile, string engineName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(version.TarballUrl))
            throw new NpmRegistryException($"Version '{version.Version}' has no tarball URL on the registry.");

        // dist.tarball may be absolute (npmjs.com) or relative to the registry (proxies); anchor
        // relative URLs on the registry base.
        string url = version.TarballUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || version.TarballUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? version.TarballUrl
            : BaseUrl.TrimEnd('/') + "/" + version.TarballUrl.TrimStart('/');

        using HttpResponseMessage response = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await ReadSuccessBytes(response, $"download tarball for '{engineName}@{version.Version}'", cancellationToken).ConfigureAwait(false);

        if (version.Integrity is { Length: > 0 })
        {
            if (!NpmIntegrity.TryParse(version.Integrity, out NpmIntegrity integrity))
                throw new NpmRegistryException($"Version '{engineName}@{version.Version}' carries a malformed integrity string.");
            if (!integrity.Verify(bytes))
                throw new NpmRegistryException(
                    $"Integrity mismatch for '{engineName}@{version.Version}': tarball does not match registry integrity '{version.Integrity}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFile))!);
        await File.WriteAllBytesAsync(destinationFile, bytes, cancellationToken).ConfigureAwait(false);
        return NpmIntegrity.Compute(bytes);
    }

    // ---------------------------------------------------------------------------------------------
    // Publish
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Pack <paramref name="packageDir"/> and publish it via the npm <c>PUT /{name}</c> protocol
    /// (base64 <c>_attachments</c>, bearer-token auth, <c>access:public</c> for scoped packages).
    /// Matches the npm CLI's append semantics: the existing packument's versions and dist-tags are
    /// fetched first and merged into the PUT document (npmjs.com silently drops an append that
    /// omits the package's history). Returns the published wire name + version. When the version
    /// already exists the registry answers a conflict status and this throws
    /// <see cref="NpmRegistryException"/> with that code.
    /// </summary>
    public async Task<(string WireName, string Version)> PublishAsync(
        string packageDir, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.AuthToken))
            throw new NpmRegistryException(
                $"No npm auth token configured — set {NpmRegistryOptions.TokenEnvVar} or .xengine/secrets.json 'npmToken' to publish.");

        string tempTarball = Path.Combine(Path.GetTempPath(), "xengine-npm-publish-" + Guid.NewGuid().ToString("N") + ".tgz");
        try
        {
            (string fileName, string wireName, string version) = NpmTarball.Pack(packageDir, tempTarball);
            byte[] tarball = await File.ReadAllBytesAsync(tempTarball, cancellationToken).ConfigureAwait(false);
            string integrity = NpmIntegrity.Compute(tarball);
            string shasum = NpmIntegrity.ComputeShasum(tarball);

            // Append semantics: carry the package's existing versions + dist-tags into the PUT.
            // A missing packument (first publish) or a transient read failure both degrade to a
            // fresh-package document — the registry rejects a conflicting re-send anyway.
            JsonObject? existing = null;
            try
            {
                string? existingJson = await GetPackumentRawAsync(
                    NpmPackageName.FromNpmName(wireName), cancellationToken).ConfigureAwait(false);
                if (existingJson != null) existing = JsonNode.Parse(existingJson) as JsonObject;
            }
            catch (NpmRegistryException)
            {
            }

            JsonObject versionMetadata = BuildVersionMetadata(packageDir, wireName, version, fileName, integrity, shasum, BaseUrl);
            string document = BuildPublishDocument(wireName, version, tarball, fileName, versionMetadata, existing);

            string url = BaseUrl + NpmPackageName.ToUrlSegment(wireName);
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(document, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AuthToken);
            request.Headers.Add("Npm-Command", "publish");
            // Accounts requiring 2FA to publish accept an authenticator OTP here; granular tokens
            // with publish rights skip this entirely.
            if (!string.IsNullOrWhiteSpace(_options.OneTimePassword))
                request.Headers.Add("npm-otp", _options.OneTimePassword);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                bool otpRequired = (int)response.StatusCode is 401 or 403
                    && (body.Contains("one-time", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("otp", StringComparison.OrdinalIgnoreCase)
                        || body.Contains("two-factor", StringComparison.OrdinalIgnoreCase));
                if (otpRequired)
                    throw new NpmRegistryException(
                        $"Publish of '{wireName}@{version}' needs 2FA: set {NpmRegistryOptions.OtpEnvVar} to a fresh authenticator code "
                        + $"(valid ~30s) or use a granular access token with publish permission. Registry said: {Truncate(body, 256)}",
                        response.StatusCode);
                // npmjs.com answers 403 (not 409) when re-publishing an existing immutable version.
                bool versionExists = (int)response.StatusCode == 403
                    && body.Contains("previously published", StringComparison.OrdinalIgnoreCase);
                if (versionExists)
                    throw new NpmRegistryException(
                        $"'{wireName}@{version}' is already published — npm versions are immutable; bump the package version to publish again.",
                        response.StatusCode);
                throw new NpmRegistryException(
                    $"Publish of '{wireName}@{version}' failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 512)}",
                    response.StatusCode);
            }
            return (wireName, version);
        }
        finally
        {
            try { if (File.Exists(tempTarball)) File.Delete(tempTarball); } catch (IOException) { }
        }
    }

    internal static JsonObject BuildVersionMetadata(
        string packageDir, string wireName, string version, string tarballFileName, string integrity, string shasum,
        string registryBaseUrl)
    {
        // The version entry mirrors the (already rewritten) package.json the tarball carries, plus
        // the dist block the registry requires. Parsing the on-disk manifest keeps repo sources
        // untouched; the name/dependency rewrite must match NpmTarball.Pack.
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(packageDir, "package.json"))) as JsonObject
                              ?? throw new NpmRegistryException($"package.json under '{packageDir}' is not a JSON object.");
        manifest["name"] = wireName;
        if (manifest["dependencies"] is JsonObject dependencies)
        {
            var rewritten = new JsonObject();
            foreach (var dependency in dependencies)
                rewritten[NpmPackageName.ToNpmName(dependency.Key)] = dependency.Value?.DeepClone();
            manifest["dependencies"] = rewritten;
        }

        manifest["dist"] = new JsonObject
        {
            ["tarball"] = registryBaseUrl.TrimEnd('/') + "/" + NpmPackageName.ToUrlSegment(wireName) + "/-/" + tarballFileName,
            ["integrity"] = integrity,
            ["shasum"] = shasum,
        };
        return manifest;
    }

    internal static string BuildPublishDocument(
        string wireName, string version, byte[] tarball, string tarballFileName, JsonObject versionMetadata,
        JsonObject? existingPackument = null)
    {
        // Merge the existing history exactly like the npm CLI: prior versions ride along (minus
        // their attachments, which the registry already holds), prior dist-tags survive, latest
        // moves to the version being published.
        var versions = new JsonObject();
        if (existingPackument?["versions"] is JsonObject priorVersions)
        {
            foreach (KeyValuePair<string, JsonNode?> prior in priorVersions)
            {
                if (string.Equals(prior.Key, version, StringComparison.Ordinal)) continue;
                var clone = prior.Value?.DeepClone() as JsonObject;
                if (clone == null) continue;
                clone.Remove("_attachments");
                versions[prior.Key] = clone;
            }
        }
        versions[version] = versionMetadata;

        var distTags = new JsonObject();
        if (existingPackument?["dist-tags"] is JsonObject priorTags)
            foreach (KeyValuePair<string, JsonNode?> prior in priorTags)
                distTags[prior.Key] = prior.Value?.DeepClone();
        distTags["latest"] = version;

        var document = new JsonObject
        {
            ["_id"] = wireName,
            ["name"] = wireName,
            ["dist-tags"] = distTags,
            ["versions"] = versions,
            ["access"] = "public",
            ["_attachments"] = new JsonObject
            {
                [tarballFileName] = new JsonObject
                {
                    ["content_type"] = "application/octet-stream",
                    ["data"] = Convert.ToBase64String(tarball),
                    // npm sends the base64 length here; harmless either way for compliant registries.
                    ["length"] = tarball.Length,
                },
            },
        };
        return document.ToJsonString(NpmJson.Options);
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------

    private string BaseUrl
    {
        get
        {
            string url = string.IsNullOrWhiteSpace(_options.RegistryUrl)
                ? NpmRegistryOptions.DefaultRegistryUrl
                : _options.RegistryUrl;
            return url.TrimEnd('/') + "/";
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        if (_options.Disabled)
            throw new NpmRegistryException(
                $"npm registry access is disabled ({NpmRegistryOptions.DisableEnvVar}); attempted '{url}'.");

        try
        {
            return await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new NpmRegistryException($"Registry request failed for '{url}': {ex.Message}", inner: ex);
        }
    }

    private static async Task<string> ReadSuccessResponse(
        HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new NpmRegistryException(
                $"Registry request failed while trying to {what}: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 256)}",
                response.StatusCode);
        }
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadSuccessBytes(
        HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new NpmRegistryException(
                $"Registry request failed while trying to {what}: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body, 256)}",
                response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int maxLength)
        => value is null ? "" : (value.Length <= maxLength ? value : value[..maxLength] + "…");
}

internal static class NpmJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
