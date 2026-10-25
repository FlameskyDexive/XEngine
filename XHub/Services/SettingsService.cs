using System;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Platform;

namespace XHub.Services;

/// <summary>
/// Loads and persists <c>settings.json</c>. Call <see cref="Load"/> once at startup (before any
/// UI binds to language) and <see cref="Save"/> whenever the user changes a setting.
/// </summary>
public sealed class SettingsService
{
    private readonly JsonStore<HubSettings> _store;

    public SettingsService() : this(AppPaths.SettingsFile) { }

    public SettingsService(string filePath) => _store = new JsonStore<HubSettings>(filePath);

    public HubSettings Current { get; private set; } = new();

    public event Action? Saved;

    public void Load()
    {
        Current = _store.Load();
        HubLog.SetVerbose(Current.VerboseLog);
    }

    public void Save()
    {
        _store.Save(Current);
        Saved?.Invoke();
    }
}
