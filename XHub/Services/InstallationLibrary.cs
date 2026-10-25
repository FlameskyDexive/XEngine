using System;
using System.Collections.Generic;
using System.IO;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

/// <summary>Why an installation was selected for a project open (§3.3 priority chain).</summary>
public enum InstallationMatchKind
{
    /// <summary>Project pinned this installation (PreferredInstallationId).</summary>
    Preferred,
    /// <summary>Installation canonical version equals the project stamp.</summary>
    VersionExact,
    /// <summary>No matching installation — the UI offers install or pick-another.</summary>
    None,
}

public sealed record InstallationMatch(InstallationMatchKind Kind, InstallationRecord? Installation, string? HintKey)
{
    public static InstallationMatch None(string hintKey) => new(InstallationMatchKind.None, null, hintKey);
}

/// <summary>
/// Persistent editor-installation registry backing <c>installations.json</c> (§4.1): discovery,
/// manual locate, probing (broken installs), default selection, and the project→installation
/// match chain (§3.3: preferred pin → exact version → none). Same-version multi-install is
/// first-class — records are deduplicated by path, never by version.
/// </summary>
public sealed class InstallationLibrary
{
    private readonly JsonStore<InstallationLibraryData> _store;
    private InstallationLibraryData _data;

    public InstallationLibrary() : this(AppPaths.InstallationsFile) { }

    public InstallationLibrary(string filePath)
    {
        _store = new JsonStore<InstallationLibraryData>(filePath);
        _data = _store.Load();
    }

    public IReadOnlyList<InstallationRecord> Installations => _data.Installations;

    public string? DefaultInstallationId => _data.DefaultInstallationId;

    public event Action? Changed;

    public InstallationRecord? FindById(string? id)
    {
        if (id == null) return null;
        foreach (InstallationRecord record in _data.Installations)
            if (record.Id == id) return record;
        return null;
    }

    public InstallationRecord? FindByPath(string path)
    {
        foreach (InstallationRecord record in _data.Installations)
            if (ProjectPath.SamePath(record.Path, path)) return record;
        return null;
    }

    public InstallationRecord? Default
    {
        get
        {
            InstallationRecord? pinned = FindById(_data.DefaultInstallationId);
            if (pinned is { IsBroken: false }) return pinned;
            // Fall through to the best unbroken installation; broken default never blocks.
            foreach (InstallationRecord record in SortedUnbroken())
                return record;
            return pinned;
        }
    }

    /// <summary>Registers a root (or exe path) — probes it, dedupes by path. Null when unusable.</summary>
    public InstallationRecord? Add(string root, string source = "manual", bool managedByHub = false)
    {
        InstallationInspector.ProbeResult probe = InstallationInspector.Probe(root);
        if (!probe.Found) return null;

        string normalized = ProjectPath.Normalize(root).RootPath;
        InstallationRecord? existing = FindByPath(normalized);
        if (existing != null)
        {
            ApplyProbe(existing, probe);
            Save();
            Changed?.Invoke();
            return existing;
        }

        var record = new InstallationRecord
        {
            Path = normalized,
            EditorExePath = probe.EditorExePath,
            VersionDisplay = probe.VersionDisplay,
            CanonicalVersion = probe.CanonicalVersion,
            Channel = probe.Channel,
            Architecture = probe.Architecture,
            Source = source,
            ManagedByHub = managedByHub,
        };
        Register(record);
        return record;
    }

    /// <summary>
    /// Registers a fully-described record — the hub-installer flow (N6) and tests use this when
    /// the version is already known. Deduplicates by path; probing only fills the broken flag
    /// and exe location, and never overwrites a provided version.
    /// </summary>
    public InstallationRecord AddRecord(InstallationRecord record)
    {
        InstallationRecord? existing = FindByPath(record.Path);
        if (existing != null)
        {
            existing.VersionDisplay = record.VersionDisplay.Length > 0 ? record.VersionDisplay : existing.VersionDisplay;
            existing.CanonicalVersion = record.CanonicalVersion.Length > 0 ? record.CanonicalVersion : existing.CanonicalVersion;
            existing.Channel = record.Channel != "unknown" ? record.Channel : existing.Channel;
            existing.Source = record.Source;
            existing.ManagedByHub = record.ManagedByHub;
            Save();
            Changed?.Invoke();
            return existing;
        }

        InstallationInspector.ProbeResult probe = InstallationInspector.Probe(record.Path);
        record.EditorExePath = probe.EditorExePath;
        record.IsBroken = !probe.Found;
        if (record.VersionDisplay.Length == 0 && probe.VersionDisplay.Length > 0)
        {
            record.VersionDisplay = probe.VersionDisplay;
            record.CanonicalVersion = probe.CanonicalVersion;
            record.Channel = probe.Channel;
        }
        if (record.Architecture.Length == 0)
            record.Architecture = probe.Architecture;

        _data.Installations.Add(record);
        _data.DefaultInstallationId ??= record.Id;
        Save();
        Changed?.Invoke();
        return record;
    }

    private void Register(InstallationRecord record)
    {
        _data.Installations.Add(record);
        _data.DefaultInstallationId ??= record.Id; // first healthy install becomes the default
        Save();
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        int removed = _data.Installations.RemoveAll(r => r.Id == id);
        if (removed == 0) return false;
        if (_data.DefaultInstallationId == id)
            _data.DefaultInstallationId = _data.Installations.Count > 0 ? _data.Installations[^1].Id : null;
        Save();
        Changed?.Invoke();
        return true;
    }

    public void SetDefault(string id)
    {
        if (FindById(id) == null || _data.DefaultInstallationId == id) return;
        _data.DefaultInstallationId = id;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Re-probes every record (exe moved away → broken; came back → recovered).</summary>
    public void ProbeAll()
    {
        bool changed = false;
        foreach (InstallationRecord record in _data.Installations)
        {
            InstallationInspector.ProbeResult probe = InstallationInspector.Probe(record.Path);
            bool wasBroken = record.IsBroken;
            ApplyProbe(record, probe);
            changed |= wasBroken != record.IsBroken;
        }
        if (changed) Save();
        Changed?.Invoke();
    }

    private static void ApplyProbe(InstallationRecord record, InstallationInspector.ProbeResult probe)
    {
        record.IsBroken = !probe.Found;
        record.LastProbedUtc = DateTime.UtcNow;
        if (probe.Found)
        {
            record.EditorExePath = probe.EditorExePath;
            record.Architecture = probe.Architecture;
            // A payload without version resources keeps the registered version (§4.2: the record
            // is authoritative; the exe stamp is only a source when the record has none).
            if (probe.VersionDisplay.Length > 0)
            {
                record.VersionDisplay = probe.VersionDisplay;
                record.CanonicalVersion = probe.CanonicalVersion;
                record.Channel = probe.Channel;
            }
        }
        // Capability probe (§4.3): --hub-info decides hub sessions vs legacy compat mode.
        record.HubProtocolVersion = HubProtocol.ProbeEditor(record.EditorExePath)?.ProtocolVersion ?? 0;
    }

    /// <summary>Scans the well-known roots (§3.3 discovery) and registers anything new.</summary>
    public int Rescan()
    {
        int added = 0;
        foreach (string root in InstallationInspector.DiscoverCandidateRoots())
        {
            if (FindByPath(ProjectPath.Normalize(root).RootPath) != null) continue;
            if (Add(root, source: "installer") != null) added++;
        }
        if (added > 0)
            HubLog.Info($"Discovered {added} editor installation(s).");
        return added;
    }

    /// <summary>
    /// Picks the installation for opening a project (§3.3): the user's pinned installation
    /// first, then any unbroken install whose canonical version equals the project stamp.
    /// Unknown/missing stamps never silently fall back — the caller offers install or pick.
    /// </summary>
    public InstallationMatch MatchForProject(string? preferredInstallationId, string? projectVersionStamp)
    {
        InstallationRecord? preferred = FindById(preferredInstallationId);
        if (preferred is { IsBroken: false })
            return new InstallationMatch(InstallationMatchKind.Preferred, preferred, null);

        if (EngineVersion.TryParse(projectVersionStamp, out EngineVersion? projectVersion))
        {
            foreach (InstallationRecord record in SortedUnbroken())
            {
                if (record.CanonicalVersion.Length > 0 &&
                    EngineVersion.TryParse(record.CanonicalVersion, out EngineVersion? installed) &&
                    installed.Canonical == projectVersion.Canonical)
                {
                    return new InstallationMatch(InstallationMatchKind.VersionExact, record, null);
                }
            }
            return InstallationMatch.None("projects.match.missing");
        }

        return _data.Installations.Count > 0
            ? InstallationMatch.None("projects.match.unknownVersion")
            : InstallationMatch.None("projects.match.noInstallations");
    }

    private IEnumerable<InstallationRecord> SortedUnbroken()
    {
        // Semantic ordering (§4.2): never string-sort versions. Unknown-version records go last.
        var list = new List<InstallationRecord>(_data.Installations);
        list.RemoveAll(r => r.IsBroken);
        list.Sort((a, b) =>
        {
            bool aKnown = EngineVersion.TryParse(a.CanonicalVersion, out EngineVersion? av);
            bool bKnown = EngineVersion.TryParse(b.CanonicalVersion, out EngineVersion? bv);
            if (!aKnown && !bKnown) return 0;
            if (!aKnown) return 1;
            if (!bKnown) return -1;
            return av!.CompareTo(bv);
        });
        return list;
    }

    public void Save() => _store.Save(_data);
}
