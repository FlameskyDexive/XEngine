using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

/// <summary>
/// Persistent project registry backing <c>projects.json</c> (planning doc §4.1). Owns the
/// import/dedupe/favorite/remove/repair operations and the one-time migration from the
/// editor's <c>%APPDATA%\XEngine\RecentProjects.json</c>. UI-layer live state (availability,
/// version stamps) lives in the view models, not here.
/// </summary>
public sealed class ProjectLibrary
{
    private readonly JsonStore<ProjectLibraryData> _store;
    private readonly string _legacyRecentProjectsFile;
    private ProjectLibraryData _data;

    public ProjectLibrary() : this(AppPaths.ProjectsFile) { }

    public ProjectLibrary(string filePath, string? legacyRecentProjectsFile = null)
    {
        _store = new JsonStore<ProjectLibraryData>(filePath);
        _legacyRecentProjectsFile = legacyRecentProjectsFile ?? AppPaths.LegacyRecentFile;
        _data = _store.Load();
        CanonicalizeStoredPaths();

        // §4.1 first-startup import: only while the library has never been saved, so a user's
        // XHub-side removals and favorites can never be overridden by the stale editor list.
        if (!_data.LegacyMigrated)
        {
            _data.LegacyMigrated = true;
            ImportLegacy();
            Save();
        }
    }

    /// <summary>Hand-edited stores may use / separators; dedupe relies on one canonical form.</summary>
    private void CanonicalizeStoredPaths()
    {
        foreach (ProjectRecord record in _data.Projects)
        {
            if (string.IsNullOrEmpty(record.Path)) continue;
            try
            {
                string normalized = Path.GetFullPath(record.Path);
                if (!string.Equals(normalized, record.Path, StringComparison.Ordinal))
                    record.Path = normalized;
            }
            catch
            {
                // unfixable path stays as-is; the probe will flag it missing
            }
        }
    }

    public IReadOnlyList<ProjectRecord> Projects => _data.Projects;

    public event Action? Changed;

    public ProjectRecord? FindByPath(string normalizedPath)
    {
        foreach (ProjectRecord record in _data.Projects)
            if (ProjectPath.SamePath(record.Path, normalizedPath))
                return record;
        return null;
    }

    public ProjectRecord? FindById(string id)
    {
        foreach (ProjectRecord record in _data.Projects)
            if (record.Id == id)
                return record;
        return null;
    }

    /// <summary>
    /// Adds a project (path must already be normalized). An existing record for the same path is
    /// updated in place — favorites, ids and open history survive a re-import.
    /// </summary>
    public ProjectRecord Add(string normalizedPath, string name, bool metadataIncomplete)
    {
        ProjectRecord? existing = FindByPath(normalizedPath);
        if (existing != null)
        {
            existing.Name = string.IsNullOrWhiteSpace(name) ? existing.Name : name;
            existing.MetadataIncomplete = metadataIncomplete;
            Save();
            Changed?.Invoke();
            return existing;
        }

        var record = new ProjectRecord
        {
            Path = normalizedPath,
            Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(normalizedPath) : name,
            MetadataIncomplete = metadataIncomplete,
        };
        _data.Projects.Add(record);
        Save();
        Changed?.Invoke();
        return record;
    }

    /// <summary>Removes only the XHub record — the project directory is never touched.</summary>
    public bool Remove(string id)
    {
        int removed = _data.Projects.RemoveAll(r => r.Id == id);
        if (removed == 0) return false;
        Save();
        Changed?.Invoke();
        return true;
    }

    public void SetFavorite(string id, bool favorite)
    {
        ProjectRecord? record = FindById(id);
        if (record == null || record.Favorite == favorite) return;
        record.Favorite = favorite;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Repoints a missing project record at a new location. Refuses when the new path already
    /// belongs to another record (merge instead of silent duplicate).
    /// </summary>
    public ProjectRecord? RepairPath(string id, string newNormalizedPath)
    {
        ProjectRecord? record = FindById(id);
        if (record == null) return null;
        ProjectRecord? clash = FindByPath(newNormalizedPath);
        if (clash != null && clash.Id != id) return null;

        record.Path = newNormalizedPath;
        record.MetadataIncomplete = false;
        Save();
        Changed?.Invoke();
        return record;
    }

    /// <summary>Records a successful open (only the editor reports "project ready", §4.4).</summary>
    public void MarkOpened(string id, DateTime? utcTimestamp = null)
    {
        ProjectRecord? record = FindById(id);
        if (record == null) return;
        record.LastOpenedUtc = utcTimestamp ?? DateTime.UtcNow;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// One-time import of the editor's recent-projects list. Runs while the store is fresh
    /// (constructor); favorites and last-opened stamps carry over, duplicates collapse.
    /// </summary>
    public int ImportLegacy()
    {
        if (!File.Exists(_legacyRecentProjectsFile)) return 0;
        List<LegacyRecentEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<LegacyRecentEntry>>(File.ReadAllText(_legacyRecentProjectsFile));
        }
        catch (Exception ex)
        {
            HubLog.Warning($"Legacy RecentProjects.json unreadable: {ex.Message}");
            return 0;
        }
        if (entries == null || entries.Count == 0) return 0;

        int imported = 0;
        foreach (LegacyRecentEntry entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path)) continue;
            ProjectLocation location = ProjectPath.Normalize(entry.Path);
            if (!location.DirectoryExists || !location.HasAssets) continue;

            ProjectRecord? existing = FindByPath(location.RootPath);
            if (existing == null)
            {
                _data.Projects.Add(new ProjectRecord
                {
                    Path = location.RootPath,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? location.Name ?? System.IO.Path.GetFileName(location.RootPath) : entry.Name,
                    Favorite = entry.Favorite,
                    LastOpenedUtc = entry.LastOpened == default ? null : entry.LastOpened.ToUniversalTime(),
                    MetadataIncomplete = location.XEngineFiles.Count == 0,
                });
                imported++;
            }
            else
            {
                // Already known (e.g. by path with different casing): merge, never duplicate.
                existing.Favorite |= entry.Favorite;
                DateTime opened = entry.LastOpened.ToUniversalTime();
                if (existing.LastOpenedUtc == null || opened > existing.LastOpenedUtc)
                    existing.LastOpenedUtc = opened;
            }
        }
        if (imported > 0)
            HubLog.Info($"Imported {imported} project(s) from legacy RecentProjects.json.");
        return imported;
    }

    private sealed class LegacyRecentEntry
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime LastOpened { get; set; }
        public bool Favorite { get; set; }
    }

    public void Save() => _store.Save(_data);
}
