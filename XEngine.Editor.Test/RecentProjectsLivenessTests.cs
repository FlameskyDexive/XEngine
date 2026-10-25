using System;
using System.IO;
using XEngine.Editor.Projects;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Contract tests for the launcher recents liveness cache: draw code must only ever read cached
/// values (never stat), background refreshes flip missing/existing correctly, re-entrancy is
/// guarded, and Remove clears the cached entry. Uses real temp directories (local SSD — fast).
/// </summary>
public class RecentProjectsLivenessTests : IDisposable
{
    private readonly string _dir;

    public RecentProjectsLivenessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xengine-recents-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        RecentProjects.SetStoragePathForTests(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void UnknownPath_ReadsOptimisticExisting_BeforeAnyProbe()
    {
        Assert.True(RecentProjects.GetCachedExists(@"X:\nope\never-probed"));
    }

    [Fact]
    public void SyncRefresh_FlipsMissingAndExisting_AndRemoveClearsCache()
    {
        string existing = Path.Combine(_dir, "live-project");
        Directory.CreateDirectory(existing);
        string missing = Path.Combine(_dir, "dead-project"); // intentionally not created

        // Seed entries through the real persistence API.
        RecentProjects.AddRecent(existing, "live");
        RecentProjects.AddRecent(missing, "dead");

        RecentProjects.SetProbeForTests(p => p == existing);
        RecentProjects.RefreshLivenessForTests();

        Assert.True(RecentProjects.GetCachedExists(existing));
        Assert.False(RecentProjects.GetCachedExists(missing));

        // Removing the dead entry must clear its cache slot so a later AddRecent of the same
        // path starts from the optimistic/existing state again rather than a stale miss.
        RecentProjects.Remove(missing);
        Assert.True(RecentProjects.GetCachedExists(missing));
    }

    [Fact]
    public void AddRecent_OfOpenProject_MarksExistImmediately()
    {
        string path = Path.Combine(_dir, "just-opened");
        Directory.CreateDirectory(path);

        RecentProjects.AddRecent(path, "fresh");
        Assert.True(RecentProjects.GetCachedExists(path));

        // Even if a later background probe could not see it anymore (deleted while open),
        // the last known state comes from real probes, not guesses:
        Directory.Delete(path, true);
        RecentProjects.SetProbeForTests(_ => false);
        RecentProjects.RefreshLivenessForTests();
        Assert.False(RecentProjects.GetCachedExists(path));
    }

    [Fact]
    public void BeginRefresh_IsReentrancyGuarded()
    {
        string path = Path.Combine(_dir, "guard");
        Directory.CreateDirectory(path);
        RecentProjects.AddRecent(path, "g");

        int probeCalls = 0;
        RecentProjects.SetProbeForTests(_ => { Interlocked.Increment(ref probeCalls); return true; });

        RecentProjects.BeginRefreshLiveness();
        // TTL gate + running flag reject immediate re-entry and repeat calls in quick succession.
        RecentProjects.BeginRefreshLiveness();
        RecentProjects.BeginRefreshLiveness();

        SpinWaitPending(probeCalls, 1);
        Assert.Equal(1, Volatile.Read(ref probeCalls));
    }

    private static void SpinWaitPending(int sentinel, int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref sentinel) < expected && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
    }
}
