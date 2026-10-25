// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Async;
using XEngine.Editor.Core;
using XEngine.Editor.Core.Progress;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.Rosetta;
using XEngine.Runtime;

namespace XEngine.Editor.GUI;

/// <summary>
/// Tools/Progress demo + acceptance tasks. They exist so the status-bar cell (and later the corner
/// card / startup screen) can be exercised and screenshot-accepted without touching the real import
/// or compile pipelines. <see cref="Tick"/> runs once per editor frame and is free while idle.
/// </summary>
internal static class ProgressDemoMenu
{
    private sealed class Demo
    {
        public ProgressHandle Handle = ProgressHandle.None;
        public int Total;
        public float Position;
        public float StepsPerSecond;
        public float Elapsed;
        public float DurationSeconds = 1f;
    }

    private static readonly List<Demo> Active = [];

    public static bool AnyActive => Active.Count > 0;

    /// <summary>Advances every running demo task. Call once per frame; no-op while idle.</summary>
    public static void Tick(float dt)
    {
        if (Active.Count == 0)
            return;

        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Demo demo = Active[i];
            demo.Elapsed += dt;

            if (demo.Total > 0)
            {
                demo.Position += demo.StepsPerSecond * dt;
                int current = (int)demo.Position;
                bool last = current >= demo.Total || demo.Elapsed >= demo.DurationSeconds;
                if (last)
                    current = demo.Total;
                EditorProgress.Report(demo.Handle, current);
                if (last)
                {
                    EditorProgress.Finish(demo.Handle, success: true, message: Loc.Get("progress.demo.finished"));
                    Active.RemoveAt(i);
                }
            }
            else
            {
                // Indeterminate: report elapsed so Detail stays fresh; finish on duration.
                EditorProgress.Report(demo.Handle, (int)(demo.Elapsed * 1000f));
                if (demo.Elapsed >= demo.DurationSeconds)
                {
                    EditorProgress.Finish(demo.Handle, success: true, message: Loc.Get("progress.demo.finished"));
                    Active.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// Acceptance automation: <c>XENGINE_PROGRESS_DEMO=import</c> starts a long import demo once the
    /// project is ready, so the status cell is presented by the NORMAL main-loop frames (nested
    /// RenderOnce pumps never reach the screen under the dedicated render thread — verified while
    /// building this). Pair with <c>--panel-screenshot</c> (quit vehicle) plus
    /// <c>XENGINE_PROGRESS_SHOT_OUT</c> (window capture before the quit).
    /// </summary>
    public static void TryStartFromEnv()
    {
        if (_envDemoStarted)
            return;
        string? spec = Environment.GetEnvironmentVariable("XENGINE_PROGRESS_DEMO");
        if (string.IsNullOrEmpty(spec))
            return;
        // Wait for the editor to be fully up so the cell draws in steady-state frames.
        if (Projects.Project.Current == null || EditorAssetBackend.Instance == null
            || Runtime.Resources.Scene.Current == null)
            return;

        _envDemoStarted = true;
        if (spec.Equals("import", StringComparison.OrdinalIgnoreCase))
            Start(Loc.Get("import.progress.assets"), EditorProgressKind.Import,
                total: 50, durationSeconds: 20f, EditorProgressVisibility.StatusBarOnly);
        else if (spec.Equals("import-popup", StringComparison.OrdinalIgnoreCase))
        {
            // Corner-card import demo with cancel, Unity-style batch window.
            ProgressHandle handle = Start(Loc.Get("import.progress.assets"), EditorProgressKind.Import,
                total: 50, durationSeconds: 20f, EditorProgressVisibility.ImmediatePopup);
            EditorProgress.RegisterCancel(handle, StopAll);
        }
        else if (spec.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            ProgressHandle handle = Start(Loc.Get("progress.demo.cancellable"), EditorProgressKind.Custom,
                total: 200, durationSeconds: 20f, EditorProgressVisibility.StatusBarOnly);
            EditorProgress.RegisterCancel(handle, StopAll);
        }
        else if (spec.Equals("busy", StringComparison.OrdinalIgnoreCase))
        {
            // Hold On… busy dialog acceptance: stays open until the editor quits.
            Popups.BusyDialog.Show(Loc.Get("progress.busy.default"));
        }
    }

    private static bool _envDemoStarted;

    [MenuItem("Tools/Progress/Demo Import Batch (5s, status bar)", priority: 0, Icon = EditorIcons.CircleNotch)]
    static void DemoImportBatch() => Start(
        Loc.Get("import.progress.assets"), EditorProgressKind.Import,
        total: 50, durationSeconds: 5f, EditorProgressVisibility.StatusBarOnly);

    [MenuItem("Tools/Progress/Demo Corner Card Import (6s, popup)", priority: 5, Icon = EditorIcons.FileImport)]
    static void DemoCornerCardImport()
    {
        ProgressHandle handle = Start(
            Loc.Get("import.progress.assets"), EditorProgressKind.Import,
            total: 40, durationSeconds: 6f, EditorProgressVisibility.ImmediatePopup);
        EditorProgress.RegisterCancel(handle, StopAll);
    }

    [MenuItem("Tools/Progress/Demo Busy Dialog (3s)", priority: 6)]
    static void DemoBusyDialog() => RunBusyDemo().Forget();

    private static async XTaskVoid RunBusyDemo()
    {
        Popups.BusyDialog.Show(Loc.Get("progress.busy.default"));
        await XTask.Delay(TimeSpan.FromSeconds(3), DelayType.Realtime);
        Popups.BusyDialog.Close();
    }

    [MenuItem("Tools/Progress/Demo Indeterminate Task (3s, status bar)", priority: 10)]
    static void DemoIndeterminate() => Start(
        Loc.Get("progress.demo.indeterminate"), EditorProgressKind.Custom,
        total: 0, durationSeconds: 3f, EditorProgressVisibility.StatusBarOnly);

    [MenuItem("Tools/Progress/Demo Cancellable Task (10s, status bar)", priority: 20)]
    static void DemoCancellable()
    {
        ProgressHandle handle = Start(
            Loc.Get("progress.demo.cancellable"), EditorProgressKind.Custom,
            total: 200, durationSeconds: 10f, EditorProgressVisibility.StatusBarOnly);
        EditorProgress.RegisterCancel(handle, StopAll);
    }

    [MenuItem("Tools/Progress/Stop All Demo Tasks", priority: 30)]
    static void StopAllMenu() => StopAll();

    /// <summary>
    /// Acceptance automation (interactive use; automated runs use <see cref="TryStartFromEnv"/> +
    /// <c>--panel-screenshot</c>): runs a 6s import demo — the main loop presents real frames while
    /// the coroutine awaits, so the status cell is live on screen — captures the whole window at
    /// ~1.5s to <c>XENGINE_WINDOW_SHOT_OUT</c> or <c>Temp/progress-acceptance.png</c>, then stops.
    /// </summary>
    [MenuItem("Tools/Progress/Acceptance: Import demo + window screenshot", priority: 100)]
    static void AcceptanceImportShot() => RunAcceptanceShot().Forget();

    private static async XTaskVoid RunAcceptanceShot()
    {
        Start(
            Loc.Get("import.progress.assets"), EditorProgressKind.Import,
            total: 50, durationSeconds: 6f, EditorProgressVisibility.StatusBarOnly);

        await XTask.Delay(TimeSpan.FromSeconds(1.5), DelayType.Realtime);

        string path = Environment.GetEnvironmentVariable("XENGINE_WINDOW_SHOT_OUT")
                      ?? "Temp/progress-acceptance.png";
        string? captured = EditorWindowCapture.Capture(path);
        Runtime.Debug.Log(captured != null
            ? $"[ProgressDemo] acceptance window shot: {captured}"
            : "[ProgressDemo] acceptance window shot FAILED");
        StopAll();
    }

    private static ProgressHandle Start(string title, EditorProgressKind kind,
        int total, float durationSeconds, EditorProgressVisibility visibility)
    {
        ProgressHandle handle = EditorProgress.Start(kind, title, total, visibility);
        Demo demo = new()
        {
            Handle = handle,
            Total = total,
            StepsPerSecond = durationSeconds > 0f && total > 0 ? total / durationSeconds : 0f,
            DurationSeconds = durationSeconds,
        };
        Active.Add(demo);
        return handle;
    }

    private static void StopAll()
    {
        for (int i = 0; i < Active.Count; i++)
            EditorProgress.Finish(Active[i].Handle, success: false);
        Active.Clear();
    }
}
