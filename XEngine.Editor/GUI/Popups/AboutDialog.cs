// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using System;

using XEngine.Editor.Core;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.GUI.Popups;

/// <summary>
/// The menu-bar <c>About</c> dialog: engine identity plus a compact capability tour so new
/// team members can discover what the editor ships with without reading the docs first.
/// </summary>
public static class AboutDialog
{
    private static DialogModal? s_modal;

    public static void Open()
    {
        s_modal = new DialogModal
        {
            Title = Loc.Get("about.title"),
            DrawContent = DrawContent,
            Width = 640,
            Icon = EditorIcons.Info_I,
        };
        Modal.Push(s_modal);
    }

    private static void DrawContent(Paper paper)
    {
        var font = EditorTheme.DefaultFont;

        using (paper.Column("about_body").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                   .Padding(18, 16, 10, 12).RowBetween(10).Enter())
        {
            // Identity block: real raster logo (same embedded asset as the launcher splash)
            // + product name + current branch. Text cells stretch to the logo's height and
            // middle-align so baseline never drifts above/below the mark's optical center.
            using (paper.Row("about_head").Width(UnitValue.Stretch()).Height(48).RowBetween(12).Enter())
            {
                paper.Box("about_logo_img").Width(44).Height(48)
                    .OnPostLayout((handle, r) => paper.Draw(ref handle, (canvas, rect) =>
                        EditorBranding.Draw(canvas, rect)));
                var nameFont = EditorTheme.DefaultFont;
                paper.Box("about_name_t").Width(UnitValue.Auto).Height(UnitValue.Stretch()).IsNotInteractable()
                    .Text("XEngine", nameFont).FontSize(24)
                    .Alignment(TextAlignment.MiddleLeft);
                if (!string.IsNullOrEmpty(GitInfo.Branch))
                    paper.Box("about_branch_t").Width(UnitValue.Auto).Height(UnitValue.Stretch())
                        .Margin(0, 0, 0, 6).IsNotInteractable()
                        .Text(GitInfo.Branch, nameFont).TextColor(EditorTheme.InkFaint)
                        .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.BottomLeft);
            }

            Origami.Label(paper, "about_tagline", Loc.Get("about.tagline"))
                .TextColor(EditorTheme.InkFaint).Show();

            FeatureBlock(paper, "render", Loc.Get("about.render.title"), Loc.Get("about.render.body"));
            FeatureBlock(paper, "anim", Loc.Get("about.anim.title"), Loc.Get("about.anim.body"));
            FeatureBlock(paper, "sim", Loc.Get("about.sim.title"), Loc.Get("about.sim.body"));
            FeatureBlock(paper, "tooling", Loc.Get("about.tooling.title"), Loc.Get("about.tooling.body"));
            FeatureBlock(paper, "pipeline", Loc.Get("about.pipeline.title"), Loc.Get("about.pipeline.body"));

            paper.Box("about_spacer").Height(2);
            Origami.Label(paper, "about_legal",
                Loc.Get("about.legal", new { year = DateTime.Now.Year }))
                .TextColor(EditorTheme.InkFaint).FontSize(EditorTheme.FontSize - 1).Show();
        }
    }

    private static void FeatureBlock(Paper paper, string id, string title, string body)
    {
        using (paper.Column($"about_f_{id}").Width(UnitValue.Stretch()).Height(UnitValue.Auto).Enter())
        {
            Origami.Label(paper, $"about_f_{id}_t", $"●  {title}")
                .TextColor(EditorTheme.Blue400).Show();
            Origami.Label(paper, $"about_f_{id}_b", body)
                .TextColor(EditorTheme.InkFaint).Show();
        }
    }
}
