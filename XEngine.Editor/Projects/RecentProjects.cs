using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace XEngine.Editor.Projects;

public class RecentProjectEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastOpened { get; set; }
    public bool Favorite { get; set; }
}

/// <summary>
/// Persists the list of recently opened projects in the user's AppData.
/// </summary>
public static class RecentProjects
{
    private const int MaxRecent = 20;
    private static readonly TimeSpan LivenessTtl = TimeSpan.FromSeconds(30);

    private static string _filePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XEngine", "RecentProjects.json");

    private static List<RecentProjectEntry>? _entries;

    /// <summary>Test isolation seam: point persistence (and cache state) at a scratch directory.
    /// Never call from product code.</summary>
    internal static void SetStoragePathForTests(string directory)
    {
        _filePath = System.IO.Path.Combine(directory, "RecentProjects.json");
        ResetLivenessForTests();
    }

    public static IReadOnlyList<RecentProjectEntry> Entries
    {
        get
        {
            _entries ??= Load();
            return _entries;
        }
    }

    // ── Existence cache ───────────────────────────────────────────────
    // The launcher used to call Directory.Exists per card EVERY presented frame. While a context
    // menu is open the editor renders continuously, and the typical "remove" candidate is a
    // project whose folder is gone — if that path sits on an unreachable network share, one stat
    // blocks the UI thread for seconds, so right-click → Remove looked frozen. Draw code now only
    // reads this cache; probing happens on one background thread at a time.
    private sealed class Liveness
    {
        public bool Exists;
        public DateTime CheckedUtc;
    }

    private static readonly Dictionary<string, Liveness> _liveness =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _livenessLock = new();
    private static Func<string, bool> _probe = Directory.Exists;
    private static int _refreshRunning;
    private static DateTime _lastRefreshUtc;

    /// <summary>Cached existence of <paramref name="path"/>; unknown paths read as existing
    /// (optimistic — matches the pre-cache first-frame behaviour until a probe lands).</summary>
    public static bool GetCachedExists(string path)
    {
        lock (_livenessLock)
        {
            return !_liveness.TryGetValue(path, out var l) || l.Exists;
        }
    }

    /// <summary>
    /// Kicks a background re-probe of every entry when the cache is older than the TTL.
    /// Never touches the calling (UI) thread; <paramref name="onChanged"/> runs on the probe
    /// thread so callers typically forward it to their invalidation scheduler.
    /// </summary>
    public static void BeginRefreshLiveness(Action? onChanged = null)
    {
        var entries = Entries; // ensure loaded on caller thread
        if (_lastRefreshUtc + LivenessTtl > DateTime.UtcNow) return;
        if (Interlocked.Exchange(ref _refreshRunning, 1) == 1) return;

        var snapshot = entries.Select(e => e.Path).ToArray();
        _lastRefreshUtc = DateTime.UtcNow;
        new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var path in snapshot)
                {
                    bool exists;
                    try { exists = _probe(path); }
                    catch { exists = false; }
                    lock (_livenessLock)
                    {
                        if (!_liveness.TryGetValue(path, out var l))
                            _liveness[path] = l = new Liveness();
                        l.Exists = exists;
                        l.CheckedUtc = DateTime.UtcNow;
                    }
                }
                if (System.Diagnostics.Stopwatch.IsHighResolution && snapshot.Length > 0)
                    Runtime.Debug.Log(
                        $"[LauncherLiveness] {snapshot.Length} paths probed on background thread in {sw.ElapsedMilliseconds}ms");
                onChanged?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
        })
        { IsBackground = true, Name = "RecentsLivenessProbe" }.Start();
    }

    /// <summary>Test seam: override the probe used by background refreshes.</summary>
    internal static void SetProbeForTests(Func<string, bool>? probe) =>
        _probe = probe ?? Directory.Exists;

    /// <summary>Synchronous test seam: run the probe for all paths immediately on this thread.</summary>
    internal static void RefreshLivenessForTests()
    {
        foreach (var e in Entries)
        {
            bool exists;
            try { exists = _probe(e.Path); }
            catch { exists = false; }
            lock (_livenessLock)
            {
                if (!_liveness.TryGetValue(e.Path, out var l))
                    _liveness[e.Path] = l = new Liveness();
                l.Exists = exists;
                l.CheckedUtc = DateTime.UtcNow;
            }
        }
        _lastRefreshUtc = DateTime.UtcNow;
    }

    internal static void ResetLivenessForTests()
    {
        lock (_livenessLock) { _liveness.Clear(); }
        _lastRefreshUtc = DateTime.MinValue;
        _entries = null;
    }

    public static void AddRecent(string path, string name)
    {
        _entries ??= Load();
        MarkLiveness(path, exists: true);

        // Remove existing entry for this path
        _entries.RemoveAll(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

        // Add at front
        _entries.Insert(0, new RecentProjectEntry
        {
            Path = path,
            Name = name,
            LastOpened = DateTime.UtcNow
        });

        // Trim the non-favorite tail to MaxRecent entries. Favorites are never evicted regardless
        // of count or age - starring a project is meant to pin it in this list permanently.
        int nonFavoriteCount = _entries.Count(e => !e.Favorite);
        if (nonFavoriteCount > MaxRecent)
        {
            int toRemove = nonFavoriteCount - MaxRecent;
            for (int i = _entries.Count - 1; i >= 0 && toRemove > 0; i--)
            {
                if (!_entries[i].Favorite)
                {
                    _entries.RemoveAt(i);
                    toRemove--;
                }
            }
        }

        Save();
    }

    public static void Remove(string path)
    {
        _entries ??= Load();
        _entries.RemoveAll(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        lock (_livenessLock) { _liveness.Remove(path); }
        Save();
    }

    /// <summary>Mark a recent project as a favorite (or clear it) and persist.</summary>
    public static void SetFavorite(string path, bool favorite)
    {
        _entries ??= Load();
        var entry = _entries.Find(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null || entry.Favorite == favorite) return;
        entry.Favorite = favorite;
        Save();
    }

    private static void MarkLiveness(string path, bool exists)
    {
        lock (_livenessLock)
        {
            if (!_liveness.TryGetValue(path, out var l))
                _liveness[path] = l = new Liveness();
            l.Exists = exists;
            l.CheckedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Recent entries with favorites floated to the top; each group keeps recency order.</summary>
    public static List<RecentProjectEntry> FavoritesFirst()
    {
        _entries ??= Load();
        var ordered = new List<RecentProjectEntry>(_entries.Count);
        foreach (var e in _entries) if (e.Favorite) ordered.Add(e);
        foreach (var e in _entries) if (!e.Favorite) ordered.Add(e);
        return ordered;
    }

    private static List<RecentProjectEntry> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<RecentProjectEntry>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    private static void Save()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Failed to save recent projects: {ex.Message}");
        }
    }
}
