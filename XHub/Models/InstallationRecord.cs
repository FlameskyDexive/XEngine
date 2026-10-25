using System;
using System.Collections.Generic;

namespace XHub.Models;

/// <summary>
/// One registered editor installation in <c>installations.json</c> (planning doc §4.1): install
/// id, root path, resolved version, architecture, source and management ownership. The same
/// version may exist at several paths — each registration has its own id (§3.3).
/// </summary>
public sealed class InstallationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Installation root (the directory that owns the editor payload).</summary>
    public string Path { get; set; } = "";

    /// <summary>Located editor executable, or "" while unknown.</summary>
    public string EditorExePath { get; set; } = "";

    /// <summary>Version exactly as displayed by the payload (e.g. "V1.0.0a1"), or "" when unknown.</summary>
    public string VersionDisplay { get; set; } = "";

    /// <summary>Canonical sortable form (e.g. "1.0.0-a1"), or "" when unknown.</summary>
    public string CanonicalVersion { get; set; } = "";

    /// <summary>"preview" / "stable" / "unknown" (§3.3 channel column).</summary>
    public string Channel { get; set; } = "unknown";

    /// <summary>"x64" / "x86" / "arm64" / "" (from the PE header).</summary>
    public string Architecture { get; set; } = "";

    /// <summary>"hub" / "installer" / "manual" / "source" (§3.3 source table).</summary>
    public string Source { get; set; } = "manual";

    /// <summary>True only for XHub-downloaded installs (full lifecycle: verify + uninstall, N6).</summary>
    public bool ManagedByHub { get; set; }

    /// <summary>True when the payload no longer contains a runnable editor (§3.3 可用性).</summary>
    public bool IsBroken { get; set; }

    /// <summary>
    /// Hub protocol version the payload reported via <c>--hub-info</c> (0 = legacy editor
    /// without the protocol — launched in compat mode, open success can never be confirmed).
    /// </summary>
    public int HubProtocolVersion { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastProbedUtc { get; set; }
}

/// <summary>Envelope payload of <c>installations.json</c>.</summary>
public sealed class InstallationLibraryData
{
    public List<InstallationRecord> Installations { get; set; } = [];

    /// <summary>Installation the user pinned as default (§3.3 设为默认).</summary>
    public string? DefaultInstallationId { get; set; }
}
