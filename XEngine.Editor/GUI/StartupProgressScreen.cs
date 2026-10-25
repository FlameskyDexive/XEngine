// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Rosetta;
using XEngine.Runtime;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI;

/// <summary>
/// Unity 6-style embedded open-progress screen: while a <c>--project</c> launch runs its frame-paced
/// open steps (ProjectOpenProgress), the editor window shows this full-cover screen — engine
/// branding, project name, the step list with the current step highlighted, the live detail line
/// and an overall progress bar — instead of sitting blank and unresponsive. The window keeps
/// pumping OS messages between steps, so it can be moved throughout.
/// </summary>
internal static class StartupProgressScreen
{
    private static NebulaBackground? s_nebula;

    public static void Draw(Paper paper, float w, float h)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null)
            return;

        float dt = (float)Time.UnscaledDeltaTime;

        // Same animated backdrop the editor chrome uses, so the window already reads as the editor.
        s_nebula ??= new NebulaBackground(paper);
        NebulaBackground.DrawEditorBackground(paper, s_nebula, "sps_bg", w, h, dt);

        // Block input — nothing is openable/clickable until the steps finish.
        paper.Box("sps_blocker")
            .PositionType(PositionType.SelfDirected).Position(0, 0).Size(w, h)
            .OnClick(0, (_, _) => { });

        const float cardW = 540f;
        float cardX = (w - cardW) / 2f;
        float cardY = MathF.Max(h * 0.26f, 120f);

        var card = paper.Column("sps_card")
            .PositionType(PositionType.SelfDirected)
            .Position(cardX, cardY)
            .Width(cardW).Height(UnitValue.Auto)
            .BackgroundColor(Color.FromArgb(235, 22, 22, 22))
            .BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Rounded(14f).Clip()
            .DropShadow(0, 28, 80, 0, Color.FromArgb(170, 0, 0, 0));

        using (card.Enter())
        using (paper.Column("sps_body").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
            .Padding(26, 26, 22, 22).RowBetween(14).Enter())
        {
            // Branding + project name.
            using (paper.Row("sps_brand").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .Padding(0, 0, 2, 12).Enter())
            {
                paper.Box("sps_logo").Width(120).Height(22).IsNotInteractable()
                    .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, rr) =>
                        EditorBranding.Draw(canvas, new Vector.Rect(
                            (float)rr.Min.X, (float)rr.Min.Y, (float)rr.Max.X, (float)rr.Max.Y))));
            }

            string projectName = Projects.Project.Current?.Name ?? string.Empty;
            var semiBold = EditorTheme.FontSemiBold ?? font;
            paper.Box("sps_project").Width(UnitValue.Stretch()).Height(26).IsNotInteractable()
                .Text(projectName, semiBold).TextColor(EditorTheme.Ink400)
                .FontSize(19f).Alignment(PaperUI.TextAlignment.MiddleLeft);
            Origami.Label(paper, "sps_opening", Loc.Get("progress.startup.title"))
                .TextColor(EditorTheme.Ink300).Show();

            // Step list: done = check, current = spinner, pending = dim.
            string[] stepKeys = ProjectOpenProgress.StepKeys;
            int current = ProjectOpenProgress.DisplayedStep;
            for (int i = 0; i < stepKeys.Length; i++)
            {
                using (paper.Row($"sps_step{i}").Width(UnitValue.Stretch()).Height(20)
                    .Padding(0, 2, 0, 0).RowBetween(8).Enter())
                {
                    string glyph = i < current ? EditorIcons.Check
                                  : i == current ? EditorIcons.Spinner
                                  : EditorIcons.Circle;
                    var color = i < current ? EditorTheme.Green400
                              : i == current ? EditorTheme.Accent
                              : EditorTheme.Ink300;
                    paper.Box($"sps_stepi{i}").Width(16).Height(20).IsNotInteractable()
                        .Text(glyph, EditorTheme.FontIconSolid)
                        .TextColor(color).FontSize(12f).Alignment(PaperUI.TextAlignment.MiddleCenter);
                    Origami.Label(paper, $"sps_stept{i}", Loc.Get(stepKeys[i]))
                        .TextColor(i <= current ? EditorTheme.Ink400 : EditorTheme.Ink300)
                        .Show();
                }
            }

            // Live detail line (e.g. "Compiling Zonezero.Game (3/18)…" / importing file).
            Origami.Label(paper, "sps_detail",
                    string.IsNullOrEmpty(ProjectOpenProgress.Detail) ? " " : ProjectOpenProgress.Detail)
                .TextColor(EditorTheme.Ink300).Show();

            Origami.ProgressBar(paper, "sps_bar", ProjectOpenProgress.Fraction)
                .Striped().Thickness(8).Show();
        }
    }
}
