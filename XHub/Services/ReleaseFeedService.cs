using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

/// <summary>One installable release row for the "可安装" section (§3.3).</summary>
public sealed record ReleaseEntry(
    string CanonicalVersion,
    string DisplayVersion,
    string Channel,
    DateTime PublishedAtUtc,
    string DownloadUrl,
    long Size,
    string Sha256,
    string EditorPath,
    int MinHubProtocol,
    string NotesUrl);

/// <summary>
/// Fetches and caches the release feed (§5). Production reads GitHub Releases for
/// <c>FlameskyDexive/XEngine</c> (the public releases-only mirror; source lives in the private
/// <c>FlameskyDexive/XE</c>) and parses the <c>xengine-release.json</c> asset of each release;
/// <c>--feed</c> (or the XHUB_FEED_URL env) points the service at a controlled test source
/// serving the manifest directly. Entries are version-sorted semantically; unparsable or
/// manifest-less releases are skipped.
/// </summary>
public sealed class ReleaseFeedService
{
    public const string GitHubReleasesUrl = "https://api.github.com/repos/FlameskyDexive/XEngine/releases";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly string? _feedOverride;
    private List<ReleaseEntry>? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReleaseFeedService(string? feedOverride = null, HttpClient? http = null)
    {
        _feedOverride = feedOverride ?? Environment.GetEnvironmentVariable("XHUB_FEED_URL");
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XEngine-XHub/0.1");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _cacheFile = Path.Combine(AppPaths.Root, "release-cache.json");
    }

    /// <summary>Entries sorted newest-first (semantic order); empty when offline and uncached.</summary>
    public IReadOnlyList<ReleaseEntry> CachedEntries => _cached ?? [];

    public DateTimeOffset? LastFetchUtc { get; private set; }

    public bool IsCacheFresh => LastFetchUtc != null && DateTimeOffset.UtcNow - LastFetchUtc < CacheTtl;

    /// <summary>Returns cached entries when fresh; otherwise refreshes (offline → cache → empty).</summary>
    public async Task<IReadOnlyList<ReleaseEntry>> GetEntriesAsync(bool forceRefresh, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cached != null && IsCacheFresh)
                return _cached;

            LoadCache();
            if (!forceRefresh && _cached != null && IsCacheFresh)
                return _cached;

            List<ReleaseEntry>? fresh = await FetchAsync(ct);
            if (fresh != null)
            {
                _cached = fresh;
                LastFetchUtc = DateTimeOffset.UtcNow;
                SaveCache();
            }
            return _cached ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public ReleaseEntry? Find(string canonicalVersion)
    {
        foreach (ReleaseEntry entry in CachedEntries)
            if (string.Equals(entry.CanonicalVersion, canonicalVersion, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    private async Task<List<ReleaseEntry>?> FetchAsync(CancellationToken ct)
    {
        try
        {
            if (_feedOverride != null)
            {
                ReleaseManifest? direct = await FetchManifestAsync(_feedOverride, ct);
                ReleaseEntry? single = ToEntry(direct);
                return single == null ? null : SortEntries([single]);
            }

            // GitHub Releases: list releases, then read each release's manifest asset.
            _http.DefaultRequestHeaders.Remove("Accept");
            _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            using HttpResponseMessage response = await _http.GetAsync(GitHubReleasesUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                HubLog.Warning($"Release feed listing failed: {(int)response.StatusCode}.");
                return null;
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var entries = new List<ReleaseEntry>();
            foreach (JsonElement release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()) continue;
                string? manifestUrl = FindManifestAssetUrl(release);
                if (manifestUrl == null) continue;
                ReleaseManifest? manifest = await FetchManifestAsync(manifestUrl, ct);
                ReleaseEntry? entry = ToEntry(manifest);
                if (entry != null) entries.Add(entry);
            }
            return SortEntries(entries);
        }
        catch (Exception ex)
        {
            HubLog.Warning($"Release feed fetch failed: {ex.Message}");
            return null;
        }
    }

    private async Task<ReleaseManifest?> FetchManifestAsync(string url, CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<ReleaseManifest>(json);
        }
        catch (Exception ex)
        {
            HubLog.Warning($"Manifest fetch failed for {url}: {ex.Message}");
            return null;
        }
    }

    private static string? FindManifestAssetUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement name)) continue;
            if (string.Equals(name.GetString(), "xengine-release.json", StringComparison.OrdinalIgnoreCase))
            {
                if (asset.TryGetProperty("browser_download_url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
                    return url.GetString();
            }
        }
        return null;
    }

    internal static ReleaseEntry? ToEntry(ReleaseManifest? manifest)
    {
        if (manifest == null || manifest.CanonicalVersion.Length == 0) return null;
        if (!EngineVersion.TryParse(manifest.CanonicalVersion, out EngineVersion? version)) return null;
        if (!manifest.Rids.TryGetValue(CurrentRid, out ReleaseRid? rid) || rid.Url.Length == 0) return null;
        string channel = version.Channel; // §4.2: channel comes from the version itself
        return new ReleaseEntry(version.Canonical, manifest.DisplayVersion, channel,
            manifest.PublishedAtUtc, rid.Url, rid.Size, rid.Sha256, manifest.EditorPath,
            manifest.MinHubProtocol, manifest.NotesUrl);
    }

    public static string CurrentRid =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => OperatingSystem.IsWindows() ? "win-x64"
                : OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64",
            System.Runtime.InteropServices.Architecture.Arm64 => OperatingSystem.IsWindows() ? "win-arm64"
                : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-arm64",
            _ => "unknown",
        };

    private static List<ReleaseEntry> SortEntries(List<ReleaseEntry> entries)
    {
        // Semantic ordering (§4.2), newest first; unparsable versions were already filtered.
        entries.Sort((a, b) =>
        {
            bool aOk = EngineVersion.TryParse(a.CanonicalVersion, out EngineVersion? av);
            bool bOk = EngineVersion.TryParse(b.CanonicalVersion, out EngineVersion? bv);
            if (!aOk && !bOk) return 0;
            if (!aOk) return 1;
            if (!bOk) return -1;
            return bv!.CompareTo(av);
        });
        return entries;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_cacheFile));
            var entries = new List<ReleaseEntry>();
            foreach (JsonElement e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var entry = new ReleaseEntry(
                    e.GetProperty("canonicalVersion").GetString() ?? "",
                    e.GetProperty("displayVersion").GetString() ?? "",
                    e.GetProperty("channel").GetString() ?? "",
                    e.GetProperty("publishedAtUtc").GetDateTime(),
                    e.GetProperty("downloadUrl").GetString() ?? "",
                    e.GetProperty("size").GetInt64(),
                    e.GetProperty("sha256").GetString() ?? "",
                    e.GetProperty("editorPath").GetString() ?? "",
                    e.GetProperty("minHubProtocol").GetInt32(),
                    e.GetProperty("notesUrl").GetString() ?? "");
                entries.Add(entry);
            }
            _cached = entries;
            LastFetchUtc = doc.RootElement.GetProperty("fetchedUtc").GetDateTime();
        }
        catch (Exception ex)
        {
            HubLog.Warning($"Release cache unreadable: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                fetchedUtc = LastFetchUtc ?? DateTimeOffset.UtcNow,
                entries = CachedEntries,
            });
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            File.WriteAllText(_cacheFile, json);
        }
        catch (Exception ex)
        {
            HubLog.Warning($"Release cache write failed: {ex.Message}");
        }
    }
}
