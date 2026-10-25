// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core.Progress;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

namespace XEngine.Editor.GUI;

/// <summary>
/// Unity-style background-task cell for the editor status bar: spinner + task title + mini progress
/// bar for the longest-running <see cref="EditorProgress"/> task, with a leading divider so the cell
/// reads as its own column between the scene and stats columns. Clicking opens the task popover
/// (Unity 6's global progress dropdown). Draws nothing — zero element cost — when no task runs.
/// </summary>
internal static class StatusBarProgressCell
{
    public static void Draw(Paper paper)
    {
        EditorProgressSnapshot snapshot = EditorProgress.Snapshot();
        if (!snapshot.AnyRunning)
            return;

        var font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        float sh = EditorTheme.StatusBarHeight;
        float fs = EditorTheme.FontSizeSmall;
        EditorProgressTaskView task = snapshot.Primary!;

        string label = task.Title;
        if (!task.IsIndeterminate)
            label += " " + Loc.Get("progress.count", new { done = task.Current, total = task.Total });

        var row = paper.Row("sb_progress")
            .Width(UnitValue.Auto).Height(sh).Margin(0, 8, 0, 0).Padding(6, 6, 0, 0)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Tooltip(Loc.Get("progress.status.tooltip"))
            .OnClick(_ => ShowTasksPopover());
        using (row.Enter())
        {
            Origami.Spinner(paper, "sb_progress_spin").Size(SpinnerSize.XS).Show();

            paper.Box("sb_progress_t")
                .Width(UnitValue.Auto).Height(sh).Margin(0, 4, UnitValue.StretchOne, UnitValue.StretchOne)
                .IsNotInteractable()
                .Text(label, font).TextColor(EditorTheme.Ink400).FontSize(fs)
                .Alignment(PaperUI.TextAlignment.MiddleLeft);

            if (!task.IsIndeterminate)
                Origami.ProgressBar(paper, "sb_progress_bar", task.Fraction).Thin().Width(64).Show();
        }
    }

    /// <summary>The task popover (Unity's status-bar global progress dropdown): every running task
    /// with its status/detail and a cancel entry when the producer registered a cancel callback.</summary>
    private static void ShowTasksPopover()
    {
        ContextMenu.ShowAtPointer(build =>
        {
            build.Title(Loc.Get("progress.popover.title"), icon: EditorIcons.Spinner);

            EditorProgressSnapshot snapshot = EditorProgress.Snapshot();
            EditorProgressTaskView[] tasks = snapshot.Tasks;
            for (int i = 0; i < tasks.Length; i++)
            {
                EditorProgressTaskView t = tasks[i];
                if (i > 0)
                    build.Separator();

                string status = t.IsIndeterminate
                    ? Loc.Get("progress.popover.running")
                    : Loc.Get("progress.count", new { done = t.Current, total = t.Total });
                build.Item($"{t.Title} — {status}", static () => { }, enabled: false);

                if (!string.IsNullOrEmpty(t.Detail))
                    build.Item("    " + t.Detail, static () => { }, enabled: false);

                if (t.CanCancel)
                {
                    ProgressHandle handle = new(t.Id);
                    build.Item(Loc.Get("progress.popover.cancel"), () => EditorProgress.RequestCancel(handle));
                }
            }
        });
    }
}
