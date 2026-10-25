using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace XHub.Models;

/// <summary>
/// <c>xengine-release.json</c> attached to every editor release (§5): canonical + display
/// version, channel, per-RID download descriptors, expected payload layout, minimum hub
/// protocol version and release notes link.
/// </summary>
public sealed class ReleaseManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("canonicalVersion")]
    public string CanonicalVersion { get; set; } = "";

    [JsonPropertyName("displayVersion")]
    public string DisplayVersion { get; set; } = "";

    /// <summary>"preview" / "stable" — validated against the canonical version (§4.2).</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "stable";

    [JsonPropertyName("publishedAt")]
    public DateTime PublishedAtUtc { get; set; }

    /// <summary>RID → download descriptor, e.g. "win-x64" → {url,size,sha256}.</summary>
    [JsonPropertyName("rids")]
    public Dictionary<string, ReleaseRid> Rids { get; set; } = [];

    /// <summary>Editor executable relative path inside the extracted payload (layout check).</summary>
    [JsonPropertyName("editorPath")]
    public string EditorPath { get; set; } = "XEngine.Editor.exe";

    /// <summary>Releases whose payload requires a newer hub protocol are refused (§5).</summary>
    [JsonPropertyName("minHubProtocol")]
    public int MinHubProtocol { get; set; }

    [JsonPropertyName("notesUrl")]
    public string NotesUrl { get; set; } = "";
}

public sealed class ReleaseRid
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

/// <summary>Persistent state of one download/install operation (§4.1 operations.json).</summary>
public sealed class DownloadOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>"download" / "install" — an install op owns its download op by id.</summary>
    public string Kind { get; set; } = "download";

    /// <summary>"queued" / "downloading" / "paused" / "completed" / "failed" / "canceled" / "installing" / "done"</summary>
    public string State { get; set; } = "queued";

    public string CanonicalVersion { get; set; } = "";

    public string DisplayVersion { get; set; } = "";

    public string Rid { get; set; } = "";

    public string Url { get; set; } = "";

    public long BytesTotal { get; set; }

    public long BytesDone { get; set; }

    /// <summary>Server etag/last-modified captured for change detection + resume validation.</summary>
    public string ETag { get; set; } = "";

    public string LastModified { get; set; } = "";

    /// <summary>Partial file path for Range resume (under the download cache).</summary>
    public string TempPath { get; set; } = "";

    public string ExpectedSha256 { get; set; } = "";

    /// <summary>Install-only state: relative directory under the editors root.</summary>
    public string InstallDir { get; set; } = "";

    public string Error { get; set; } = "";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Envelope payload of <c>operations.json</c> — unfinished downloads and installs (§4.1).</summary>
public sealed class OperationsData
{
    public List<DownloadOperation> Operations { get; set; } = [];
}
