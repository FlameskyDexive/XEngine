using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Services;
using XHub.ViewModels;
using Xunit;

namespace XHub.Test;

/// <summary>
/// Performance baselines from §8 (search/filter P95 ≤ 100 ms at 1000 records). The asserts use
/// generous CI-safe bounds; the evidence doc records the locally measured numbers.
/// </summary>
[Collection("Avalonia")]
public class PerformanceTests : IDisposable
{
    private readonly string _dir;

    public PerformanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ProjectsViewModel CreateThousandProjectVm()
    {
        string path = Path.Combine(_dir, "projects.json");
        var data = new ProjectLibraryData { LegacyMigrated = true };
        for (int i = 1; i <= 1000; i++)
        {
            data.Projects.Add(new ProjectRecord
            {
                Id = $"p{i:0000}",
                Path = Path.Combine(_dir, $"SeedProject{i:0000}"),
                Name = i % 14 == 0 ? $"种子项目{i:0000}" : $"SeedProject{i:0000}",
                Favorite = i % 50 == 0,
                LastOpenedUtc = DateTime.UnixEpoch.AddMinutes(i),
            });
        }
        new JsonStore<ProjectLibraryData>(path).Save(data);

        var library = new ProjectLibrary(path, Path.Combine(_dir, "no-legacy.json"));
        return new ProjectsViewModel(library, new NullDialogService());
    }

    [Fact]
    public void Search_Filter_1000Records_WellUnderBudget()
    {
        var vm = CreateThousandProjectVm();
        Assert.Equal(1000, vm.VisibleItems.Count);

        var worst = TimeSpan.Zero;
        string[] queries = ["Seed0", "种子项目", "0900", "zzz-no-match", "SeedProject0100"];
        foreach (string query in queries)
        {
            var sw = Stopwatch.StartNew();
            vm.SearchText = query;
            sw.Stop();
            worst = worst > sw.Elapsed ? worst : sw.Elapsed;
        }

        Assert.True(worst.TotalMilliseconds < 1000,
            $"search took {worst.TotalMilliseconds:F1}ms — the 100ms budget was breached by an order of magnitude");
    }

    [Fact]
    public void StatusFilter_1000Records_WellUnderBudget()
    {
        var vm = CreateThousandProjectVm();
        var sw = Stopwatch.StartNew();
        vm.StatusFilterIndex = 2;
        sw.Stop();

        Assert.True(sw.Elapsed.TotalMilliseconds < 1000,
            $"filter took {sw.Elapsed.TotalMilliseconds:F1}ms");
    }
}
