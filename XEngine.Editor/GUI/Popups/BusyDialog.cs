// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

namespace XEngine.Editor.GUI.Popups;

/// <summary>
/// Unity's "Hold On…" centered busy dialog: an indeterminate progress card shown around synchronous
/// main-thread phases (script reload, big metadata saves) so the editor communicates why it is
/// momentarily busy. Not dismissable — the code that shows it must call <see cref="Close"/> when the
/// phase ends. Use sparingly: long work should be time-sliced or backgrounded instead (see
/// EditorProgress); this is the fallback for phases that cannot be sliced yet.
/// </summary>
internal static class BusyDialog
{
    private static DialogModal? s_modal;
    private static string s_message = "";

    public static bool IsOpen => s_modal != null;

    /// <summary>Shows (or updates) the busy dialog. Safe to call repeatedly with a fresh message.</summary>
    public static void Show(string message)
    {
        if (s_modal != null)
        {
            s_message = message ?? string.Empty;
            return;
        }

        s_message = message ?? string.Empty;
        s_modal = new DialogModal
        {
            Title = Loc.Get("progress.busy.title"),
            DrawContent = DrawContent,
            Width = 360,
            CloseOnEscape = false,
            CloseOnBackdrop = false,
            Icon = EditorIcons.Spinner_I,
        };
        Modal.Push(s_modal);
    }

    public static void Close()
    {
        if (s_modal != null)
        {
            Modal.Remove(s_modal);
            s_modal = null;
        }
    }

    private static void DrawContent(Paper paper)
    {
        var font = EditorTheme.DefaultFont;

        using (paper.Column("busy_body").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                   .Padding(14, 12, 8, 8).RowBetween(10).Enter())
        {
            Origami.Spinner(paper, "busy_spin").Size(SpinnerSize.SM).Show();

            Origami.Label(paper, "busy_msg",
                    string.IsNullOrEmpty(s_message) ? Loc.Get("progress.busy.default") : s_message)
                .Show();

            Origami.ProgressBar(paper, "busy_bar", 0f).Indeterminate().Show();
        }
    }
}
