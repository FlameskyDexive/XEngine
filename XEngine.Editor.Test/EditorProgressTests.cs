// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Core.Progress;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorProgressTests : IDisposable
{
    public EditorProgressTests()
    {
        EditorProgress.ResetForTests();
    }

    public void Dispose()
    {
        EditorProgress.ResetForTests();
    }

    [Fact]
    public void Start_ShowsTaskInSnapshot_OrderedByStart()
    {
        ProgressHandle first = EditorProgress.Start(EditorProgressKind.Import, "Importing assets", 10);
        ProgressHandle second = EditorProgress.Start(EditorProgressKind.Compile, "Compiling scripts");

        EditorProgressSnapshot snapshot = EditorProgress.Snapshot();

        Assert.True(snapshot.AnyRunning);
        Assert.Equal(2, snapshot.Tasks.Length);
        Assert.Same(snapshot.Primary, snapshot.Tasks[0]);
        Assert.Equal(first.Id, snapshot.Tasks[0].Id);
        Assert.Equal(second.Id, snapshot.Tasks[1].Id);
    }

    [Fact]
    public void Report_UpdatesCurrentDetailAndFraction()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Import, "Importing", total: 4);

        EditorProgress.Report(handle, 3, "Textures/diffuse.png");

        EditorProgressTaskView view = EditorProgress.Snapshot().Primary!;
        Assert.Equal(3, view.Current);
        Assert.Equal("Textures/diffuse.png", view.Detail);
        Assert.Equal(0.75f, view.Fraction);
        Assert.False(view.IsIndeterminate);
    }

    [Fact]
    public void TotalZero_IsIndeterminate()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Compile, "Compiling");

        EditorProgressTaskView view = EditorProgress.Snapshot().Primary!;
        Assert.True(view.IsIndeterminate);
        Assert.Equal(0f, view.Fraction);

        EditorProgress.Report(handle, 5);
        Assert.True(EditorProgress.Snapshot().Primary!.IsIndeterminate);
    }

    [Fact]
    public void SetTotal_RetargetsBatchSize()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Import, "Scan", total: 0);

        EditorProgress.SetTotal(handle, 100);

        Assert.False(EditorProgress.Snapshot().Primary!.IsIndeterminate);
    }

    [Fact]
    public void Finish_RemovesTask_AndQueuesCompletion()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Import, "Importing", 2);
        EditorProgress.Report(handle, 2);

        EditorProgress.Finish(handle, success: true, message: "Imported 2 assets");

        Assert.False(EditorProgress.Snapshot().AnyRunning);
        EditorProgressCompletion[] completions = EditorProgress.ConsumeCompletions();
        Assert.Single(completions);
        Assert.True(completions[0].Success);
        Assert.Equal("Imported 2 assets", completions[0].Message);

        // Draining clears the queue.
        Assert.Empty(EditorProgress.ConsumeCompletions());
    }

    [Fact]
    public void DoubleFinish_AndReportAfterFinish_AreNoOps()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Custom, "task");
        long version = EditorProgress.Version;

        EditorProgress.Finish(handle);
        long versionAfterFinish = EditorProgress.Version;
        EditorProgress.Finish(handle);
        EditorProgress.Report(handle, 1, "stale");
        EditorProgress.SetDetail(handle, "stale");
        EditorProgress.SetTotal(handle, 5);

        Assert.False(EditorProgress.Snapshot().AnyRunning);
        // The second finish and the stale reports must not have bumped the version again.
        Assert.Equal(versionAfterFinish, EditorProgress.Version);
        Assert.True(versionAfterFinish > version);
        // Only the first finish queued a completion.
        Assert.Single(EditorProgress.ConsumeCompletions());
    }

    [Fact]
    public void Snapshot_IsCachedInstance_WhenStateUnchanged()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Custom, "task");

        EditorProgressSnapshot first = EditorProgress.Snapshot();
        EditorProgressSnapshot second = EditorProgress.Snapshot();

        Assert.Same(first, second);

        EditorProgress.Report(handle, 1);
        Assert.NotSame(first, EditorProgress.Snapshot());
    }

    [Fact]
    public void InvalidHandle_MembersAreNoOps()
    {
        ProgressHandle none = ProgressHandle.None;

        EditorProgress.Report(none, 1, "x");
        EditorProgress.SetDetail(none, "x");
        EditorProgress.SetTotal(none, 1);
        EditorProgress.Finish(none);
        EditorProgress.RegisterCancel(none, static () => { });
        EditorProgress.RequestCancel(none);
        Assert.False(EditorProgress.IsRunning(none));
        Assert.False(EditorProgress.IsCancelRequested(none));
        Assert.False(EditorProgress.Snapshot().AnyRunning);
    }

    [Fact]
    public void Cancel_InvokesCallback_AndSetsFlag()
    {
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Import, "Importing", 100);
        bool invoked = false;

        EditorProgress.RegisterCancel(handle, () => invoked = true);
        Assert.True(EditorProgress.Snapshot().Primary!.CanCancel);
        Assert.False(invoked);

        EditorProgress.RequestCancel(handle);

        Assert.True(invoked);
        Assert.True(EditorProgress.IsCancelRequested(handle));
        Assert.True(EditorProgress.Snapshot().Primary!.CancelRequested);
    }

    [Fact]
    public void Start_NullTitle_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EditorProgress.Start(EditorProgressKind.Custom, null!));
    }

    [Fact]
    public void AutoPopup_EscalatesOnlyAfterDelay()
    {
        // popupDelayMs clamps to >= 0; a 0ms delay means the card shows immediately on the next frame.
        ProgressHandle immediate = EditorProgress.Start(
            EditorProgressKind.Import, "Importing", 5, EditorProgressVisibility.ImmediatePopup);
        Assert.True(EditorProgress.Snapshot().Primary!.ShouldPopupNow);

        EditorProgress.Finish(immediate);

        ProgressHandle statusOnly = EditorProgress.Start(
            EditorProgressKind.Custom, "quick", 1, EditorProgressVisibility.StatusBarOnly);
        Assert.False(EditorProgress.Snapshot().Primary!.ShouldPopupNow);
        EditorProgress.Finish(statusOnly);

        ProgressHandle auto = EditorProgress.Start(
            EditorProgressKind.Import, "batch", 50, EditorProgressVisibility.AutoPopup, popupDelayMs: 60_000);
        Assert.False(EditorProgress.Snapshot().Primary!.ShouldPopupNow);
        EditorProgress.Finish(auto);

        ProgressHandle autoNow = EditorProgress.Start(
            EditorProgressKind.Import, "batch2", 50, EditorProgressVisibility.AutoPopup, popupDelayMs: 0);
        Assert.True(EditorProgress.Snapshot().Primary!.ShouldPopupNow);
    }

    [Fact]
    public async Task ConcurrentProducers_ProduceConsistentSnapshots()
    {
        const int Workers = 4;
        const int Reports = 200;
        ProgressHandle handle = EditorProgress.Start(EditorProgressKind.Import, "batch", Workers * Reports);
        long versionBefore = EditorProgress.Version;

        Task[] workers = new Task[Workers];
        for (int w = 0; w < Workers; w++)
        {
            workers[w] = Task.Run(() =>
            {
                for (int i = 0; i < Reports; i++)
                {
                    EditorProgress.Report(handle, i, $"file{i}.png");
                    _ = EditorProgress.Snapshot();
                }
            });
        }

        await Task.WhenAll(workers);

        EditorProgressTaskView view = EditorProgress.Snapshot().Primary!;
        Assert.Equal(Workers * Reports, view.Total);
        Assert.True(EditorProgress.Version > versionBefore);
        // The last snapshot must be internally consistent — never torn by a concurrent write.
        Assert.InRange(view.Current, 0, Workers * Reports);
    }
}
