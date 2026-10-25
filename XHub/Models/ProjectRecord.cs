using System;
using System.Collections.Generic;

namespace XHub.Models;

/// <summary>
/// One entry in <c>projects.json</c> (planning doc §4.1): normalized project path, display name,
/// favorite pin, last successful open time, and the preferred installation for future opens.
/// <see cref="MetadataIncomplete"/> marks legacy folders that only contain <c>Assets/</c> —
/// they open fine but carry no <c>.xengine</c> metadata, and XHub never writes missing files
/// into them on its own.
/// </summary>
public sealed class ProjectRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Normalized project root (never the .xengine file itself).</summary>
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Favorite { get; set; }

    /// <summary>UTC timestamp of the last open that reached "project ready". Null = never.</summary>
    public DateTime? LastOpenedUtc { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Installation the user pinned for this project (node N3 resolves it).</summary>
    public string? PreferredInstallationId { get; set; }

    /// <summary>True when the folder has Assets/ but no .xengine marker file.</summary>
    public bool MetadataIncomplete { get; set; }
}

/// <summary>Envelope payload of <c>projects.json</c>.</summary>
public sealed class ProjectLibraryData
{
    public List<ProjectRecord> Projects { get; set; } = [];

    /// <summary>Set after the one-time import of the editor's RecentProjects.json.</summary>
    public bool LegacyMigrated { get; set; }
}
