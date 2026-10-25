// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Core.Progress;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI.Popups;

/// <summary>
/// Unity-style corner progress window: a small card anchored bottom-right above the status bar for
/// the top popup-worthy <see cref="EditorProgress"/> task (AutoPopup tasks that outlived their
/// delay, or ImmediatePopup tasks like the startup import). While shown, the invisible backdrop
/// absorbs input below it — Unity likewise blocks editing during an import batch. Self-closes when
/// no popup task remains. Managed as a single <see cref="CustomDrawModal"/> pushed/popped from
/// EndGui so the standard modal machinery (layer, input catching, forced frames) applies.
/// </summary>
internal static class ProgressCornerCard
{
    private const float CardWidth = 340f;
    private const float BodyHeight = 118f; // head + detail + bar + count rows
    private const float CancelRowHeight = 44f;
    private const float AnchorMargin = 16f;

    private static CustomDrawModal? s_modal;

    private static float s_shownAt = -1f;
    private static bool s_cardShotDone;

    /// <summary>Pushes/pops the card to match the current snapshot; call from EndGui before Origami.EndFrame.</summary>
    public static void Tick()
    {
        if (EditorProgress.Snapshot().TopPopup == null)
        {
            CloseIfOpen();
            return;
        }

        if (s_modal == null)
        {
            s_modal = new CustomDrawModal(DrawCard)
            {
                CloseOnEscape = false,
                CloseOnBackdrop = false,
                // Click-catcher only: no dim, but editing below is blocked (Unity import behavior).
                BackdropAlpha = 0,
            };
            Modal.Push(s_modal);
            s_shownAt = XEngine.Runtime.Time.UnscaledTotalTime;
            s_cardShotDone = false;
        }

        // Acceptance hook: capture the live card ~1.2s after it first appears.
        if (!s_cardShotDone
            && Environment.GetEnvironmentVariable("XENGINE_CARD_SHOT_OUT") is { Length: > 0 } shotPath
            && XEngine.Runtime.Time.UnscaledTotalTime - s_shownAt >= 1.2f)
        {
            s_cardShotDone = true;
            string? shot = Core.EditorWindowCapture.Capture(shotPath);
            XEngine.Runtime.Debug.Log(shot != null
                ? $"Progress card shot: {shot}"
                : "Progress card shot FAILED");
        }
    }

    public static void CloseIfOpen()
    {
        if (s_modal != null)
        {
            Modal.Remove(s_modal);
            s_modal = null;
        }
    }

    private static void DrawCard(Paper paper, int layer, int stackIndex)
    {
        EditorProgressTaskView? task = EditorProgress.Snapshot().TopPopup;
        if (task == null)
            return;

        var theme = Origami.Current;
        if (theme.Font == null)
            return;

        bool cancellable = task.CanCancel && !task.CancelRequested;
        float cardH = BodyHeight + (cancellable ? CancelRowHeight : 0f);
        float screenW = (float)paper.ScreenRect.Size.X;
        float screenH = (float)paper.ScreenRect.Size.Y;

        var card = paper.Column($"pcc_{stackIndex}")
            .PositionType(PositionType.SelfDirected)
            .Position(screenW - CardWidth - AnchorMargin, screenH - EditorTheme.StatusBarHeight - cardH - AnchorMargin)
            .Width(CardWidth).Height(cardH)
            .BackgroundColor(theme.Popover)
            .BorderColor(theme.BorderStrong).BorderWidth(1)
            .Rounded(12f).Clip()
            .DropShadow(0, 18, 48, 0, Color.FromArgb(150, 0, 0, 0))
            .Layer(layer)
            .StopEventPropagation();
        using (card.Enter())
        {
            // Head: kind glyph + title. Unity's import window has no close button — it stays until
            // the batch finishes or is cancelled.
            float headH = theme.Metrics.FontSize + 16f;
            using (paper.Row($"pcc_h{stackIndex}").Width(UnitValue.Stretch()).Height(headH)
                .BackgroundColor(theme.Glass).RoundedTop(12f).Padding(11, 11, 0, 0).RowBetween(8).Enter())
            {
                paper.Box($"pcc_hi{stackIndex}").Width(15).Height(headH).IsNotInteractable()
                    .Text(IconGlyph(task.Kind), EditorTheme.FontIconSolid)
                    .TextColor(theme.Ink.C400).FontSize(12f).Alignment(PaperUI.TextAlignment.MiddleCenter);
                paper.Box($"pcc_ht{stackIndex}").Width(UnitValue.Stretch()).Height(headH)
                    .Text(task.Title, theme.SemiBold ?? theme.Font).TextColor(theme.Ink.C500)
                    .FontSize(theme.Metrics.FontSize).Alignment(PaperUI.TextAlignment.MiddleLeft).IsNotInteractable();
            }

            using (paper.Column($"pcc_b{stackIndex}").Width(UnitValue.Stretch()).Height(UnitValue.Stretch())
                .Padding(12, 12, 10, 10).RowBetween(8).Enter())
            {
                paper.Box($"pcc_d{stackIndex}").Width(UnitValue.Stretch()).Height(18).IsNotInteractable()
                    .Text(string.IsNullOrEmpty(task.Detail) ? Loc.Get("progress.popover.running") : task.Detail,
                        theme.Font).TextColor(theme.Ink.C300).FontSize(theme.Metrics.FontSize - 1f)
                    .Alignment(PaperUI.TextAlignment.MiddleLeft).TextTruncate();

                var bar = Origami.ProgressBar(paper, $"pcc_bar{stackIndex}", task.Fraction);
                if (task.IsIndeterminate)
                    bar.Indeterminate();
                bar.Show();

                string status = task.IsIndeterminate
                    ? Loc.Get("progress.popover.running")
                    : Loc.Get("progress.count", new { done = task.Current, total = task.Total });
                paper.Box($"pcc_c{stackIndex}").Width(UnitValue.Stretch()).Height(18).IsNotInteractable()
                    .Text(status, theme.Font).TextColor(theme.Ink.C300)
                    .FontSize(theme.Metrics.FontSize - 1f).Alignment(PaperUI.TextAlignment.MiddleRight);

                if (cancellable)
                {
                    ProgressHandle handle = new(task.Id);
                    Origami.Button(paper, $"pcc_cancel{stackIndex}", Loc.Get("progress.popover.cancel"),
                            () => EditorProgress.RequestCancel(handle))
                        .Danger().Width(110f).Show();
                }
            }
        }
    }

    private static string IconGlyph(EditorProgressKind kind) => kind switch
    {
        EditorProgressKind.Import => EditorIcons.FileImport,
        EditorProgressKind.Compile or EditorProgressKind.Reload => EditorIcons.FileCode,
        EditorProgressKind.ProjectOpen => EditorIcons.FolderOpen,
        _ => EditorIcons.Spinner,
    };
}
