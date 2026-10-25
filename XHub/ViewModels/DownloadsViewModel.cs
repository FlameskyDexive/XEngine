using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XHub.Infrastructure;
using XHub.Models;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>Row state for one download/install operation (§3.5).</summary>
public partial class DownloadItemViewModel : ViewModelBase
{
    public string Id { get; }
    public string Title { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public string ProgressText { get; private set; } = "";
    public string RateText { get; private set; } = "";
    public string DetailText { get; private set; } = "";

    public double ProgressRatio { get; private set; }
    public bool IsIndeterminate { get; private set; }
    public bool CanPause { get; private set; }
    public bool CanResume { get; private set; }
    public bool CanCancel { get; private set; }
    public bool CanRetry { get; private set; }
    public bool CanRemove { get; private set; }
    public bool IsFailed { get; private set; }

    /// <summary>Recent log lines surfaced by the 日志详情 flyout (§3.5).</summary>
    public Queue<string> LogLines { get; } = new();

    public DownloadItemViewModel(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public void Apply(OperationProgress progress)
    {
        StateText = Loc.Instance[progress.State switch
        {
            OperationState.Downloading => "downloads.state.downloading",
            OperationState.Paused => "downloads.state.paused",
            OperationState.Completed => "downloads.state.completed",
            OperationState.Failed => "downloads.state.failed",
            OperationState.Canceled => "downloads.state.canceled",
            OperationState.Installing => "downloads.state.installing",
            OperationState.Done => "downloads.state.done",
            _ => "downloads.state.queued",
        }];
        IsIndeterminate = progress.State == OperationState.Installing;
        ProgressRatio = progress.BytesTotal > 0 && !IsIndeterminate
            ? Math.Clamp((double)progress.BytesDone / progress.BytesTotal, 0, 1)
            : 0;
        ProgressText = progress.BytesTotal > 0
            ? $"{FormatBytes(progress.BytesDone)} / {FormatBytes(progress.BytesTotal)}"
            : FormatBytes(progress.BytesDone);
        RateText = progress.BytesPerSecond > 0.5 ? $"{FormatBytes((long)progress.BytesPerSecond)}/s" : "";
        DetailText = progress.Error.Length > 0 ? Loc.Instance[progress.Error] : "";
        IsFailed = progress.State == OperationState.Failed;

        CanPause = progress.State == OperationState.Downloading;
        CanResume = progress.State == OperationState.Paused;
        CanCancel = progress.State is OperationState.Queued or OperationState.Downloading or OperationState.Installing;
        CanRetry = progress.State == OperationState.Failed;
        CanRemove = progress.State is OperationState.Completed or OperationState.Failed or OperationState.Canceled or OperationState.Done;

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(ProgressRatio));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(IsFailed));
    }

    public void AppendLog(string line)
    {
        LogLines.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
        while (LogLines.Count > 50)
            LogLines.Dequeue();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (double)(1024L * 1024 * 1024):F2} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (double)(1024 * 1024):F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// Download tasks page (§3.5): queue, progress, rate, pause/resume, cancel, retry, log detail.
/// Max two parallel package downloads and serial install commits are enforced by the services.
/// </summary>
public partial class DownloadsViewModel : ViewModelBase
{
    private readonly EditorInstaller _installer;
    private readonly Dictionary<string, DownloadItemViewModel> _rows = [];
    private readonly IDialogService _dialogs;

    public DownloadsViewModel(EditorInstaller installer, IDialogService dialogs)
    {
        _installer = installer;
        _dialogs = dialogs;
        installer.OperationProgress += OnProgress;

        // Existing operations (incl. recovered unfinished ones) surface on page build.
        foreach (DownloadOperation op in installer.Downloads.Operations)
        {
            DownloadItemViewModel row = GetOrCreate(op.Id, op.DisplayVersion);
            row.Apply(new OperationProgress
            {
                Id = op.Id,
                State = MapState(op.State),
                BytesDone = op.BytesDone,
                BytesTotal = op.BytesTotal,
                Error = op.Error,
            });
        }
        RebuildVisible();
    }

    public IReadOnlyList<DownloadItemViewModel> VisibleItems { get; private set; } = [];

    public bool HasNoTasks => VisibleItems.Count == 0;

    private static OperationState MapState(string state) => state switch
    {
        "downloading" => OperationState.Downloading,
        "paused" => OperationState.Paused,
        "completed" => OperationState.Completed,
        "failed" => OperationState.Failed,
        "canceled" => OperationState.Canceled,
        "installing" => OperationState.Installing,
        "done" => OperationState.Done,
        _ => OperationState.Queued,
    };

    private DownloadItemViewModel GetOrCreate(string id, string title)
    {
        if (_rows.TryGetValue(id, out DownloadItemViewModel? row)) return row;
        row = new DownloadItemViewModel(id, title);
        _rows[id] = row;
        return row;
    }

    private void OnProgress(OperationProgress progress)
    {
        void Run()
        {
            DownloadItemViewModel row = GetOrCreate(progress.Id, TitleFor(progress));
            row.AppendLog($"{row.StateText} {row.ProgressText}");
            row.Apply(progress);
            RebuildVisible();
        }
        if (Dispatcher.UIThread.CheckAccess()) Run();
        else Dispatcher.UIThread.Post(Run);
    }

    private string TitleFor(OperationProgress progress)
    {
        foreach (DownloadOperation op in _installer.Downloads.Operations)
            if (op.Id == progress.Id)
                return op.DisplayVersion.Length > 0 ? op.DisplayVersion : op.CanonicalVersion;
        return progress.Id.Length > 0 ? progress.Id[..Math.Min(8, progress.Id.Length)] : progress.Id;
    }

    private void RebuildVisible()
    {
        var visible = new List<DownloadItemViewModel>();
        foreach (DownloadItemViewModel row in _rows.Values)
            visible.Add(row);
        visible.Sort((a, b) => string.CompareOrdinal(a.Title, b.Title));
        VisibleItems = visible;
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(HasNoTasks));
    }

    [RelayCommand]
    public void Pause(DownloadItemViewModel? item) { if (item != null) _installer.Downloads.Pause(item.Id); }

    [RelayCommand]
    public void Resume(DownloadItemViewModel? item) { if (item != null) _installer.Downloads.Resume(item.Id); }

    [RelayCommand]
    public void Cancel(DownloadItemViewModel? item) { if (item != null) _installer.Downloads.Cancel(item.Id); }

    [RelayCommand]
    public void Retry(DownloadItemViewModel? item) { if (item != null) _installer.Downloads.Retry(item.Id); }

    [RelayCommand]
    public void Remove(DownloadItemViewModel? item) { if (item != null) _installer.Downloads.Remove(item.Id); }

    [RelayCommand]
    public async Task ShowLogAsync(DownloadItemViewModel? item)
    {
        if (item == null) return;
        string lines = string.Join("\n", item.LogLines);
        if (lines.Length == 0) lines = Loc.Instance["downloads.log.empty"];
        await _dialogs.ShowErrorAsync(item.Title, "downloads.log.title", lines);
    }
}
