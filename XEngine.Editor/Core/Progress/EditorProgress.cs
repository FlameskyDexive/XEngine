// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

namespace XEngine.Editor.Core.Progress;

/// <summary>What a running task is about. Drives icons and default popup behavior in the UI layer.</summary>
public enum EditorProgressKind
{
    ProjectOpen,
    Import,
    Compile,
    Reload,
    Build,
    Custom,
}

/// <summary>
/// How a task's progress surfaces. <see cref="StatusBarOnly"/> never pops a card (short work the
/// status-bar spinner absorbs); <see cref="AutoPopup"/> pops the corner card once the task outlives
/// its popup delay (Unity shows the import window the same way: brief work stays on the spinner);
/// <see cref="ImmediatePopup"/> shows the card on the next frame (startup open, reload).
/// </summary>
public enum EditorProgressVisibility
{
    StatusBarOnly,
    AutoPopup,
    ImmediatePopup,
}

/// <summary>Opaque token for a task started via <see cref="EditorProgress.Start"/>.</summary>
public readonly struct ProgressHandle : IEquatable<ProgressHandle>
{
    public int Id { get; }
    internal ProgressHandle(int id) => Id = id;

    public bool IsValid => Id > 0;
    public static ProgressHandle None => default;

    public bool Equals(ProgressHandle other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is ProgressHandle other && Equals(other);
    public override int GetHashCode() => Id;
    public override string ToString() => $"progress:{Id}";
}

/// <summary>Immutable view of one running task. Rebuilt (never mutated) whenever progress changes.</summary>
public sealed class EditorProgressTaskView
{
    internal EditorProgressTaskView(
        int id, EditorProgressKind kind, string title, string detail,
        int current, int total, long startedTickMs,
        EditorProgressVisibility visibility, double popupDelayMs,
        bool canCancel, bool cancelRequested)
    {
        Id = id;
        Kind = kind;
        Title = title;
        Detail = detail;
        Current = current;
        Total = total;
        StartedTickMs = startedTickMs;
        Visibility = visibility;
        PopupDelayMs = popupDelayMs;
        CanCancel = canCancel;
        CancelRequested = cancelRequested;
    }

    public int Id { get; }
    public EditorProgressKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public int Current { get; }
    /// <summary>Target step count; 0 or less means the task is indeterminate.</summary>
    public int Total { get; }
    public long StartedTickMs { get; }
    public EditorProgressVisibility Visibility { get; }
    public double PopupDelayMs { get; }
    public bool CanCancel { get; }
    public bool CancelRequested { get; }

    public bool IsIndeterminate => Total <= 0;
    public float Fraction => Total <= 0 ? 0f : Math.Clamp(Current / (float)Total, 0f, 1f);
    public long ElapsedMs => Environment.TickCount64 - StartedTickMs;

    /// <summary>Whether the corner card should be showing for this task right now.</summary>
    public bool ShouldPopupNow => Visibility switch
    {
        EditorProgressVisibility.ImmediatePopup => true,
        EditorProgressVisibility.StatusBarOnly => false,
        _ => ElapsedMs >= PopupDelayMs,
    };
}

/// <summary>Immutable completion entry consumed by the UI (completion toasts) via <see cref="EditorProgress.ConsumeCompletions"/>.</summary>
public sealed class EditorProgressCompletion
{
    internal EditorProgressCompletion(EditorProgressKind kind, string title, bool success, string message, long finishedTickMs)
    {
        Kind = kind;
        Title = title;
        Success = success;
        Message = message;
        FinishedTickMs = finishedTickMs;
    }

    public EditorProgressKind Kind { get; }
    public string Title { get; }
    public bool Success { get; }
    public string Message { get; }
    public long FinishedTickMs { get; }
}

/// <summary>
/// Zero-allocation-read snapshot of all running progress tasks, ordered by start (oldest first, so
/// <see cref="Primary"/> is the longest-running task). Rebuilt only when something changes; the GUI
/// re-reads the same instance every frame while progress is idle.
/// </summary>
public sealed class EditorProgressSnapshot
{
    public static readonly EditorProgressSnapshot Empty = new([], 0);

    internal EditorProgressSnapshot(EditorProgressTaskView[] tasks, long version)
    {
        Tasks = tasks;
        Version = version;
    }

    public EditorProgressTaskView[] Tasks { get; }
    public long Version { get; }
    public bool AnyRunning => Tasks.Length > 0;

    /// <summary>Longest-running task — the one the status-bar cell summarizes when several run.</summary>
    public EditorProgressTaskView? Primary => Tasks.Length > 0 ? Tasks[0] : null;

    /// <summary>The task whose corner card is on top (first popup-worthy task by start order).</summary>
    public EditorProgressTaskView? TopPopup
    {
        get
        {
            for (int i = 0; i < Tasks.Length; i++)
                if (Tasks[i].ShouldPopupNow)
                    return Tasks[i];
            return null;
        }
    }
}

/// <summary>
/// Central editor progress registry (Unity's Progress API counterpart). Producers — the import loop
/// on the main thread, the compile worker thread, the project-open step machine — report here; every
/// consumer (status-bar cell, corner progress card, startup screen, MCP runtime_state) reads the same
/// <see cref="Snapshot"/>. All members are thread-safe; <see cref="Changed"/> may fire on any thread,
/// so thread-affine UI should poll <see cref="Snapshot"/> per frame instead of subscribing.
/// </summary>
public static class EditorProgress
{
    private sealed class TaskRecord
    {
        public int Id;
        public EditorProgressKind Kind;
        public string Title = "";
        public string Detail = "";
        public int Current;
        public int Total;
        public long StartedTickMs;
        public EditorProgressVisibility Visibility;
        public double PopupDelayMs;
        public Action? CancelCallback;
        public bool CancelRequested;
    }

    private const int CompletionHistoryLimit = 8;

    private static readonly object Gate = new();
    private static readonly List<TaskRecord> Tasks = [];
    private static readonly List<EditorProgressCompletion> Completions = [];
    private static int _nextId;
    private static long _version;
    private static EditorProgressSnapshot _snapshot = EditorProgressSnapshot.Empty;

    /// <summary>Raised after any change. Invoked off the lock and possibly from a worker thread.</summary>
    public static event Action? Changed;

    /// <summary>Latest immutable snapshot. Cheap: returns a cached reference until state changes.</summary>
    public static EditorProgressSnapshot Snapshot() => _snapshot;

    /// <summary>True while at least one task runs — forces animated frames even when the editor is idle.</summary>
    public static bool HasVisibleWork => _snapshot.AnyRunning;

    /// <summary>Monotonic change counter; consumers compare against the value they last drew.</summary>
    public static long Version => _snapshot.Version;

    public static ProgressHandle Start(
        EditorProgressKind kind,
        string title,
        int total = 0,
        EditorProgressVisibility visibility = EditorProgressVisibility.AutoPopup,
        double popupDelayMs = 350)
    {
        if (string.IsNullOrEmpty(title))
            throw new ArgumentNullException(nameof(title));

        TaskRecord record = new()
        {
            Id = Interlocked.Increment(ref _nextId),
            Kind = kind,
            Title = title,
            Total = Math.Max(total, 0),
            Visibility = visibility,
            PopupDelayMs = Math.Max(popupDelayMs, 0),
            StartedTickMs = Environment.TickCount64,
        };

        lock (Gate)
        {
            Tasks.Add(record);
            RebuildSnapshotLocked();
        }

        Changed?.Invoke();
        return new ProgressHandle(record.Id);
    }

    /// <summary>Updates step progress and (optionally) the detail line. No-op for unknown/finished handles.</summary>
    public static void Report(ProgressHandle handle, int current, string? detail = null)
    {
        lock (Gate)
        {
            if (FindLocked(handle) is not { } record)
                return;
            record.Current = current;
            if (detail != null)
                record.Detail = detail;
            RebuildSnapshotLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Updates only the detail line (e.g. "Compiling Zonezero.Game (3/18)" while indeterminate).</summary>
    public static void SetDetail(ProgressHandle handle, string detail)
    {
        lock (Gate)
        {
            if (FindLocked(handle) is not { } record)
                return;
            record.Detail = detail ?? string.Empty;
            RebuildSnapshotLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Re-targets the step count once an unknown-size phase discovers it (scan → import).</summary>
    public static void SetTotal(ProgressHandle handle, int total)
    {
        lock (Gate)
        {
            if (FindLocked(handle) is not { } record)
                return;
            record.Total = Math.Max(total, 0);
            RebuildSnapshotLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Completes a task. Double-finish and finish of an unknown handle are no-ops. On success the
    /// completion is queued for the UI toasts; supply <paramref name="message"/> for the toast body.
    /// </summary>
    public static void Finish(ProgressHandle handle, bool success = true, string? message = null)
    {
        lock (Gate)
        {
            TaskRecord? record = FindLocked(handle);
            if (record is null)
                return;

            Tasks.Remove(record);
            Completions.Add(new EditorProgressCompletion(record.Kind, record.Title, success, message ?? string.Empty, Environment.TickCount64));
            while (Completions.Count > CompletionHistoryLimit)
                Completions.RemoveAt(0);
            RebuildSnapshotLocked();
        }
        Changed?.Invoke();
    }

    public static bool IsRunning(ProgressHandle handle)
    {
        lock (Gate)
            return FindLocked(handle) != null;
    }

    /// <summary>Registers the callback invoked by <see cref="RequestCancel"/> (on the requesting thread).</summary>
    public static void RegisterCancel(ProgressHandle handle, Action cancel)
    {
        lock (Gate)
        {
            if (FindLocked(handle) is not { } record)
                return;
            record.CancelCallback = cancel;
            RebuildSnapshotLocked(); // CanCancel flips
        }
        Changed?.Invoke();
    }

    /// <summary>Marks the task cancel-requested and invokes its registered callback, if any.</summary>
    public static void RequestCancel(ProgressHandle handle)
    {
        Action? callback;
        lock (Gate)
        {
            if (FindLocked(handle) is not { } record)
                return;
            record.CancelRequested = true;
            callback = record.CancelCallback;
            RebuildSnapshotLocked();
        }
        Changed?.Invoke();
        callback?.Invoke();
    }

    /// <summary>Poll for cancellation from inside a sliced work loop on the producer thread.</summary>
    public static bool IsCancelRequested(ProgressHandle handle)
    {
        lock (Gate)
            return FindLocked(handle)?.CancelRequested ?? false;
    }

    /// <summary>Drains queued completions for the UI (toasts). Rebuilding clears them and bumps <see cref="Version"/>.</summary>
    public static EditorProgressCompletion[] ConsumeCompletions()
    {
        lock (Gate)
        {
            if (Completions.Count == 0)
                return [];
            EditorProgressCompletion[] drained = [.. Completions];
            Completions.Clear();
            RebuildSnapshotLocked();
            return drained;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Tasks.Clear();
            Completions.Clear();
            _version++;
            _snapshot = EditorProgressSnapshot.Empty;
        }
        Changed?.Invoke();
    }

    private static TaskRecord? FindLocked(ProgressHandle handle)
    {
        if (!handle.IsValid)
            return null;
        for (int i = 0; i < Tasks.Count; i++)
            if (Tasks[i].Id == handle.Id)
                return Tasks[i];
        return null;
    }

    private static void RebuildSnapshotLocked()
    {
        _version++;
        EditorProgressTaskView[] views = new EditorProgressTaskView[Tasks.Count];
        for (int i = 0; i < Tasks.Count; i++)
        {
            TaskRecord r = Tasks[i];
            views[i] = new EditorProgressTaskView(
                r.Id, r.Kind, r.Title, r.Detail, r.Current, r.Total, r.StartedTickMs,
                r.Visibility, r.PopupDelayMs, r.CancelCallback != null, r.CancelRequested);
        }
        _snapshot = new EditorProgressSnapshot(views, _version);
    }
}
